using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Coordination;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Coordination
{
    /// <summary>
    /// 验证 IAP 和全部 Modbus 服务入口共享同一无队列互斥门。
    /// </summary>
    [TestFixture]
    public sealed class ApplicationOperationGateTests
    {
        /// <summary>
        /// 验证活动 Modbus 租约阻止 IAP，而 IAP 租约反向阻止新 Modbus。
        /// </summary>
        [Test]
        public void Gate_EnforcesBidirectionalExclusion()
        {
            ApplicationOperationGate gate = new();
            using IApplicationOperationLease modbusLease =
                gate.TryEnterModbus() ??
                throw new AssertionException("空闲门必须允许 Modbus 进入。");

            Assert.That(gate.TryEnterIap(), Is.Null);
            modbusLease.Dispose();
            using IApplicationOperationLease iapLease =
                gate.TryEnterIap() ??
                throw new AssertionException("Modbus 释放后必须允许 IAP 进入。");

            Assert.Multiple((Action)(() =>
            {
                Assert.That(gate.IsIapActive, Is.True);
                Assert.That(gate.TryEnterModbus(), Is.Null);
            }));

            iapLease.Dispose();
            using IApplicationOperationLease restoredModbusLease =
                gate.TryEnterModbus() ??
                throw new AssertionException("IAP 释放后必须恢复 Modbus。");
            Assert.That(gate.ActiveModbusOperationCount, Is.EqualTo(1));
        }

        /// <summary>
        /// 验证绕过界面直接提交事务时，协调器仍以 IapActive 拒绝且不写串口。
        /// </summary>
        [Test]
        public async Task Coordinator_IapActive_RejectsServiceEntryWithoutWrite()
        {
            ManualTimeProvider timeProvider = new();
            await using FakeSerialTransport transport = new(timeProvider);
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            ApplicationOperationGate gate = new();
            await using ModbusTransactionCoordinator coordinator = new(
                transport,
                timeProvider,
                gate);
            await coordinator.StartAsync(CancellationToken.None);
            using IApplicationOperationLease iapLease =
                gate.TryEnterIap() ??
                throw new AssertionException("空闲门必须允许 IAP 进入。");
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                1,
                0,
                1);
            TransactionExecutionResult result = await coordinator.TryExecuteAsync(
                TransactionRequest.CreateStandard(
                    request,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(20)),
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.IsAccepted, Is.False);
                Assert.That(result.Rejection, Is.EqualTo(TransactionRejected.IapActive));
                Assert.That(transport.WrittenFrames, Is.Empty);
            }));
        }

        /// <summary>
        /// 验证绕过 ViewModel 直接启动定时服务时仍会被 IAP 门拒绝。
        /// </summary>
        [Test]
        public async Task PeriodicSendService_IapActive_RejectsStart()
        {
            ManualTimeProvider timeProvider = new();
            await using FakeSerialTransport transport = new(timeProvider);
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            ApplicationOperationGate gate = new();
            await using ModbusTransactionCoordinator coordinator = new(
                transport,
                timeProvider,
                gate);
            await coordinator.StartAsync(CancellationToken.None);
            await using PeriodicSendService periodic = new(
                coordinator,
                timeProvider,
                gate);
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                1,
                0,
                1);
            periodic.Configure(
                TransactionRequest.CreateStandard(
                    request,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(20)),
                TimeSpan.FromSeconds(1));
            using IApplicationOperationLease iapLease =
                gate.TryEnterIap() ??
                throw new AssertionException("空闲门必须允许 IAP 进入。");

            InvalidOperationException? exception = null;

            try
            {
                periodic.Start();
            }
            catch (InvalidOperationException captured)
            {
                exception = captured;
            }

            Assert.Multiple((Action)(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(periodic.IsRunning, Is.False);
                Assert.That(transport.WrittenFrames, Is.Empty);
            }));
        }
    }
}
