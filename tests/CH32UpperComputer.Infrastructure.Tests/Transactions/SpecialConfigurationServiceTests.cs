using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Transactions
{
    /// <summary>
    /// 验证地址、波特率、恢复出厂和未知地址查询组成的完整安全配置流程。
    /// </summary>
    [TestFixture]
    public sealed class SpecialConfigurationServiceTests
    {
        /// <summary>
        /// 验证地址修改会先永久停止定时发送，并且仅在 0x06 完整回显后更新本地地址。
        /// </summary>
        [Test]
        public async Task ChangeSlaveAddress_ConfirmedEcho_StopsPeriodicAndUpdatesAddress()
        {
            await using ConfigurationHarness harness =
                await ConfigurationHarness.CreateAsync();
            harness.PeriodicService.Configure(
                CreateReadTransaction(harness.InitialSlaveAddress, harness.InitialSettings),
                TimeSpan.FromMilliseconds(100));
            harness.PeriodicService.Start();
            Task waitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);

            Task<ConfigurationChangeResult> operation = harness.ConfigurationService
                .ChangeSlaveAddressAsync(9, CancellationToken.None)
                .AsTask();
            await waitingResponse;
            byte[] requestFrame = harness.Transport.WrittenFrames.Single().ToArray();
            await harness.Transport.InjectReceiveAsync(requestFrame);
            ConfigurationChangeResult result = await operation;
            harness.TimeProvider.Advance(TimeSpan.FromSeconds(2));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Status, Is.EqualTo(ConfigurationChangeStatus.Succeeded));
                Assert.That(result.PreviousConfiguration.SlaveAddress, Is.EqualTo(7));
                Assert.That(result.LocalConfiguration.SlaveAddress, Is.EqualTo(9));
                Assert.That(
                    harness.ConfigurationService.CurrentConfiguration.SlaveAddress,
                    Is.EqualTo(9));
                Assert.That(harness.PeriodicService.IsRunning, Is.False);
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
                Assert.That(
                    requestFrame[..6],
                    Is.EqualTo(new byte[] { 0x07, 0x06, 0x00, 0x00, 0x00, 0x09 }));
            }));
        }

        /// <summary>
        /// 验证地址写请求超时后不会猜测新地址，并向界面提供明确的旧、新配置恢复选项。
        /// </summary>
        [Test]
        public async Task ChangeSlaveAddress_ResponseLost_ReturnsConfigurationUncertain()
        {
            await using ConfigurationHarness harness =
                await ConfigurationHarness.CreateAsync();
            Task waitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            Task<ConfigurationChangeResult> operation = harness.ConfigurationService
                .ChangeSlaveAddressAsync(9, CancellationToken.None)
                .AsTask();
            await waitingResponse;

            harness.TimeProvider.Advance(harness.InitialSettings.ResponseTimeout);
            ConfigurationChangeResult result = await operation;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    result.Status,
                    Is.EqualTo(ConfigurationChangeStatus.ConfigurationUncertain));
                Assert.That(result.IsConfigurationUncertain, Is.True);
                Assert.That(result.PreviousConfiguration.SlaveAddress, Is.EqualTo(7));
                Assert.That(result.ProposedConfiguration!.SlaveAddress, Is.EqualTo(9));
                Assert.That(result.LocalConfiguration.SlaveAddress, Is.EqualTo(7));
                Assert.That(
                    harness.ConfigurationService.CurrentConfiguration.SlaveAddress,
                    Is.EqualTo(7));
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证波特率与恢复出厂写入都必须先取得完整回显，再按各自独立规则重连。
        /// </summary>
        /// <param name="changeKind">需要执行的独立重连流程。</param>
        [TestCase(ConfirmedReconnectKind.BaudRate)]
        [TestCase(ConfirmedReconnectKind.FactoryReset)]
        public async Task ConfirmedConfigurationWrite_ReopensWithExpectedConfiguration(
            ConfirmedReconnectKind changeKind)
        {
            SerialSettings initialSettings = changeKind == ConfirmedReconnectKind.FactoryReset
                ? CreateSettings(
                    19200,
                    7,
                    System.IO.Ports.Parity.Even,
                    System.IO.Ports.StopBits.Two)
                : CreateSettings(9600);
            await using ConfigurationHarness harness =
                await ConfigurationHarness.CreateAsync(initialSettings);
            Task waitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            Task<ConfigurationChangeResult> operation;

            if (changeKind == ConfirmedReconnectKind.BaudRate)
            {
                operation = harness.ConfigurationService
                    .ChangeBaudRateAsync(19200, CancellationToken.None)
                    .AsTask();
            }
            else
            {
                operation = harness.ConfigurationService
                    .RestoreFactoryDefaultsAsync(true, CancellationToken.None)
                    .AsTask();
            }

            await waitingResponse;
            byte[] requestFrame = harness.Transport.WrittenFrames.Single().ToArray();
            await harness.Transport.InjectReceiveAsync(requestFrame);
            ConfigurationChangeResult result = await operation;
            DeviceConnectionConfiguration current =
                harness.ConfigurationService.CurrentConfiguration;
            SerialSettings reopenedSettings = harness.Transport.CurrentSettings!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Status, Is.EqualTo(ConfigurationChangeStatus.Succeeded));
                Assert.That(harness.Transport.OpenHistory, Has.Count.EqualTo(2));
                Assert.That(harness.Transport.CloseOperationCount, Is.EqualTo(1));
                Assert.That(harness.PeriodicService.IsRunning, Is.False);
                Assert.That(reopenedSettings, Is.SameAs(current.SerialSettings));
            }));

            if (changeKind == ConfirmedReconnectKind.BaudRate)
            {
                Assert.Multiple((Action)(() =>
                {
                    Assert.That(current.SlaveAddress, Is.EqualTo(7));
                    Assert.That(reopenedSettings.BaudRate, Is.EqualTo(19200));
                    Assert.That(
                        requestFrame[..6],
                        Is.EqualTo(new byte[] { 0x07, 0x06, 0x00, 0x01, 0x00, 0x03 }));
                }));
            }
            else
            {
                Assert.Multiple((Action)(() =>
                {
                    Assert.That(current.SlaveAddress, Is.EqualTo(1));
                    Assert.That(reopenedSettings.BaudRate, Is.EqualTo(9600));
                    Assert.That(reopenedSettings.DataBits, Is.EqualTo(8));
                    Assert.That(
                        reopenedSettings.Parity,
                        Is.EqualTo(System.IO.Ports.Parity.None));
                    Assert.That(
                        reopenedSettings.StopBits,
                        Is.EqualTo(System.IO.Ports.StopBits.One));
                    Assert.That(
                        requestFrame[..6],
                        Is.EqualTo(new byte[] { 0x07, 0x06, 0x00, 0x23, 0x00, 0x01 }));
                }));
            }
        }

        /// <summary>
        /// 验证普通事务占用协调器时专用配置立即返回 Busy，不创建等待队列或过期写入。
        /// </summary>
        [Test]
        public async Task ChangeWhileCoordinatorBusy_ReturnsBusyWithoutQueue()
        {
            await using ConfigurationHarness harness =
                await ConfigurationHarness.CreateAsync();
            Task<TransactionExecutionResult> manualOperation = harness.Coordinator
                .TryExecuteAsync(
                    CreateReadTransaction(
                        harness.InitialSlaveAddress,
                        harness.InitialSettings),
                    CancellationToken.None)
                .AsTask();

            ConfigurationChangeResult busyResult = await harness.ConfigurationService
                .ChangeSlaveAddressAsync(9, CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(busyResult.Status, Is.EqualTo(ConfigurationChangeStatus.Busy));
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(1));
            }));

            await harness.Transport.InjectReceiveAsync(
                CreateReadResponse(harness.InitialSlaveAddress, 0x002A));
            await manualOperation;
            Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
        }

        /// <summary>
        /// 验证 0xFE 查询保留固定风险警告，只接受地址 1 至 64 且寄存器值与地址一致的响应。
        /// </summary>
        [Test]
        public async Task DiscoverUnknownAddress_StrictResponse_SavesOnlyRealAddress()
        {
            await using ConfigurationHarness harness =
                await ConfigurationHarness.CreateAsync();
            Task waitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            Task<ConfigurationChangeResult> operation = harness.ConfigurationService
                .DiscoverUnknownAddressAsync(CancellationToken.None)
                .AsTask();
            await waitingResponse;

            await harness.Transport.InjectReceiveAsync(
                CreateReadResponse(5, 5));
            ConfigurationChangeResult result = await operation;
            byte[] requestFrame = harness.Transport.WrittenFrames.Single().ToArray();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    SpecialConfigurationService.UnknownAddressRiskWarning,
                    Is.EqualTo(
                        "未知地址查询仅适用于总线上只有一个设备的情况；多设备同时应答会产生总线冲突。"));
                Assert.That(result.Status, Is.EqualTo(ConfigurationChangeStatus.Succeeded));
                Assert.That(result.Message, Does.StartWith(
                    SpecialConfigurationService.UnknownAddressRiskWarning));
                Assert.That(result.DiscoveredSlaveAddress, Is.EqualTo(5));
                Assert.That(result.LocalConfiguration.SlaveAddress, Is.EqualTo(5));
                Assert.That(result.LocalConfiguration.SlaveAddress, Is.Not.EqualTo(0xFE));
                Assert.That(
                    requestFrame[..6],
                    Is.EqualTo(new byte[] { 0xFE, 0x03, 0x00, 0x00, 0x00, 0x01 }));
            }));
        }

        /// <summary>
        /// 创建一个读单寄存器的标准事务，用于制造协调器 Busy 或定时发送请求。
        /// </summary>
        /// <param name="slaveAddress">读请求目标真实从站地址。</param>
        /// <param name="settings">提供事务总超时和原始静默超时的串口设置。</param>
        /// <returns>读取零基寄存器零、数量一的标准事务。</returns>
        private static TransactionRequest CreateReadTransaction(
            byte slaveAddress,
            SerialSettings settings)
        {
            return TransactionRequest.CreateStandard(
                ModbusRequestFactory.CreateReadHoldingRegisters(slaveAddress, 0, 1),
                settings.ResponseTimeout,
                settings.RawInterByteTimeout);
        }

        /// <summary>
        /// 创建地址和值可分别指定的单寄存器 0x03 响应。
        /// </summary>
        /// <param name="slaveAddress">响应帧携带的真实从站地址。</param>
        /// <param name="value">响应帧携带的单个原始寄存器字。</param>
        /// <returns>附带正确 Modbus CRC 的完整响应帧。</returns>
        private static byte[] CreateReadResponse(
            byte slaveAddress,
            ushort value)
        {
            return ModbusCrc16.Append(
                [
                    slaveAddress,
                    0x03,
                    0x02,
                    (byte)(value >> 8),
                    (byte)value,
                ]);
        }

        /// <summary>
        /// 创建可明确指定线路格式、响应超时固定为 50 毫秒的测试串口设置。
        /// </summary>
        /// <param name="baudRate">线路波特率。</param>
        /// <param name="dataBits">每字符数据位数。</param>
        /// <param name="parity">奇偶校验方式。</param>
        /// <param name="stopBits">停止位设置。</param>
        /// <returns>端口名为 COM_TEST 的不可变测试串口设置。</returns>
        private static SerialSettings CreateSettings(
            int baudRate,
            int dataBits = 8,
            System.IO.Ports.Parity parity = System.IO.Ports.Parity.None,
            System.IO.Ports.StopBits stopBits = System.IO.Ports.StopBits.One)
        {
            return new SerialSettings(
                "COM_TEST",
                baudRate,
                dataBits,
                parity,
                stopBits,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(20));
        }

        /// <summary>
        /// 等待协调器进入指定非当前状态，不使用轮询或业务时间延迟。
        /// </summary>
        /// <param name="coordinator">待观察的共享事务协调器。</param>
        /// <param name="expectedState">期望进入的协调器状态。</param>
        /// <returns>状态事件发生后的任务。</returns>
        private static Task WaitForCoordinatorStateAsync(
            ModbusTransactionCoordinator coordinator,
            TransactionCoordinatorState expectedState)
        {
            if (coordinator.State == expectedState)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<TransactionCoordinatorState>? observer = null;
            observer = state =>
            {
                if (state == expectedState && coordinator.State == expectedState)
                {
                    coordinator.StateChanged -= observer;
                    completionSource.TrySetResult();
                }
            };
            coordinator.StateChanged += observer;
            return completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 指定需要在完整回显后关闭旧会话并重新打开的配置流程。
        /// </summary>
        public enum ConfirmedReconnectKind
        {
            /// <summary>
            /// 写入波特率代码并使用目标波特率重新打开。
            /// </summary>
            BaudRate = 0,

            /// <summary>
            /// 写入恢复出厂命令并使用地址 1、9600-8-N-1 重新打开。
            /// </summary>
            FactoryReset = 1,
        }

        /// <summary>
        /// 管理专用配置测试使用的时间源、模拟串口、协调器和服务生命周期。
        /// </summary>
        private sealed class ConfigurationHarness : IAsyncDisposable
        {
            /// <summary>
            /// 初始化已经打开、启动且定时发送默认关闭的完整测试组合。
            /// </summary>
            /// <param name="timeProvider">确定性测试时间源。</param>
            /// <param name="transport">已经打开的模拟串口。</param>
            /// <param name="coordinator">已经启动的无队列协调器。</param>
            /// <param name="periodicService">默认关闭的定时发送服务。</param>
            /// <param name="configurationService">被测专用安全配置服务。</param>
            /// <param name="initialSlaveAddress">初始真实从站地址。</param>
            /// <param name="initialSettings">初始串口设置。</param>
            private ConfigurationHarness(
                ManualTimeProvider timeProvider,
                FakeSerialTransport transport,
                ModbusTransactionCoordinator coordinator,
                PeriodicSendService periodicService,
                SpecialConfigurationService configurationService,
                byte initialSlaveAddress,
                SerialSettings initialSettings)
            {
                TimeProvider = timeProvider;
                Transport = transport;
                Coordinator = coordinator;
                PeriodicService = periodicService;
                ConfigurationService = configurationService;
                InitialSlaveAddress = initialSlaveAddress;
                InitialSettings = initialSettings;
            }

            /// <summary>
            /// 获取确定性测试时间源。
            /// </summary>
            internal ManualTimeProvider TimeProvider { get; }

            /// <summary>
            /// 获取模拟串口传输。
            /// </summary>
            internal FakeSerialTransport Transport { get; }

            /// <summary>
            /// 获取共享无队列事务协调器。
            /// </summary>
            internal ModbusTransactionCoordinator Coordinator { get; }

            /// <summary>
            /// 获取定时发送服务。
            /// </summary>
            internal PeriodicSendService PeriodicService { get; }

            /// <summary>
            /// 获取被测专用安全配置服务。
            /// </summary>
            internal SpecialConfigurationService ConfigurationService { get; }

            /// <summary>
            /// 获取组合创建时的真实从站地址。
            /// </summary>
            internal byte InitialSlaveAddress { get; }

            /// <summary>
            /// 获取组合创建时的完整串口设置。
            /// </summary>
            internal SerialSettings InitialSettings { get; }

            /// <summary>
            /// 创建地址 7、指定串口设置且全部底层组件已经启动的测试组合。
            /// </summary>
            /// <param name="initialSettings">可选初始串口设置；为空时使用 9600-8-N-1 测试设置。</param>
            /// <returns>定时发送仍处于关闭状态的测试组合。</returns>
            internal static async Task<ConfigurationHarness> CreateAsync(
                SerialSettings? initialSettings = null)
            {
                const byte initialSlaveAddress = 7;
                ManualTimeProvider timeProvider = new();
                FakeSerialTransport transport = new(timeProvider);
                SerialSettings settings = initialSettings ?? CreateSettings(9600);
                await transport.OpenAsync(settings, CancellationToken.None);
                ModbusTransactionCoordinator coordinator = new(transport, timeProvider);
                await coordinator.StartAsync(CancellationToken.None);
                PeriodicSendService periodicService = new(coordinator, timeProvider);
                SpecialConfigurationService configurationService = new(
                    coordinator,
                    periodicService,
                    transport,
                    initialSlaveAddress,
                    settings);
                return new ConfigurationHarness(
                    timeProvider,
                    transport,
                    coordinator,
                    periodicService,
                    configurationService,
                    initialSlaveAddress,
                    settings);
            }

            /// <summary>
            /// 停止协调器和定时服务，并永久释放模拟串口。
            /// </summary>
            /// <returns>全部异步资源完成收敛后的值任务。</returns>
            public async ValueTask DisposeAsync()
            {
                await Coordinator.StopAsync(CancellationToken.None);
                await PeriodicService.DisposeAsync();
                await Transport.DisposeAsync();
            }
        }
    }
}
