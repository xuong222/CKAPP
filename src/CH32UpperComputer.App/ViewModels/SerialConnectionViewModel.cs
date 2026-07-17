using CH32UpperComputer.App.Services;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Infrastructure.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System.Collections.ObjectModel;
using System.IO.Ports;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 管理串口参数、连接生命周期和连接后保持静默的界面状态。
    /// </summary>
    public sealed partial class SerialConnectionViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 实际打开、关闭串口且只使用单后台读取循环的传输抽象。
        /// </summary>
        private readonly ISerialTransport transport;

        /// <summary>
        /// 启动接收循环、管理事务和执行确定性断开的协调器。
        /// </summary>
        private readonly ModbusTransactionCoordinator coordinator;

        /// <summary>
        /// 将后台状态事件切换到界面线程的调度器。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 指示 ViewModel 已经释放并停止接收后台事件。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 当前选择的操作系统串口名称。
        /// </summary>
        [ObservableProperty]
        private string portName;

        /// <summary>
        /// 当前选择的线路波特率。
        /// </summary>
        [ObservableProperty]
        private int baudRate;

        /// <summary>
        /// 当前选择的串口数据位数。
        /// </summary>
        [ObservableProperty]
        private int dataBits;

        /// <summary>
        /// 当前选择的串口奇偶校验。
        /// </summary>
        [ObservableProperty]
        private Parity parity;

        /// <summary>
        /// 当前选择的串口停止位。
        /// </summary>
        [ObservableProperty]
        private StopBits stopBits;

        /// <summary>
        /// 当前普通 Modbus 从站地址。
        /// </summary>
        [ObservableProperty]
        private int slaveAddress;

        /// <summary>
        /// 当前标准事务总响应超时毫秒数。
        /// </summary>
        [ObservableProperty]
        private int responseTimeoutMilliseconds;

        /// <summary>
        /// 当前原始调试模式字节间静默超时毫秒数。
        /// </summary>
        [ObservableProperty]
        private int rawInterByteTimeoutMilliseconds;

        /// <summary>
        /// 当前是否存在已打开且协调器正在接收的串口会话。
        /// </summary>
        [ObservableProperty]
        private bool isConnected;

        /// <summary>
        /// 当前是否正在执行打开或关闭操作。
        /// </summary>
        [ObservableProperty]
        private bool isConnectionOperationBusy;

        /// <summary>
        /// 当前是否有一个 Modbus 事务占用无队列活动门。
        /// </summary>
        [ObservableProperty]
        private bool isTransactionBusy;

        /// <summary>
        /// 顶部状态栏和串口设置卡共同显示的连接说明。
        /// </summary>
        [ObservableProperty]
        private string connectionStatus = "未连接";

        /// <summary>
        /// 最近一次连接或参数校验错误。
        /// </summary>
        [ObservableProperty]
        private string errorMessage = string.Empty;

        /// <summary>
        /// 初始化串口连接 ViewModel，并应用经过验证的持久化设置。
        /// </summary>
        /// <param name="transport">负责实际打开、关闭和单循环读取的串口传输。</param>
        /// <param name="coordinator">负责接收循环和无队列事务状态的协调器。</param>
        /// <param name="dispatcher">负责把后台事件投递到界面线程的调度器。</param>
        /// <param name="settings">已经由设置存储校验并强制关闭自动发送的初始设置。</param>
        public SerialConnectionViewModel(
            ISerialTransport transport,
            ModbusTransactionCoordinator coordinator,
            IUiDispatcher dispatcher,
            AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(coordinator);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(settings);
            this.transport = transport;
            this.coordinator = coordinator;
            this.dispatcher = dispatcher;
            AppSettings safeSettings = settings.CreateValidatedCopy();
            portName = safeSettings.PortName;
            baudRate = safeSettings.BaudRate;
            dataBits = safeSettings.DataBits;
            parity = safeSettings.Parity;
            stopBits = safeSettings.StopBits;
            slaveAddress = safeSettings.SlaveAddress;
            responseTimeoutMilliseconds = safeSettings.ResponseTimeoutMilliseconds;
            rawInterByteTimeoutMilliseconds = safeSettings.RawInterByteTimeoutMilliseconds;
            AvailablePorts = new ObservableCollection<string>();
            BaudRates = Array.AsReadOnly(new[] { 2400, 4800, 9600, 19200, 38400, 57600 });
            DataBitOptions = Array.AsReadOnly(new[] { 5, 6, 7, 8 });
            ParityOptions = Array.AsReadOnly(new[] { Parity.None, Parity.Even, Parity.Odd, Parity.Mark, Parity.Space });
            StopBitOptions = Array.AsReadOnly(new[] { StopBits.One, StopBits.Two, StopBits.OnePointFive });
            ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
            DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
            RefreshPortsCommand = new RelayCommand(RefreshPorts, CanEditSettings);
            ResetDefaultsCommand = new RelayCommand(ResetDefaults, CanEditSettings);
            coordinator.BusyChanged += HandleBusyChanged;
            coordinator.StateChanged += HandleCoordinatorStateChanged;
            RefreshPorts();
        }

        /// <summary>
        /// 在有效连接参数应用或从站地址变化后通知依赖页面刷新顶部状态。
        /// </summary>
        public event Action? SettingsChanged;

        /// <summary>
        /// 获取当前检测到的操作系统串口名称集合。
        /// </summary>
        public ObservableCollection<string> AvailablePorts { get; }

        /// <summary>
        /// 获取固件明确支持的波特率集合。
        /// </summary>
        public IReadOnlyList<int> BaudRates { get; }

        /// <summary>
        /// 获取 SerialPort 支持的数据位选项。
        /// </summary>
        public IReadOnlyList<int> DataBitOptions { get; }

        /// <summary>
        /// 获取可选择的奇偶校验选项。
        /// </summary>
        public IReadOnlyList<Parity> ParityOptions { get; }

        /// <summary>
        /// 获取可选择的停止位选项。
        /// </summary>
        public IReadOnlyList<StopBits> StopBitOptions { get; }

        /// <summary>
        /// 获取打开当前串口的异步命令。
        /// </summary>
        public IAsyncRelayCommand ConnectCommand { get; }

        /// <summary>
        /// 获取安全断开当前串口的异步命令。
        /// </summary>
        public IAsyncRelayCommand DisconnectCommand { get; }

        /// <summary>
        /// 获取刷新系统串口列表的命令。
        /// </summary>
        public IRelayCommand RefreshPortsCommand { get; }

        /// <summary>
        /// 获取只恢复配置、不连接且不发送任何指令的默认值命令。
        /// </summary>
        public IRelayCommand ResetDefaultsCommand { get; }

        /// <summary>
        /// 获取标准 Modbus RTU 帧最大长度只读说明。
        /// </summary>
        public int MaximumFrameBytes => 256;

        /// <summary>
        /// 获取接收缓存最大容量只读说明。
        /// </summary>
        public int ReceiveBufferBytes => 4096;

        /// <summary>
        /// 根据当前界面参数创建经过完整校验的不可变串口设置。
        /// </summary>
        /// <returns>可以直接交给传输层和事务请求的设置。</returns>
        public SerialSettings CreateSerialSettings()
        {
            return new SerialSettings(
                PortName,
                BaudRate,
                DataBits,
                Parity,
                StopBits,
                TimeSpan.FromMilliseconds(ResponseTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(RawInterByteTimeoutMilliseconds));
        }

        /// <summary>
        /// 把当前界面参数转换为只允许安全字段持久化的设置对象。
        /// </summary>
        /// <param name="periodicIntervalMilliseconds">最近选择的定时发送间隔。</param>
        /// <param name="autoAppendCrc">手动输入是否默认自动补充 CRC。</param>
        /// <param name="lastCommand">最近一次手动输入的指令。</param>
        /// <param name="logExportDirectory">最近选择的日志导出目录。</param>
        /// <returns>自动发送和定时启用均固定为假的设置对象。</returns>
        public AppSettings CreateAppSettings(
            int periodicIntervalMilliseconds,
            bool autoAppendCrc,
            string lastCommand,
            string logExportDirectory)
        {
            return new AppSettings
            {
                PortName = PortName,
                BaudRate = BaudRate,
                DataBits = DataBits,
                Parity = Parity,
                StopBits = StopBits,
                SlaveAddress = SlaveAddress,
                ResponseTimeoutMilliseconds = ResponseTimeoutMilliseconds,
                RawInterByteTimeoutMilliseconds = RawInterByteTimeoutMilliseconds,
                PeriodicIntervalMilliseconds = periodicIntervalMilliseconds,
                AutoAppendCrc = autoAppendCrc,
                LastCommand = lastCommand,
                LogExportDirectory = logExportDirectory,
                AutomaticSendOnConnect = false,
                PeriodicSendEnabled = false,
            }.CreateValidatedCopy();
        }

        /// <summary>
        /// 取消后台事件订阅；物理串口的最终关闭由应用组合根统一执行。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            coordinator.BusyChanged -= HandleBusyChanged;
            coordinator.StateChanged -= HandleCoordinatorStateChanged;
        }

        /// <summary>
        /// 校验参数、打开串口并启动唯一接收循环；成功后不发送任何数据。
        /// </summary>
        /// <returns>打开和状态更新完成后的任务。</returns>
        private async Task ConnectAsync()
        {
            IsConnectionOperationBusy = true;
            ErrorMessage = string.Empty;
            NotifyCommandStates();

            try
            {
                SerialSettings settings = CreateSerialSettings();
                ValidateSlaveAddress();
                await transport.OpenAsync(settings, CancellationToken.None).ConfigureAwait(true);
                await coordinator.StartAsync(CancellationToken.None).ConfigureAwait(true);
                IsConnected = true;
                ConnectionStatus = "已连接 · 等待手动指令";
                SettingsChanged?.Invoke();
            }
            catch (Exception exception)
            {
                await SafeCloseAfterConnectFailureAsync().ConfigureAwait(true);
                IsConnected = false;
                ConnectionStatus = "连接失败";
                ErrorMessage = exception.Message;
            }
            finally
            {
                IsConnectionOperationBusy = false;
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 完成活动事务并关闭串口读取循环。
        /// </summary>
        /// <returns>断开和状态更新完成后的任务。</returns>
        private async Task DisconnectAsync()
        {
            IsConnectionOperationBusy = true;
            ErrorMessage = string.Empty;
            NotifyCommandStates();

            try
            {
                await coordinator.DisconnectAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ErrorMessage = exception.Message;
            }
            finally
            {
                IsConnected = false;
                ConnectionStatus = "未连接";
                IsConnectionOperationBusy = false;
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 连接失败时尽力恢复到确定的关闭状态，并保留首个异常给调用方显示。
        /// </summary>
        /// <returns>清理尝试结束后的任务。</returns>
        private async Task SafeCloseAfterConnectFailureAsync()
        {
            try
            {
                await coordinator.DisconnectAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception)
            {
                try
                {
                    await transport.CloseAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 首个连接异常更有诊断价值，二次清理异常不覆盖它。
                }
            }
        }

        /// <summary>
        /// 重新枚举系统串口，并在当前端口不存在时保留用户原选择供手工输入。
        /// </summary>
        private void RefreshPorts()
        {
            string[] ports = SerialPort.GetPortNames()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AvailablePorts.Clear();

            foreach (string port in ports)
            {
                AvailablePorts.Add(port);
            }

            if (!string.IsNullOrWhiteSpace(PortName) && !AvailablePorts.Contains(PortName))
            {
                AvailablePorts.Insert(0, PortName);
            }

            if (AvailablePorts.Count == 0)
            {
                AvailablePorts.Add("COM1");
            }
        }

        /// <summary>
        /// 恢复 9600-8-N-1、地址一和默认超时，不连接且不发送任何请求。
        /// </summary>
        private void ResetDefaults()
        {
            string retainedPort = string.IsNullOrWhiteSpace(PortName) ? "COM1" : PortName;
            SerialSettings defaults = SerialSettings.CreateDefault(retainedPort);
            BaudRate = defaults.BaudRate;
            DataBits = defaults.DataBits;
            Parity = defaults.Parity;
            StopBits = defaults.StopBits;
            SlaveAddress = 1;
            ResponseTimeoutMilliseconds = checked((int)defaults.ResponseTimeout.TotalMilliseconds);
            RawInterByteTimeoutMilliseconds = checked((int)defaults.RawInterByteTimeout.TotalMilliseconds);
            ErrorMessage = string.Empty;
            SettingsChanged?.Invoke();
        }

        /// <summary>
        /// 获取当前状态是否允许开始连接。
        /// </summary>
        /// <returns>未连接、无连接操作且无活动事务时返回真。</returns>
        private bool CanConnect()
        {
            return !IsConnected && !IsConnectionOperationBusy && !IsTransactionBusy;
        }

        /// <summary>
        /// 获取当前状态是否允许主动断开。
        /// </summary>
        /// <returns>已连接且没有连接操作或活动事务时返回真。</returns>
        private bool CanDisconnect()
        {
            return IsConnected && !IsConnectionOperationBusy && !IsTransactionBusy;
        }

        /// <summary>
        /// 获取当前状态是否允许编辑串口参数。
        /// </summary>
        /// <returns>未连接且没有连接操作时返回真。</returns>
        private bool CanEditSettings()
        {
            return !IsConnected && !IsConnectionOperationBusy;
        }

        /// <summary>
        /// 验证普通从站地址位于固件允许的 1 至 64。
        /// </summary>
        private void ValidateSlaveAddress()
        {
            if (SlaveAddress is < 1 or > 64)
            {
                throw new InvalidOperationException("普通 Modbus 从站地址必须位于 1 至 64。");
            }
        }

        /// <summary>
        /// 接收协调器忙状态并在界面线程刷新全部相关命令。
        /// </summary>
        /// <param name="isBusy">协调器唯一活动门是否被占用。</param>
        private void HandleBusyChanged(bool isBusy)
        {
            dispatcher.Post(
                () =>
                {
                    IsTransactionBusy = isBusy;
                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 接收协调器连接状态，在意外断开时同步界面显示。
        /// </summary>
        /// <param name="state">协调器新的连接或事务状态。</param>
        private void HandleCoordinatorStateChanged(TransactionCoordinatorState state)
        {
            if (state != TransactionCoordinatorState.Disconnected)
            {
                return;
            }

            dispatcher.Post(
                () =>
                {
                    IsConnected = false;
                    ConnectionStatus = "未连接";
                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 通知所有依赖连接、编辑和事务忙状态的命令重新计算可执行性。
        /// </summary>
        private void NotifyCommandStates()
        {
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            RefreshPortsCommand.NotifyCanExecuteChanged();
            ResetDefaultsCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 在从站地址改变后通知顶部状态和请求构建器。
        /// </summary>
        /// <param name="value">用户输入的新普通从站地址。</param>
        partial void OnSlaveAddressChanged(int value)
        {
            SettingsChanged?.Invoke();
        }

        /// <summary>
        /// 在波特率改变后通知顶部状态栏刷新。
        /// </summary>
        /// <param name="value">用户选择的新波特率。</param>
        partial void OnBaudRateChanged(int value)
        {
            SettingsChanged?.Invoke();
        }
    }
}
