using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Logging;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;
using CommunityToolkit.Mvvm.Input;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证串口识别、连接静默以及发送、解析、快照、卡片和日志组成的完整流程。
    /// </summary>
    [TestFixture]
    public sealed class SerialAndCommandWorkflowTests
    {
        /// <summary>
        /// 验证连接只打开串口和接收循环，默认不写数据且定时发送关闭。
        /// </summary>
        [Test]
        public async Task Connect_DefaultsToManualAndDoesNotWrite()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();

            await harness.ConnectAsync();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.SerialConnection.IsConnected, Is.True);
                Assert.That(
                    harness.SerialConnection.ConnectionStatus,
                    Is.EqualTo("已连接 · 等待手动指令"));
                Assert.That(harness.Periodic.IsRunning, Is.False);
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
            }));
        }

        /// <summary>
        /// 验证连接状态切换期间的空文本或其他选项不会覆盖已经实际打开的活动端口。
        /// </summary>
        [Test]
        public async Task ConnectedPortName_IgnoresEditorChangesUntilDisconnected()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();

            harness.SerialConnection.PortName = string.Empty;
            harness.SerialConnection.PortName = "COM99";

            Assert.That(harness.SerialConnection.PortName, Is.EqualTo("COM_TEST"));
        }

        /// <summary>
        /// 验证默认 COM1 会被当前项目的 CH340 友好设备替换，空结果不会伪造 COM1 列表项。
        /// </summary>
        [Test]
        public async Task RefreshPorts_PrefersCh340AndDoesNotCreatePhantomCom1()
        {
            ManualTimeProvider timeProvider = new();
            await using FakeSerialTransport transport = new(timeProvider);
            await using ModbusTransactionCoordinator coordinator = new(
                transport,
                timeProvider);
            FakeSerialPortDiscovery discovery = new(
                new[]
                {
                    new SerialPortDescriptor(
                        "COM1",
                        "通信端口 (COM1)",
                        "ACPI\\PNP0501",
                        false),
                    new SerialPortDescriptor(
                        "COM7",
                        "USB-SERIAL CH340 (COM7)",
                        "USB\\VID_1A86&PID_7523",
                        true),
                });
            SerialConnectionViewModel viewModel = new(
                transport,
                coordinator,
                discovery,
                new ImmediateUiDispatcher(),
                new AppSettings
                {
                    PortName = "COM1",
                });

            try
            {
                await viewModel.RefreshPortsAsync(CancellationToken.None);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(viewModel.PortName, Is.EqualTo("COM7"));
                    Assert.That(viewModel.AvailablePorts, Has.Count.EqualTo(2));
                    Assert.That(
                        viewModel.AvailablePorts[1].DisplayName,
                        Is.EqualTo("USB-SERIAL CH340 (COM7)"));
                }));

                discovery.SetResult(Array.Empty<SerialPortDescriptor>());
                await viewModel.RefreshPortsAsync(CancellationToken.None);

                Assert.That(viewModel.AvailablePorts, Is.Empty);
            }
            finally
            {
                viewModel.Dispose();
            }
        }

        /// <summary>
        /// 验证一次完整手动 0x03 事务更新仪表盘、统一参数表、计数和同编号 TX/RX 日志。
        /// </summary>
        [Test]
        public async Task ManualRead_UpdatesDashboardParametersMetricsAndRawLogs()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            harness.Device.SetRegister(0x0002, 2534);
            harness.Device.SetRegister(0x0003, 120);
            harness.Device.SetRegister(0x0004, 35);
            harness.Device.SetRegister(0x0005, 87);
            harness.Device.SetRegister(0x0006, 880);
            harness.Device.SetRegister(0x0007, 12);
            harness.Device.SetRegister(0x0008, 1);
            harness.Device.SetRegister(0x0009, 0x0004);
            await harness.ConnectAsync();
            Task sendTask = harness.CommandConsole.SendCommand.ExecuteAsync(null);

            await harness.RespondToWriteAsync(1);
            await sendTask.WaitAsync(TimeSpan.FromSeconds(1));

            IReadOnlyList<CommunicationLogEntry> logs =
                harness.LogService.CreateDataSnapshot();
            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Monitor.SensorCards[0].ValueText, Is.EqualTo("25.34"));
                Assert.That(harness.Monitor.SensorCards[4].ValueText, Is.EqualTo("880"));
                Assert.That(harness.Monitor.OverallStateText, Is.EqualTo("设备报警"));
                Assert.That(harness.Monitor.AlarmItems[2].IsActive, Is.True);
                Assert.That(
                    harness.Parameters.Registers.Single(
                        item => item.Definition.DocumentAddress == 40003)
                    .CurrentValueText,
                    Is.EqualTo("25.34 ℃"));
                Assert.That(harness.CommandConsole.SendCount, Is.EqualTo(1));
                Assert.That(harness.CommandConsole.SuccessCount, Is.EqualTo(1));
                Assert.That(
                    logs.Count(
                        entry => entry.Direction == CommunicationDirection.Transmit),
                    Is.EqualTo(1));
                Assert.That(
                    logs.Count(
                        entry => entry.Direction == CommunicationDirection.Receive),
                    Is.EqualTo(1));
                Assert.That(
                    logs.Where(entry => entry.TransactionId.HasValue)
                        .Select(entry => entry.TransactionId)
                        .Distinct()
                        .Count(),
                    Is.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证单列常用指令按钮使用当前从站地址直接发送，不要求再次点击发送。
        /// </summary>
        [Test]
        public async Task CommonMonitorCommand_ClickOnce_SendsCurrentAddressRequest()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            ModbusRequest expectedRequest =
                ModbusRequestFactory.CreateReadHoldingRegisters(
                    1,
                    0x0002,
                    8);

            harness.CommandConsole.CommonCommands[0].SelectCommand.Execute(null);
            await harness.RespondToWriteAsync(1);
            IAsyncRelayCommand? asynchronousCommand =
                harness.CommandConsole.CommonCommands[0].SelectCommand as
                    IAsyncRelayCommand;

            Assert.That(
                asynchronousCommand,
                Is.Not.Null,
                "常用指令必须等待其一键发送事务完成并正确管理忙状态。");
            await asynchronousCommand!.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.Transport.WrittenFrames[0].ToArray(),
                    Is.EqualTo(expectedRequest.RawFrame.ToArray()));
                Assert.That(harness.CommandConsole.SendCount, Is.EqualTo(1));
                Assert.That(harness.CommandConsole.SuccessCount, Is.EqualTo(1));
            }));
        }
    }
}
