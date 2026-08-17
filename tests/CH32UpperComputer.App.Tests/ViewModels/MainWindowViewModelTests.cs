using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证精简后的主窗口顶部固定状态字段。
    /// </summary>
    [TestFixture]
    public sealed class MainWindowViewModelTests
    {
        /// <summary>
        /// 验证主窗口聚合串口、波特率、从站、连接状态和最近响应五项字段。
        /// </summary>
        [Test]
        public async Task MainWindow_ExposesRequiredTopStatusFields()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            using CommunicationLogViewModel logViewModel = new(
                harness.LogService,
                harness.Dispatcher,
                harness.TimeProvider,
                string.Empty);
            using MainWindowViewModel mainWindow = new(
                harness.SerialConnection,
                harness.CommandConsole,
                harness.Monitor,
                harness.Parameters,
                harness.FirmwareUpgrade,
                logViewModel,
                harness.SerialAssistant,
                harness.OperationService,
                harness.Dispatcher);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(mainWindow.PortStatus, Is.EqualTo("COM_TEST"));
                Assert.That(mainWindow.BaudRateStatus, Is.EqualTo("9600"));
                Assert.That(mainWindow.SlaveAddressStatus, Is.EqualTo("1"));
                Assert.That(mainWindow.ConnectionStatus, Is.EqualTo("未连接"));
                Assert.That(mainWindow.LastResponseDurationText, Is.EqualTo("--"));
            }));
        }
    }
}
