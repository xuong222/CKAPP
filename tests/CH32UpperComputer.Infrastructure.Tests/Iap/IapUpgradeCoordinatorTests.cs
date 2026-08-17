using System.Net;

using CH32UpperComputer.Core.Iap;
using CH32UpperComputer.Infrastructure.Coordination;
using CH32UpperComputer.Infrastructure.Iap;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Iap
{
    /// <summary>
    /// 验证完整升级在断线和 END 不确定边界上的重连规则。
    /// </summary>
    [TestFixture]
    public sealed class IapUpgradeCoordinatorTests
    {
        /// <summary>
        /// 验证 DATA 响应前断线会新建连接、从 HELLO 和 BEGIN 重启且不会续传旧 offset。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_DataDisconnect_RestartsEntireUpgrade()
        {
            BootloaderSimulator simulator = new()
            {
                DataDisconnectsRemaining = 1,
            };
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(1500);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            BootloaderSimulatorRequest[] beginRequests = simulator.Requests
                .Where(request => request.Command == IapCommand.Begin)
                .ToArray();
            BootloaderSimulatorRequest[] zeroOffsetDataRequests = simulator.Requests
                .Where(request =>
                    request.Command == IapCommand.Data &&
                    request.Offset == 0U)
                .ToArray();
            BootloaderSimulatorRequest secondConnectionFirstRequest = simulator.Requests
                .First(request => request.ConnectionId == 2);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    fixture.Coordinator.State,
                    Is.EqualTo(IapUpgradeState.UpgradeSucceeded));
                Assert.That(beginRequests, Has.Length.EqualTo(2));
                Assert.That(zeroOffsetDataRequests, Has.Length.EqualTo(2));
                Assert.That(
                    secondConnectionFirstRequest.Command,
                    Is.EqualTo(IapCommand.Hello));
                Assert.That(secondConnectionFirstRequest.Sequence, Is.EqualTo(1U));
            }));
        }

        /// <summary>
        /// 验证 END 超时后关闭原连接，仅在 sequence 从一开始的新连接执行 HELLO 验证。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_EndTimeout_UsesNewVerificationConnection()
        {
            BootloaderSimulator simulator = new()
            {
                EndTimeoutsRemaining = 1,
            };
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            BootloaderSimulatorRequest endRequest = simulator.Requests
                .Single(request => request.Command == IapCommand.End);
            BootloaderSimulatorRequest verificationHello = simulator.Requests
                .Last(request => request.Command == IapCommand.Hello);
            int beginCount = simulator.Requests.Count(
                request => request.Command == IapCommand.Begin);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    fixture.Coordinator.State,
                    Is.EqualTo(IapUpgradeState.UpgradeSucceeded));
                Assert.That(verificationHello.ConnectionId, Is.Not.EqualTo(endRequest.ConnectionId));
                Assert.That(verificationHello.Sequence, Is.EqualTo(1U));
                Assert.That(beginCount, Is.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证正常 END ACK 后优先在原连接发送下一 sequence 的 HELLO。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_EndAck_ReusesConnectionForHello()
        {
            BootloaderSimulator simulator = new();
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            BootloaderSimulatorRequest end = simulator.Requests
                .Single(request => request.Command == IapCommand.End);
            BootloaderSimulatorRequest verificationHello = simulator.Requests
                .Last(request => request.Command == IapCommand.Hello);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(verificationHello.ConnectionId, Is.EqualTo(end.ConnectionId));
                Assert.That(verificationHello.Sequence, Is.EqualTo(end.Sequence + 1U));
                Assert.That(
                    fixture.Coordinator.State,
                    Is.EqualTo(IapUpgradeState.UpgradeSucceeded));
            }));
        }

        /// <summary>
        /// 验证 DATA CRC NACK 允许三次额外重发，同一 offset 最多发送四次。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_DataCrcNack_RetriesCurrentChunkThreeTimes()
        {
            BootloaderSimulator simulator = new()
            {
                DataCrcNacksRemaining = 3,
            };
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            BootloaderSimulatorRequest[] dataRequests = simulator.Requests
                .Where(request => request.Command == IapCommand.Data)
                .ToArray();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(dataRequests, Has.Length.EqualTo(4));
                Assert.That(dataRequests.All(request => request.Offset == 0U), Is.True);
                Assert.That(
                    fixture.Coordinator.State,
                    Is.EqualTo(IapUpgradeState.UpgradeSucceeded));
            }));
        }

        /// <summary>
        /// 验证连续 DATA 断线只建立三次完整升级尝试，耗尽后明确失败。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_DataDisconnect_StopsAfterThreeFullAttempts()
        {
            BootloaderSimulator simulator = new()
            {
                DataDisconnectsRemaining = 3,
            };
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            int beginCount = simulator.Requests.Count(
                request => request.Command == IapCommand.Begin);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(beginCount, Is.EqualTo(3));
                Assert.That(fixture.Coordinator.State, Is.EqualTo(IapUpgradeState.Failed));
            }));
        }

        /// <summary>
        /// 验证 END 后明确不匹配会先重连 HELLO，再消耗下一次完整 BEGIN。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_VerificationMismatch_ReconnectsBeforeNextBegin()
        {
            BootloaderSimulator simulator = new()
            {
                VerificationMismatchesRemaining = 1,
            };
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            BootloaderSimulatorRequest[] beginRequests = simulator.Requests
                .Where(request => request.Command == IapCommand.Begin)
                .ToArray();
            BootloaderSimulatorRequest firstRequestOnSecondConnection = simulator.Requests
                .First(request => request.ConnectionId == 2);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(beginRequests, Has.Length.EqualTo(2));
                Assert.That(
                    firstRequestOnSecondConnection.Command,
                    Is.EqualTo(IapCommand.Hello));
                Assert.That(firstRequestOnSecondConnection.Sequence, Is.EqualTo(1U));
                Assert.That(
                    fixture.Coordinator.State,
                    Is.EqualTo(IapUpgradeState.UpgradeSucceeded));
            }));
        }

        /// <summary>
        /// 验证 RUN ACK、ACK 前超时和明确 NACK 分别进入独立三种结果。
        /// </summary>
        /// <param name="behavior">模拟 Bootloader 的 RUN 行为。</param>
        /// <param name="expectedState">期望独立 RUN 状态。</param>
        [TestCase(
            BootloaderSimulatorRunBehavior.Acknowledge,
            IapRunState.Confirmed)]
        [TestCase(
            BootloaderSimulatorRunBehavior.Timeout,
            IapRunState.Uncertain)]
        [TestCase(
            BootloaderSimulatorRunBehavior.Reject,
            IapRunState.Rejected)]
        public async Task RunApplicationAsync_MapsIndependentRunResult(
            BootloaderSimulatorRunBehavior behavior,
            IapRunState expectedState)
        {
            BootloaderSimulator simulator = new();
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);
            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);
            simulator.RunBehavior = behavior;

            await fixture.Coordinator.RunApplicationAsync(
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(fixture.Coordinator.RunState, Is.EqualTo(expectedState));
                Assert.That(
                    fixture.Coordinator.State,
                    Is.EqualTo(IapUpgradeState.UpgradeSucceeded));
            }));
        }

        /// <summary>
        /// 验证等待 DATA ACK 时取消会先废弃可能残留响应的连接，再收敛为 Cancelled 并释放应用门。
        /// </summary>
        [Test]
        public async Task CancelAsync_WhileWaitingDataResponse_AbortsAndReleasesGate()
        {
            BootloaderSimulator simulator = new();
            BootloaderSimulatorPause pause = simulator.PauseNextDataResponse();
            ApplicationOperationGate operationGate = new();
            await using CoordinatorFixture fixture = new(simulator, operationGate);
            FirmwareImage image = CreateFirmwareImage(64);
            Task upgrade = fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);
            await pause.Entered.WaitAsync(TimeSpan.FromSeconds(1));

            await fixture.Coordinator.CancelAsync(CancellationToken.None);
            await upgrade.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    fixture.Coordinator.State,
                    Is.EqualTo(IapUpgradeState.Cancelled));
                Assert.That(simulator.IsConnected, Is.False);
                Assert.That(operationGate.IsIapActive, Is.False);
                Assert.That(operationGate.ActiveModbusOperationCount, Is.Zero);
            }));
        }

        /// <summary>
        /// 验证设备 App 基址不符时立即停止，绝不发送会擦除 App 区的 BEGIN。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_AppBaseMismatch_FailsBeforeBegin()
        {
            BootloaderSimulator simulator = new()
            {
                HelloAppBase = 0x08010000U,
            };
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(fixture.Coordinator.State, Is.EqualTo(IapUpgradeState.Failed));
                Assert.That(
                    simulator.Requests.Any(request => request.Command == IapCommand.Begin),
                    Is.False);
            }));
        }

        /// <summary>
        /// 验证设备声明容量小于镜像时立即停止，绝不发送 BEGIN。
        /// </summary>
        [Test]
        public async Task StartUpgradeAsync_DeviceCapacityTooSmall_FailsBeforeBegin()
        {
            BootloaderSimulator simulator = new()
            {
                HelloAppMaxSize = 32U,
            };
            await using CoordinatorFixture fixture = new(simulator);
            FirmwareImage image = CreateFirmwareImage(64);

            await fixture.Coordinator.StartUpgradeAsync(
                image,
                IPAddress.Loopback,
                5000,
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(fixture.Coordinator.State, Is.EqualTo(IapUpgradeState.Failed));
                Assert.That(
                    simulator.Requests.Any(request => request.Command == IapCommand.Begin),
                    Is.False);
            }));
        }

        /// <summary>
        /// 创建具有确定性内容和 CRC32 的临时固件快照。
        /// </summary>
        /// <param name="length">固件字节数。</param>
        /// <returns>不依赖真实磁盘读取的固件对象。</returns>
        private static FirmwareImage CreateFirmwareImage(int length)
        {
            byte[] bytes = Enumerable.Range(0, length)
                .Select(index => checked((byte)(index % 251)))
                .ToArray();
            return new FirmwareImage(
                Path.Combine(Path.GetTempPath(), $"iap-test-{length}.bin"),
                bytes);
        }

        /// <summary>
        /// 组合并按正确顺序释放一次协调器测试所需对象。
        /// </summary>
        private sealed class CoordinatorFixture : IAsyncDisposable
        {
            /// <summary>
            /// 测试协议客户端。
            /// </summary>
            private readonly IapProtocolClient client;

            /// <summary>
            /// 初始化使用指定模拟 Bootloader 的完整协调器。
            /// </summary>
            /// <param name="simulator">接收并响应 IAP 请求的模拟器。</param>
            /// <param name="operationGate">可选的共享应用操作门。</param>
            public CoordinatorFixture(
                BootloaderSimulator simulator,
                ApplicationOperationGate? operationGate = null)
            {
                IapCommunicationLogService logService = new(TimeProvider.System);
                client = new IapProtocolClient(
                    simulator,
                    logService,
                    TimeProvider.System);
                Coordinator = new IapUpgradeCoordinator(
                    client,
                    logService,
                    operationGate ?? new ApplicationOperationGate(),
                    TimeProvider.System);
            }

            /// <summary>
            /// 获取待验证升级协调器。
            /// </summary>
            public IapUpgradeCoordinator Coordinator { get; }

            /// <summary>
            /// 释放协调器及其协议客户端和模拟传输。
            /// </summary>
            /// <returns>全部资源已经释放后的任务。</returns>
            public async ValueTask DisposeAsync()
            {
                await Coordinator.DisposeAsync();
            }
        }
    }
}
