using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Infrastructure.Settings;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证主窗口顶部固定状态字段和系统页本地信息。
    /// </summary>
    [TestFixture]
    public sealed class MainWindowAndSystemInfoViewModelTests
    {
        /// <summary>
        /// 验证主窗口聚合串口、波特率、从站、连接状态和最近响应五项字段。
        /// </summary>
        [Test]
        public async Task MainWindow_ExposesRequiredTopStatusFields()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            string settingsPath = Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid().ToString("N"),
                "settings.json");
            JsonSettingsStore settingsStore = new(settingsPath, harness.TimeProvider);
            using CommunicationLogViewModel logViewModel = new(
                harness.LogService,
                harness.Dispatcher,
                harness.TimeProvider,
                string.Empty);
            SystemInfoViewModel systemInfo = new(settingsStore, logViewModel.ExportDirectory);
            using MainWindowViewModel mainWindow = new(
                harness.SerialConnection,
                harness.CommandConsole,
                harness.Monitor,
                harness.Parameters,
                harness.RegisterTool,
                logViewModel,
                systemInfo,
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

        /// <summary>
        /// 验证系统信息只暴露本地运行环境、文件路径、协议范围和免责声明。
        /// </summary>
        [Test]
        public void SystemInfo_ExposesLocalPathsAndProtocolScope()
        {
            string settingsPath = Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid().ToString("N"),
                "settings.json");
            JsonSettingsStore settingsStore = new(settingsPath);
            SystemInfoViewModel viewModel = new(settingsStore, Path.GetTempPath());

            Assert.Multiple((Action)(() =>
            {
                Assert.That(viewModel.SettingsFilePath, Is.EqualTo(Path.GetFullPath(settingsPath)));
                Assert.That(viewModel.ProtocolSupport, Does.Contain("0x03"));
                Assert.That(viewModel.ProtocolSupport, Does.Contain("0xFE"));
                Assert.That(viewModel.RegisterMapVersion, Does.Contain("40001～40036"));
                Assert.That(viewModel.Disclaimer, Is.Not.Empty);
            }));
        }
    }
}
