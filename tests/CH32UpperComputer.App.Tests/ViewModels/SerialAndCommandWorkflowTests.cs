using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.Infrastructure.Logging;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证连接静默以及手动发送、解析、快照、卡片和日志组成的完整应用流程。
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
                Assert.That(harness.SerialConnection.ConnectionStatus, Is.EqualTo("已连接 · 等待手动指令"));
                Assert.That(harness.Periodic.IsRunning, Is.False);
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
            }));
        }

        /// <summary>
        /// 验证一次完整手动 0x03 事务更新六张数据卡、报警栏、计数和同编号 TX/RX 日志。
        /// </summary>
        [Test]
        public async Task ManualRead_UpdatesDashboardMetricsAndRawLogs()
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

            IReadOnlyList<CommunicationLogEntry> logs = harness.LogService.CreateDataSnapshot();
            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Monitor.SensorCards[0].ValueText, Is.EqualTo("25.34"));
                Assert.That(harness.Monitor.SensorCards[4].ValueText, Is.EqualTo("880"));
                Assert.That(harness.Monitor.OverallStateText, Is.EqualTo("设备报警"));
                Assert.That(harness.Monitor.AlarmItems[2].IsActive, Is.True);
                Assert.That(harness.CommandConsole.SendCount, Is.EqualTo(1));
                Assert.That(harness.CommandConsole.SuccessCount, Is.EqualTo(1));
                Assert.That(logs.Count(entry => entry.Direction == CommunicationDirection.Transmit), Is.EqualTo(1));
                Assert.That(logs.Count(entry => entry.Direction == CommunicationDirection.Receive), Is.EqualTo(1));
                Assert.That(logs.Where(entry => entry.TransactionId.HasValue).Select(entry => entry.TransactionId).Distinct().Count(), Is.EqualTo(1));
            }));
        }
    }
}
