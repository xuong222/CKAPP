using CH32UpperComputer.App.Services;
using CH32UpperComputer.Infrastructure.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;

using System.ComponentModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 聚合主窗口顶部状态和监控、参数、专家工具、日志、系统五个页面 ViewModel。
    /// </summary>
    public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 提供最近响应耗时和事务统计的统一操作服务。
        /// </summary>
        private readonly ModbusOperationService operationService;

        /// <summary>
        /// 将后台统计事件切换到界面线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 指示主窗口 ViewModel 已释放并取消事件订阅。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 顶部状态栏显示的最近响应耗时。
        /// </summary>
        [ObservableProperty]
        private string lastResponseDurationText = "--";

        /// <summary>
        /// 当前是否处于应用退出流程。
        /// </summary>
        [ObservableProperty]
        private bool isShuttingDown;

        /// <summary>
        /// 初始化主窗口全部页面及顶部状态聚合。
        /// </summary>
        /// <param name="serialConnection">串口连接与参数 ViewModel。</param>
        /// <param name="commandConsole">监控页紧凑收发区 ViewModel。</param>
        /// <param name="monitor">六项数据卡和报警栏 ViewModel。</param>
        /// <param name="parameters">普通参数和安全配置流程 ViewModel。</param>
        /// <param name="registerTool">专家寄存器工具 ViewModel。</param>
        /// <param name="communicationLog">有界虚拟化日志 ViewModel。</param>
        /// <param name="systemInfo">本地系统信息 ViewModel。</param>
        /// <param name="operationService">提供最近事务耗时的统一操作服务。</param>
        /// <param name="dispatcher">负责把后台事件投递到界面线程的调度器。</param>
        public MainWindowViewModel(
            SerialConnectionViewModel serialConnection,
            CommandConsoleViewModel commandConsole,
            MonitorViewModel monitor,
            ParametersViewModel parameters,
            RegisterToolViewModel registerTool,
            CommunicationLogViewModel communicationLog,
            SystemInfoViewModel systemInfo,
            ModbusOperationService operationService,
            IUiDispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(serialConnection);
            ArgumentNullException.ThrowIfNull(commandConsole);
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(registerTool);
            ArgumentNullException.ThrowIfNull(communicationLog);
            ArgumentNullException.ThrowIfNull(systemInfo);
            ArgumentNullException.ThrowIfNull(operationService);
            ArgumentNullException.ThrowIfNull(dispatcher);
            SerialConnection = serialConnection;
            CommandConsole = commandConsole;
            Monitor = monitor;
            Parameters = parameters;
            RegisterTool = registerTool;
            CommunicationLog = communicationLog;
            SystemInfo = systemInfo;
            this.operationService = operationService;
            this.dispatcher = dispatcher;
            operationService.MetricsChanged += HandleMetricsChanged;
            serialConnection.PropertyChanged += HandleSerialConnectionPropertyChanged;
        }

        /// <summary>
        /// 获取串口连接与设置 ViewModel。
        /// </summary>
        public SerialConnectionViewModel SerialConnection { get; }

        /// <summary>
        /// 获取监控页紧凑收发区 ViewModel。
        /// </summary>
        public CommandConsoleViewModel CommandConsole { get; }

        /// <summary>
        /// 获取六项传感器卡片和报警栏 ViewModel。
        /// </summary>
        public MonitorViewModel Monitor { get; }

        /// <summary>
        /// 获取参数和安全配置流程 ViewModel。
        /// </summary>
        public ParametersViewModel Parameters { get; }

        /// <summary>
        /// 获取专家寄存器工具 ViewModel。
        /// </summary>
        public RegisterToolViewModel RegisterTool { get; }

        /// <summary>
        /// 获取通信日志 ViewModel。
        /// </summary>
        public CommunicationLogViewModel CommunicationLog { get; }

        /// <summary>
        /// 获取系统信息 ViewModel。
        /// </summary>
        public SystemInfoViewModel SystemInfo { get; }

        /// <summary>
        /// 获取顶部状态栏显示的串口名称。
        /// </summary>
        public string PortStatus => SerialConnection.PortName;

        /// <summary>
        /// 获取顶部状态栏显示的波特率。
        /// </summary>
        public string BaudRateStatus => SerialConnection.BaudRate.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 获取顶部状态栏显示的普通从站地址。
        /// </summary>
        public string SlaveAddressStatus => SerialConnection.SlaveAddress.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 获取顶部状态栏显示的连接状态。
        /// </summary>
        public string ConnectionStatus => SerialConnection.ConnectionStatus;

        /// <summary>
        /// 取消全部事件订阅并释放各页面 ViewModel。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            operationService.MetricsChanged -= HandleMetricsChanged;
            SerialConnection.PropertyChanged -= HandleSerialConnectionPropertyChanged;
            RegisterTool.Dispose();
            Parameters.Dispose();
            Monitor.Dispose();
            CommandConsole.Dispose();
            CommunicationLog.Dispose();
            SerialConnection.Dispose();
        }

        /// <summary>
        /// 标记应用正在退出，供顶部状态和关闭流程禁用交互。
        /// </summary>
        public void BeginShutdown()
        {
            IsShuttingDown = true;
        }

        /// <summary>
        /// 接收统计快照并在界面线程刷新最近响应耗时。
        /// </summary>
        /// <param name="metrics">统一操作服务发布的不可变统计快照。</param>
        private void HandleMetricsChanged(OperationMetrics metrics)
        {
            dispatcher.Post(
                () => LastResponseDurationText = metrics.LastResponseDuration.HasValue
                    ? $"{metrics.LastResponseDuration.Value.TotalMilliseconds:0} ms"
                    : "--");
        }

        /// <summary>
        /// 在顶部五项依赖字段变化后发布对应聚合属性通知。
        /// </summary>
        /// <param name="sender">发布属性变化的串口 ViewModel。</param>
        /// <param name="eventArgs">发生变化的属性名称。</param>
        private void HandleSerialConnectionPropertyChanged(
            object? sender,
            PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(SerialConnectionViewModel.PortName))
            {
                OnPropertyChanged(nameof(PortStatus));
            }
            else if (eventArgs.PropertyName == nameof(SerialConnectionViewModel.BaudRate))
            {
                OnPropertyChanged(nameof(BaudRateStatus));
            }
            else if (eventArgs.PropertyName == nameof(SerialConnectionViewModel.SlaveAddress))
            {
                OnPropertyChanged(nameof(SlaveAddressStatus));
            }
            else if (eventArgs.PropertyName == nameof(SerialConnectionViewModel.ConnectionStatus))
            {
                OnPropertyChanged(nameof(ConnectionStatus));
            }
        }
    }
}
