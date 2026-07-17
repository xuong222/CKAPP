using CH32UpperComputer.App.Services;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Core.Registers;
using CH32UpperComputer.Infrastructure.Logging;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Infrastructure.Transactions;

using System.IO;
using System.Windows;

namespace CH32UpperComputer.App
{
    /// <summary>
    /// 提供上位机显式组合根、启动加载和安全退出生命周期。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 应用生产时间源；所有通信超时和日历时间均从此派生。
        /// </summary>
        private readonly TimeProvider timeProvider = TimeProvider.System;

        /// <summary>
        /// 持久化 UTF-8 JSON 设置的存储。
        /// </summary>
        private JsonSettingsStore? settingsStore;

        /// <summary>
        /// 单后台读取循环串口传输。
        /// </summary>
        private SerialPortTransport? transport;

        /// <summary>
        /// 无队列 Modbus 事务协调器。
        /// </summary>
        private ModbusTransactionCoordinator? coordinator;

        /// <summary>
        /// 默认关闭且不积压任务的定时发送服务。
        /// </summary>
        private PeriodicSendService? periodicSendService;

        /// <summary>
        /// 5000/750 双容量通信日志服务。
        /// </summary>
        private CommunicationLogService? communicationLogService;

        /// <summary>
        /// 主窗口聚合 ViewModel。
        /// </summary>
        private MainWindowViewModel? mainWindowViewModel;

        /// <summary>
        /// 防止主窗口关闭事件重复执行异步退出流程。
        /// </summary>
        private int shutdownStarted;

        /// <summary>
        /// 异步加载安全设置、手工构造全部依赖并显示主窗口；启动期间不会打开串口或发送数据。
        /// </summary>
        /// <param name="eventArgs">WPF 启动参数。</param>
        protected override async void OnStartup(StartupEventArgs eventArgs)
        {
            base.OnStartup(eventArgs);

            try
            {
                string settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CH32UpperComputer",
                    "settings.json");
                settingsStore = new JsonSettingsStore(settingsPath, timeProvider);
                AppSettings settings = await settingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
                transport = new SerialPortTransport(timeProvider);
                coordinator = new ModbusTransactionCoordinator(transport, timeProvider);
                periodicSendService = new PeriodicSendService(coordinator, timeProvider);
                communicationLogService = new CommunicationLogService(timeProvider);
                DeviceSnapshot snapshot = new();
                WpfUiDispatcher dispatcher = new(Dispatcher);
                ModbusOperationService operationService = new(
                    coordinator,
                    transport,
                    snapshot,
                    communicationLogService,
                    timeProvider);
                SerialConnectionViewModel serialConnection = new(
                    transport,
                    coordinator,
                    dispatcher,
                    settings);
                SerialSettings initialSerialSettings = serialConnection.CreateSerialSettings();
                SpecialConfigurationService specialConfigurationService = new(
                    coordinator,
                    periodicSendService,
                    transport,
                    checked((byte)serialConnection.SlaveAddress),
                    initialSerialSettings);
                CommandConsoleViewModel commandConsole = new(
                    operationService,
                    periodicSendService,
                    serialConnection,
                    dispatcher,
                    settings);
                MonitorViewModel monitor = new(operationService, dispatcher);
                ParametersViewModel parameters = new(
                    commandConsole,
                    serialConnection,
                    specialConfigurationService,
                    operationService,
                    dispatcher);
                RegisterToolViewModel registerTool = new(
                    commandConsole,
                    serialConnection,
                    specialConfigurationService,
                    operationService,
                    dispatcher);
                CommunicationLogViewModel communicationLog = new(
                    communicationLogService,
                    dispatcher,
                    timeProvider,
                    settings.LogExportDirectory);
                SystemInfoViewModel systemInfo = new(
                    settingsStore,
                    communicationLog.ExportDirectory);
                mainWindowViewModel = new MainWindowViewModel(
                    serialConnection,
                    commandConsole,
                    monitor,
                    parameters,
                    registerTool,
                    communicationLog,
                    systemInfo,
                    operationService,
                    dispatcher);
                MainWindow window = new(mainWindowViewModel);
                MainWindow = window;
                window.Show();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    $"上位机启动失败：{exception.Message}",
                    "CH32 上位机",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        /// <summary>
        /// 保存安全偏好、停止定时、完成活动事务、退出读取循环并释放全部服务。
        /// </summary>
        /// <returns>整个退出序列只执行一次并完成后的任务。</returns>
        public async Task ShutdownRuntimeAsync()
        {
            if (Interlocked.CompareExchange(ref shutdownStarted, 1, 0) != 0)
            {
                return;
            }

            MainWindowViewModel? viewModel = mainWindowViewModel;
            viewModel?.BeginShutdown();

            try
            {
                if (viewModel is not null && settingsStore is not null)
                {
                    AppSettings settings = viewModel.SerialConnection.CreateAppSettings(
                        viewModel.CommandConsole.PeriodicIntervalMilliseconds,
                        viewModel.CommandConsole.AutoAppendCrc,
                        viewModel.CommandConsole.InputText,
                        viewModel.CommunicationLog.ExportDirectory);
                    await settingsStore.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch (Exception)
            {
                // 设置保存失败不应阻止串口和后台任务安全退出。
            }

            if (periodicSendService is not null)
            {
                await periodicSendService.StopAsync(CancellationToken.None).ConfigureAwait(true);
            }

            if (coordinator is not null)
            {
                await coordinator.StopAsync(CancellationToken.None).ConfigureAwait(true);
                await coordinator.WaitForBackgroundOperationsAsync(CancellationToken.None).ConfigureAwait(true);
            }

            communicationLogService?.FlushPendingPublication();
            viewModel?.Dispose();

            if (periodicSendService is not null)
            {
                await periodicSendService.DisposeAsync().ConfigureAwait(true);
            }

            if (coordinator is not null)
            {
                await coordinator.DisposeAsync().ConfigureAwait(true);
            }
            else if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(true);
            }

            communicationLogService?.Dispose();
        }
    }
}
