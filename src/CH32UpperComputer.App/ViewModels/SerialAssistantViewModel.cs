using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Text;
using CH32UpperComputer.App.Collections;
using CH32UpperComputer.App.Services;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 管理独立串口助手的线路参数、UTF-8/HEX 收发、统一 TX/RX 画布和命令状态。
    /// </summary>
    public sealed partial class SerialAssistantViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 严格 UTF-8 发送编码；遇到无效 UTF-16 代理项时明确拒绝发送。
        /// </summary>
        private static readonly Encoding StrictUtf8Encoding =
            new UTF8Encoding(false, true);

        /// <summary>
        /// 带 BOM 的 UTF-8 文件编码，用于保存当前统一收发画布。
        /// </summary>
        private static readonly Encoding Utf8WithBom =
            new UTF8Encoding(true, false);

        /// <summary>
        /// 独占第二套传输和唯一接收消费循环的会话服务。
        /// </summary>
        private readonly SerialAssistantSessionService sessionService;

        /// <summary>
        /// 合并系统串口表与 PnP 友好名称的发现服务。
        /// </summary>
        private readonly ISerialPortDiscovery serialPortDiscovery;

        /// <summary>
        /// 把后台接收与状态事件切换到界面线程的调度器。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 保存当前可视 TX/RX 消息，并支持快照重绘时的一次性 Reset 通知。
        /// </summary>
        private readonly ResettableObservableCollection<SerialAssistantTrafficDisplayRecord>
            mutableTrafficRecords = [];

        /// <summary>
        /// 保护热插拔防抖取消源替换与释放的同步门。
        /// </summary>
        private readonly object portRefreshSyncRoot = new();

        /// <summary>
        /// 当前热插拔防抖等待使用的取消源。
        /// </summary>
        private CancellationTokenSource? portRefreshCancellation;

        /// <summary>
        /// 当前 UTF-8 增量显示解码器，跨接收批次保留多字节字符状态。
        /// </summary>
        private Decoder liveUtf8Decoder = CreateDisplayUtf8Decoder();

        /// <summary>
        /// 当前画布已经完整应用的统一 TX/RX 缓存修订号。
        /// </summary>
        private long renderedTrafficRevision;

        /// <summary>
        /// 当前视图允许接受的最早清空代次，旧代次的排队更新必须丢弃。
        /// </summary>
        private long acceptedClearVersion;

        /// <summary>
        /// 最近一项 RX 记录的时间，用于会话结束时标记残留的不完整 UTF-8 字符。
        /// </summary>
        private DateTimeOffset? lastReceiveRecordedAtUtc;

        /// <summary>
        /// 当前状态栏已经应用的最新统计修订号。
        /// </summary>
        private long appliedStatisticsRevision;

        /// <summary>
        /// 当前选择的操作系统串口名称。
        /// </summary>
        private string portName;

        /// <summary>
        /// 当前线路波特率。
        /// </summary>
        [ObservableProperty]
        private int baudRate;

        /// <summary>
        /// 当前线路数据位数。
        /// </summary>
        [ObservableProperty]
        private int dataBits;

        /// <summary>
        /// 当前线路奇偶校验方式。
        /// </summary>
        [ObservableProperty]
        private Parity parity;

        /// <summary>
        /// 当前线路停止位方式。
        /// </summary>
        [ObservableProperty]
        private StopBits stopBits;

        /// <summary>
        /// 当前发送输入模式。
        /// </summary>
        [ObservableProperty]
        private SerialAssistantDataMode sendMode;

        /// <summary>
        /// 当前统一收发画布的数据显示模式。
        /// </summary>
        [ObservableProperty]
        private SerialAssistantDataMode receiveMode;

        /// <summary>
        /// 发送时是否在末尾追加 CRLF。
        /// </summary>
        [ObservableProperty]
        private bool appendNewLine;

        /// <summary>
        /// 统一收发画布是否显示每项记录的时间戳。
        /// </summary>
        [ObservableProperty]
        private bool showTimestamps;

        /// <summary>
        /// 统一收发画布内容变化时是否自动滚动到底部。
        /// </summary>
        [ObservableProperty]
        private bool autoScroll;

        /// <summary>
        /// 定时发送间隔毫秒数。
        /// </summary>
        [ObservableProperty]
        private int periodicIntervalMilliseconds;

        /// <summary>
        /// 用户当前发送输入文本。
        /// </summary>
        [ObservableProperty]
        private string sendText;

        /// <summary>
        /// 当前按发生顺序包含 TX 与 RX 数据的只读串口画布文本。
        /// </summary>
        [ObservableProperty]
        private string receiveText = string.Empty;

        /// <summary>
        /// 最近一次保存当前统一收发画布的目录。
        /// </summary>
        [ObservableProperty]
        private string saveDirectory;

        /// <summary>
        /// 当前是否存在活动助手串口会话。
        /// </summary>
        [ObservableProperty]
        private bool isConnected;

        /// <summary>
        /// 当前是否正在执行连接或断开。
        /// </summary>
        [ObservableProperty]
        private bool isConnectionOperationBusy;

        /// <summary>
        /// 当前是否暂停保存与显示新接收数据。
        /// </summary>
        [ObservableProperty]
        private bool isPaused;

        /// <summary>
        /// 当前定时发送循环是否正在运行或收敛。
        /// </summary>
        [ObservableProperty]
        private bool isPeriodicSending;

        /// <summary>
        /// 当前是否已经进入应用退出流程。
        /// </summary>
        [ObservableProperty]
        private bool isShuttingDown;

        /// <summary>
        /// 当前连接和运行状态说明。
        /// </summary>
        [ObservableProperty]
        private string connectionStatus = "未连接";

        /// <summary>
        /// 最近一次参数、编码、文件或串口错误。
        /// </summary>
        [ObservableProperty]
        private string errorMessage = string.Empty;

        /// <summary>
        /// 当前串口发现数量、推荐端口或驱动提示。
        /// </summary>
        [ObservableProperty]
        private string portDiscoveryStatus = "正在识别串口…";

        /// <summary>
        /// 成功完成的发送操作次数。
        /// </summary>
        [ObservableProperty]
        private long transmitOperationCount;

        /// <summary>
        /// 成功完成发送的线路字节数。
        /// </summary>
        [ObservableProperty]
        private long transmitBytes;

        /// <summary>
        /// 已从驱动排空的接收线路字节数。
        /// </summary>
        [ObservableProperty]
        private long receiveBytes;

        /// <summary>
        /// 因暂停或缓存淘汰而未保留的字节总数。
        /// </summary>
        [ObservableProperty]
        private long discardedBytes;

        /// <summary>
        /// 暂停期间读取但未保留的字节数。
        /// </summary>
        [ObservableProperty]
        private long pausedDiscardedBytes;

        /// <summary>
        /// 指示 ViewModel 已释放并取消后台事件订阅。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 初始化串口助手界面，并应用经过验证且不包含运行状态的持久化偏好。
        /// </summary>
        /// <param name="sessionService">独占第二套传输的助手会话服务。</param>
        /// <param name="serialPortDiscovery">负责异步发现系统串口的服务。</param>
        /// <param name="dispatcher">负责把后台事件投递到界面线程的调度器。</param>
        /// <param name="preferences">已经加载的助手嵌套偏好。</param>
        public SerialAssistantViewModel(
            SerialAssistantSessionService sessionService,
            ISerialPortDiscovery serialPortDiscovery,
            IUiDispatcher dispatcher,
            SerialAssistantPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(sessionService);
            ArgumentNullException.ThrowIfNull(serialPortDiscovery);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(preferences);
            SerialAssistantPreferences safePreferences = preferences.CreateValidatedCopy();
            this.sessionService = sessionService;
            this.serialPortDiscovery = serialPortDiscovery;
            this.dispatcher = dispatcher;
            portName = safePreferences.PortName;
            baudRate = safePreferences.BaudRate;
            dataBits = safePreferences.DataBits;
            parity = safePreferences.Parity;
            stopBits = safePreferences.StopBits;
            sendMode = safePreferences.SendMode;
            receiveMode = safePreferences.ReceiveMode;
            appendNewLine = safePreferences.AppendNewLine;
            showTimestamps = safePreferences.ShowTimestamps;
            autoScroll = safePreferences.AutoScroll;
            periodicIntervalMilliseconds = safePreferences.PeriodicIntervalMilliseconds;
            sendText = safePreferences.LastInput;
            saveDirectory = safePreferences.SaveDirectory;
            TrafficRecords = new ReadOnlyObservableCollection<SerialAssistantTrafficDisplayRecord>(
                mutableTrafficRecords);
            AvailablePorts = new ObservableCollection<SerialPortDescriptor>();
            BaudRates = Array.AsReadOnly(new[] { 2400, 4800, 9600, 19200, 38400, 57600, 115200 });
            DataBitOptions = Array.AsReadOnly(new[] { 5, 6, 7, 8 });
            ParityOptions = Array.AsReadOnly(new[] { Parity.None, Parity.Even, Parity.Odd, Parity.Mark, Parity.Space });
            StopBitOptions = Array.AsReadOnly(new[] { StopBits.One, StopBits.Two, StopBits.OnePointFive });
            DataModeOptions = Array.AsReadOnly(
                new[]
                {
                    new SerialAssistantDataModeOption(
                        SerialAssistantDataMode.Utf8,
                        "UTF-8"),
                    new SerialAssistantDataModeOption(
                        SerialAssistantDataMode.Hex,
                        "HEX"),
                });
            ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
            DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
            RefreshPortsCommand = new AsyncRelayCommand(RefreshPortsFromCommandAsync, CanRefreshPorts);
            SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
            StartPeriodicCommand = new RelayCommand(StartPeriodic, CanStartPeriodic);
            StopPeriodicCommand = new AsyncRelayCommand(StopPeriodicAsync, CanStopPeriodic);
            TogglePauseCommand = new RelayCommand(TogglePause, CanTogglePause);
            ClearCommand = new RelayCommand(ClearReceive, CanClear);
            sessionService.StateChanged += HandleStateChanged;
            sessionService.TrafficUpdated += HandleTrafficUpdated;
            sessionService.StatisticsChanged += HandleStatisticsChanged;
            sessionService.PeriodicSendingChanged += HandlePeriodicSendingChanged;
            ApplyStatistics(sessionService.Statistics);
            SerialAssistantTrafficSnapshot initialSnapshot =
                sessionService.CreateTrafficSnapshotState();
            renderedTrafficRevision = initialSnapshot.TrafficRevision;
            acceptedClearVersion = initialSnapshot.ClearVersion;
        }

        /// <summary>
        /// 获取或设置待打开串口名称；连接和驱动清理期间拒绝切换。
        /// </summary>
        public string PortName
        {
            get => portName;
            set
            {
                string candidate = value ?? string.Empty;

                if (!CanEditSerialSettings &&
                    !string.Equals(candidate, portName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SetProperty(ref portName, candidate);
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 获取当前发现的系统串口描述集合。
        /// </summary>
        public ObservableCollection<SerialPortDescriptor> AvailablePorts { get; }

        /// <summary>
        /// 获取按发生顺序排列、供分色串口画布绑定的只读 TX/RX 消息集合。
        /// </summary>
        public ReadOnlyObservableCollection<SerialAssistantTrafficDisplayRecord> TrafficRecords
        {
            get;
        }

        /// <summary>
        /// 获取普通串口助手固定支持的七档波特率。
        /// </summary>
        public IReadOnlyList<int> BaudRates { get; }

        /// <summary>
        /// 获取可选择的数据位数。
        /// </summary>
        public IReadOnlyList<int> DataBitOptions { get; }

        /// <summary>
        /// 获取可选择的奇偶校验方式。
        /// </summary>
        public IReadOnlyList<Parity> ParityOptions { get; }

        /// <summary>
        /// 获取可选择的停止位方式。
        /// </summary>
        public IReadOnlyList<StopBits> StopBitOptions { get; }

        /// <summary>
        /// 获取 UTF-8 与 HEX 两种可选择的数据模式。
        /// </summary>
        public IReadOnlyList<SerialAssistantDataModeOption> DataModeOptions { get; }

        /// <summary>
        /// 获取暂停按钮随当前状态显示的明确动作文本。
        /// </summary>
        public string PauseButtonText => IsPaused ? "恢复接收" : "暂停接收";

        /// <summary>
        /// 获取连接期间锁定线路参数的统一编辑开关。
        /// </summary>
        public bool CanEditSerialSettings =>
            !IsConnected &&
            !IsConnectionOperationBusy &&
            !IsShuttingDown;

        /// <summary>
        /// 获取定时发送期间锁定发送模式、换行开关与输入内容的统一编辑开关。
        /// </summary>
        public bool CanEditPayload => !IsPeriodicSending && !IsShuttingDown;

        /// <summary>
        /// 获取打开独立助手串口的命令。
        /// </summary>
        public IAsyncRelayCommand ConnectCommand { get; }

        /// <summary>
        /// 获取停止定时发送并断开助手串口的命令。
        /// </summary>
        public IAsyncRelayCommand DisconnectCommand { get; }

        /// <summary>
        /// 获取刷新系统串口列表的命令。
        /// </summary>
        public IAsyncRelayCommand RefreshPortsCommand { get; }

        /// <summary>
        /// 获取不等待响应的单次发送命令。
        /// </summary>
        public IAsyncRelayCommand SendCommand { get; }

        /// <summary>
        /// 获取冻结当前负载并启动定时发送的命令。
        /// </summary>
        public IRelayCommand StartPeriodicCommand { get; }

        /// <summary>
        /// 获取停止定时发送并等待当前写入收敛的命令。
        /// </summary>
        public IAsyncRelayCommand StopPeriodicCommand { get; }

        /// <summary>
        /// 获取切换暂停接收显示状态的命令。
        /// </summary>
        public IRelayCommand TogglePauseCommand { get; }

        /// <summary>
        /// 获取清空视图、原始缓存和全部收发统计的命令。
        /// </summary>
        public IRelayCommand ClearCommand { get; }

        /// <summary>
        /// 异步发现串口，并在界面线程完整替换助手端口列表。
        /// </summary>
        /// <param name="cancellationToken">取消尚未完成的系统设备查询。</param>
        /// <returns>端口查询和界面提交完成后的任务。</returns>
        public async Task RefreshPortsAsync(CancellationToken cancellationToken)
        {
            SerialPortDiscoveryResult result;

            try
            {
                result = await serialPortDiscovery
                    .DiscoverAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                result = new SerialPortDiscoveryResult(
                    Array.Empty<SerialPortDescriptor>(),
                    $"串口识别失败：{exception.Message} 请检查 USB 连接和串口驱动。");
            }

            if (dispatcher.CheckAccess)
            {
                ApplyDiscoveredPorts(result);
                return;
            }

            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.Post(
                () =>
                {
                    ApplyDiscoveredPorts(result);
                    completion.TrySetResult();
                });
            await completion.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// 以 250 毫秒防抖安排一次热插拔端口刷新。
        /// </summary>
        public void SchedulePortRefresh()
        {
            CancellationTokenSource cancellation;

            lock (portRefreshSyncRoot)
            {
                if (isDisposed)
                {
                    return;
                }

                portRefreshCancellation?.Cancel();
                portRefreshCancellation?.Dispose();
                portRefreshCancellation = new CancellationTokenSource();
                cancellation = portRefreshCancellation;
            }

            _ = RefreshPortsAfterDelayAsync(cancellation);
        }

        /// <summary>
        /// 根据当前输入模式严格构造最大 64 KiB 的线路负载，并按需追加 CRLF。
        /// </summary>
        /// <param name="payload">成功时返回独立的完整线路负载。</param>
        /// <param name="errorMessage">失败时返回可直接显示的中文校验错误。</param>
        /// <returns>输入合法且总长度不超过 64 KiB 时返回真。</returns>
        public bool TryBuildPayload(
            out ReadOnlyMemory<byte> payload,
            out string errorMessage)
        {
            int contentLimit = AppendNewLine
                ? SerialAssistantSessionService.MaximumPayloadBytes - 2
                : SerialAssistantSessionService.MaximumPayloadBytes;
            byte[] content;

            if (SendMode == SerialAssistantDataMode.Hex)
            {
                HexFrameParseResult parseResult = HexFrameParser.TryParse(
                    SendText,
                    contentLimit);

                if (!parseResult.IsSuccess)
                {
                    payload = ReadOnlyMemory<byte>.Empty;
                    errorMessage = parseResult.ErrorMessage ?? "HEX 输入无效。";
                    return false;
                }

                content = parseResult.Bytes.ToArray();
            }
            else
            {
                try
                {
                    content = StrictUtf8Encoding.GetBytes(SendText ?? string.Empty);
                }
                catch (EncoderFallbackException)
                {
                    payload = ReadOnlyMemory<byte>.Empty;
                    errorMessage = "发送文本包含无效的 UTF-16 代理项，无法严格编码为 UTF-8。";
                    return false;
                }

                if (content.Length == 0)
                {
                    payload = ReadOnlyMemory<byte>.Empty;
                    errorMessage = "UTF-8 发送文本不能为空。";
                    return false;
                }

                if (content.Length > contentLimit)
                {
                    payload = ReadOnlyMemory<byte>.Empty;
                    errorMessage = $"UTF-8 负载超过允许的最大长度 {contentLimit} 字节。";
                    return false;
                }
            }

            if (!AppendNewLine)
            {
                payload = content;
                errorMessage = string.Empty;
                return true;
            }

            byte[] withNewLine = new byte[checked(content.Length + 2)];
            content.CopyTo(withNewLine, 0);
            withNewLine[^2] = 0x0D;
            withNewLine[^1] = 0x0A;
            payload = withNewLine;
            errorMessage = string.Empty;
            return true;
        }

        /// <summary>
        /// 将当前统一 TX/RX 画布按 UTF-8 BOM 原样保存到用户选择的文本文件。
        /// </summary>
        /// <param name="filePath">用户选择的目标 <c>.txt</c> 路径。</param>
        /// <param name="cancellationToken">取消尚未完成的文本文件写入。</param>
        /// <returns>当前视图和 BOM 全部写入目标文件后的任务。</returns>
        public async Task SaveCurrentViewAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("保存文件路径不能为空。", nameof(filePath));
            }

            string fullPath = Path.GetFullPath(filePath.Trim());
            string? directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                fullPath,
                ReceiveText,
                Utf8WithBom,
                cancellationToken).ConfigureAwait(true);
            SaveDirectory = directory ?? string.Empty;
        }

        /// <summary>
        /// 创建只包含可持久化偏好的安全副本，连接、暂停和定时运行状态不在模型中。
        /// </summary>
        /// <returns>经过完整校验的助手嵌套偏好。</returns>
        public SerialAssistantPreferences CreatePreferences()
        {
            return new SerialAssistantPreferences
            {
                PortName = PortName,
                BaudRate = BaudRate,
                DataBits = DataBits,
                Parity = Parity,
                StopBits = StopBits,
                SendMode = SendMode,
                ReceiveMode = ReceiveMode,
                AppendNewLine = AppendNewLine,
                ShowTimestamps = ShowTimestamps,
                AutoScroll = AutoScroll,
                PeriodicIntervalMilliseconds = PeriodicIntervalMilliseconds,
                SaveDirectory = SaveDirectory,
                LastInput = SendText,
            }.CreateValidatedCopy();
        }

        /// <summary>
        /// 禁用新命令并通知会话退出流程开始。
        /// </summary>
        public void BeginShutdown()
        {
            IsShuttingDown = true;
            NotifyCommandStates();
        }

        /// <summary>
        /// 取消全部后台事件订阅和端口刷新；会话服务由应用组合根异步释放。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            sessionService.StateChanged -= HandleStateChanged;
            sessionService.TrafficUpdated -= HandleTrafficUpdated;
            sessionService.StatisticsChanged -= HandleStatisticsChanged;
            sessionService.PeriodicSendingChanged -= HandlePeriodicSendingChanged;

            lock (portRefreshSyncRoot)
            {
                portRefreshCancellation?.Cancel();
                portRefreshCancellation?.Dispose();
                portRefreshCancellation = null;
            }
        }

        /// <summary>
        /// 校验当前线路参数并打开独立助手会话。
        /// </summary>
        /// <returns>连接和界面状态提交完成后的任务。</returns>
        private async Task ConnectAsync()
        {
            IsConnectionOperationBusy = true;
            ErrorMessage = string.Empty;
            NotifyCommandStates();

            try
            {
                SerialLineSettings settings = new(
                    PortName,
                    BaudRate,
                    DataBits,
                    Parity,
                    StopBits);
                await sessionService.ConnectAsync(
                    settings,
                    CancellationToken.None).ConfigureAwait(true);
                IsConnected = true;
                IsPaused = false;
                liveUtf8Decoder = CreateDisplayUtf8Decoder();
                ConnectionStatus = $"已连接 {settings.PortName}";
            }
            catch (Exception exception)
            {
                IsConnected = false;
                ConnectionStatus = "连接失败";
                ErrorMessage = SerialAssistantSessionService.TranslateSerialException(
                    exception,
                    PortName,
                    "打开");
            }
            finally
            {
                IsConnectionOperationBusy = false;
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 停止定时发送、断开会话并恢复非暂停状态。
        /// </summary>
        /// <returns>助手读取循环已经收敛后的任务。</returns>
        private async Task DisconnectAsync()
        {
            IsConnectionOperationBusy = true;
            ErrorMessage = string.Empty;
            NotifyCommandStates();

            try
            {
                await sessionService.DisconnectAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ErrorMessage = SerialAssistantSessionService.TranslateSerialException(
                    exception,
                    PortName,
                    "断开");
            }
            finally
            {
                IsConnected = false;
                IsPaused = false;
                IsPeriodicSending = false;
                ConnectionStatus = "未连接";
                IsConnectionOperationBusy = false;
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 严格构造并单次发送当前负载；成功写入后由会话事件把 TX 追加到统一画布。
        /// </summary>
        /// <returns>发送操作及界面错误提交完成后的任务。</returns>
        private async Task SendAsync()
        {
            ErrorMessage = string.Empty;

            if (!TryBuildPayload(out ReadOnlyMemory<byte> payload, out string validationError))
            {
                ErrorMessage = validationError;
                return;
            }

            try
            {
                await sessionService.SendAsync(payload, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ErrorMessage = SerialAssistantSessionService.TranslateSerialException(
                    exception,
                    PortName,
                    "发送");
            }
        }

        /// <summary>
        /// 冻结当前严格解析后的负载并启动定时发送。
        /// </summary>
        private void StartPeriodic()
        {
            ErrorMessage = string.Empty;

            if (!TryBuildPayload(out ReadOnlyMemory<byte> payload, out string validationError))
            {
                ErrorMessage = validationError;
                return;
            }

            try
            {
                sessionService.StartPeriodicSending(
                    payload,
                    TimeSpan.FromMilliseconds(PeriodicIntervalMilliseconds));
                IsPeriodicSending = true;
                NotifyCommandStates();
            }
            catch (Exception exception)
            {
                ErrorMessage = SerialAssistantSessionService.TranslateSerialException(
                    exception,
                    PortName,
                    "启动定时发送");
            }
        }

        /// <summary>
        /// 停止定时发送并等待当前唯一写入收敛。
        /// </summary>
        /// <returns>定时循环完全退出后的任务。</returns>
        private async Task StopPeriodicAsync()
        {
            await sessionService.StopPeriodicSendingAsync().ConfigureAwait(true);
            IsPeriodicSending = false;
            NotifyCommandStates();
        }

        /// <summary>
        /// 切换暂停状态；暂停不停止驱动读取，仅停止保存新数据。
        /// </summary>
        private void TogglePause()
        {
            IsPaused = !IsPaused;
            sessionService.SetPaused(IsPaused);
            NotifyCommandStates();
        }

        /// <summary>
        /// 清除视图、原始缓存、UTF-8 Decoder 状态和全部统计。
        /// </summary>
        private void ClearReceive()
        {
            SerialAssistantReceiveSnapshot clearedSnapshot =
                sessionService.ClearReceiveData();
            SerialAssistantTrafficSnapshot clearedTrafficSnapshot =
                sessionService.CreateTrafficSnapshotState();
            acceptedClearVersion = clearedSnapshot.ClearVersion;
            renderedTrafficRevision = clearedTrafficSnapshot.TrafficRevision;
            liveUtf8Decoder = CreateDisplayUtf8Decoder();
            lastReceiveRecordedAtUtc = null;
            mutableTrafficRecords.Clear();
            ReceiveText = string.Empty;
            ApplyStatistics(sessionService.Statistics);
        }

        /// <summary>
        /// 接收后台连接状态并在界面线程更新可见状态。
        /// </summary>
        /// <param name="change">不可变连接状态变化。</param>
        private void HandleStateChanged(SerialAssistantSessionStateChange change)
        {
            dispatcher.Post(
                () =>
                {
                    ConnectionStatus = change.Message;

                    if (change.State == SerialAssistantSessionState.Faulted)
                    {
                        CompleteCurrentUtf8Sequence();
                        IsConnected = false;
                        IsPaused = false;
                        ErrorMessage = change.Message;
                    }
                    else if (change.State == SerialAssistantSessionState.Connected)
                    {
                        IsConnected = true;

                        if (change.Message.Contains("失败", StringComparison.Ordinal))
                        {
                            ErrorMessage = change.Message;
                        }
                    }
                    else if (change.State == SerialAssistantSessionState.Disconnected)
                    {
                        CompleteCurrentUtf8Sequence();
                        IsConnected = false;
                        IsPaused = false;
                    }

                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 接收后台统一收发缓存更新，并按当前模式增量追加或从快照完整重绘。
        /// </summary>
        /// <param name="update">新 TX/RX 记录、缓存淘汰标记和排序版本。</param>
        private void HandleTrafficUpdated(SerialAssistantTrafficUpdate update)
        {
            dispatcher.Post(
                () =>
                {
                    if (update.ClearVersion < acceptedClearVersion ||
                        update.TrafficRevision <= renderedTrafficRevision)
                    {
                        return;
                    }

                    if (update.ClearVersion > acceptedClearVersion ||
                        update.RequiresFullRefresh ||
                        update.TrafficRevision != renderedTrafficRevision + 1)
                    {
                        RefreshTrafficTextFromSnapshot();
                        return;
                    }

                    AppendTrafficRecord(RenderTrafficBatchIncrementally(update.Batch));
                    renderedTrafficRevision = update.TrafficRevision;
                });
        }

        /// <summary>
        /// 接收后台统计快照并在界面线程更新状态栏。
        /// </summary>
        /// <param name="statistics">最新不可变统计快照。</param>
        private void HandleStatisticsChanged(SerialAssistantStatistics statistics)
        {
            dispatcher.Post(
                () =>
                {
                    if (statistics.ClearVersion < acceptedClearVersion)
                    {
                        return;
                    }

                    if (statistics.StatisticsRevision <= appliedStatisticsRevision)
                    {
                        return;
                    }

                    ApplyStatistics(statistics);
                });
        }

        /// <summary>
        /// 接收定时发送运行开关并刷新全部相关命令。
        /// </summary>
        /// <param name="isRunning">定时发送仍在运行或收敛时为真。</param>
        private void HandlePeriodicSendingChanged(bool isRunning)
        {
            dispatcher.Post(
                () =>
                {
                    IsPeriodicSending = isRunning;
                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 从 512 KiB 有界统一收发缓存按当前模式和时间戳选项完整重绘画布。
        /// </summary>
        private void RefreshTrafficTextFromSnapshot()
        {
            SerialAssistantTrafficSnapshot snapshot =
                sessionService.CreateTrafficSnapshotState();
            StringBuilder builder = new();
            List<SerialAssistantTrafficDisplayRecord> records = [];
            liveUtf8Decoder = CreateDisplayUtf8Decoder();
            lastReceiveRecordedAtUtc = null;
            int? previousReceivePortGeneration = null;

            foreach (SerialAssistantTrafficBatch batch in snapshot.Batches)
            {
                if (ReceiveMode == SerialAssistantDataMode.Utf8 &&
                    batch.Direction == SerialAssistantTrafficDirection.Receive &&
                    previousReceivePortGeneration.HasValue &&
                    previousReceivePortGeneration.Value != batch.PortGeneration)
                {
                    AddTrafficRecordToSnapshot(
                        CompletePendingReceiveRecord(),
                        records,
                        builder);
                }

                AddTrafficRecordToSnapshot(
                    RenderTrafficBatchIncrementally(batch),
                    records,
                    builder);

                if (batch.Direction == SerialAssistantTrafficDirection.Receive)
                {
                    previousReceivePortGeneration = batch.PortGeneration;
                }
            }

            if (ReceiveMode == SerialAssistantDataMode.Utf8 && !IsConnected)
            {
                AddTrafficRecordToSnapshot(
                    CompletePendingReceiveRecord(),
                    records,
                    builder);
            }

            mutableTrafficRecords.ReplaceAll(records);
            ReceiveText = builder.ToString();
            acceptedClearVersion = snapshot.ClearVersion;
            renderedTrafficRevision = snapshot.TrafficRevision;
        }

        /// <summary>
        /// 按当前 UTF-8 或 HEX 模式渲染一项 TX/RX 记录，并为 RX 保留跨批次 Decoder 状态。
        /// </summary>
        /// <param name="batch">待增量渲染的统一收发记录。</param>
        /// <returns>可分色的双行画布记录；尚无完整 UTF-8 字符时为空。</returns>
        private SerialAssistantTrafficDisplayRecord? RenderTrafficBatchIncrementally(
            SerialAssistantTrafficBatch batch)
        {
            string renderedData;

            if (ReceiveMode == SerialAssistantDataMode.Hex)
            {
                renderedData = HexFrameParser.Format(batch.Data.Span);
            }
            else if (batch.Direction == SerialAssistantTrafficDirection.Receive)
            {
                renderedData = DecodeUtf8(batch.Data.Span, liveUtf8Decoder, false);
                lastReceiveRecordedAtUtc = batch.RecordedAtUtc;
            }
            else
            {
                renderedData = DecodeUtf8(
                    batch.Data.Span,
                    CreateDisplayUtf8Decoder(),
                    true);
            }

            if (renderedData.Length == 0)
            {
                return null;
            }

            return CreateTrafficDisplayRecord(
                batch.Direction,
                batch.RecordedAtUtc,
                renderedData);
        }

        /// <summary>
        /// 将当前 RX Decoder 中残留的不完整 UTF-8 序列输出为替代字符并开始新序列。
        /// </summary>
        /// <returns>存在残留字符时返回 RX 双行记录，否则返回空。</returns>
        private SerialAssistantTrafficDisplayRecord? CompletePendingReceiveRecord()
        {
            string renderedData = DecodeUtf8(
                ReadOnlySpan<byte>.Empty,
                liveUtf8Decoder,
                true);
            liveUtf8Decoder = CreateDisplayUtf8Decoder();

            if (renderedData.Length == 0)
            {
                return null;
            }

            return CreateTrafficDisplayRecord(
                SerialAssistantTrafficDirection.Receive,
                lastReceiveRecordedAtUtc ?? DateTimeOffset.UtcNow,
                renderedData);
        }

        /// <summary>
        /// 为统一串口画布创建秒级标题、规范化正文和 TX/RX 方向语义。
        /// </summary>
        /// <param name="direction">当前记录的发送或接收方向。</param>
        /// <param name="recordedAtUtc">数据实际发送完成或到达线路的 UTC 时刻。</param>
        /// <param name="renderedData">已经按当前显示模式转换的非空数据文本。</param>
        /// <returns>可以绑定到分色列表并保存为纯文本的双行消息。</returns>
        private SerialAssistantTrafficDisplayRecord CreateTrafficDisplayRecord(
            SerialAssistantTrafficDirection direction,
            DateTimeOffset recordedAtUtc,
            string renderedData)
        {
            string directionLabel = direction == SerialAssistantTrafficDirection.Transmit
                ? "TX"
                : "RX";
            string headerText = ShowTimestamps
                ? $"[{recordedAtUtc.ToLocalTime():HH:mm:ss}]  {directionLabel}"
                : directionLabel;
            string payloadText = NormalizeTrafficPayload(renderedData);
            return new SerialAssistantTrafficDisplayRecord(
                direction,
                headerText,
                payloadText);
        }

        /// <summary>
        /// 把一项增量记录同时追加到彩色画布集合和可保存纯文本视图。
        /// </summary>
        /// <param name="record">已完成解码的消息；不完整 UTF-8 尚无输出时为空。</param>
        private void AppendTrafficRecord(SerialAssistantTrafficDisplayRecord? record)
        {
            if (record is null)
            {
                return;
            }

            mutableTrafficRecords.Add(record);
            ReceiveText += record.PlainText;
        }

        /// <summary>
        /// 把重绘阶段产生的一项记录同步写入集合快照与纯文本构建器。
        /// </summary>
        /// <param name="record">已完成解码的消息；不完整 UTF-8 尚无输出时为空。</param>
        /// <param name="records">按顺序累积的分色消息集合。</param>
        /// <param name="builder">按相同顺序累积的保存文本。</param>
        private static void AddTrafficRecordToSnapshot(
            SerialAssistantTrafficDisplayRecord? record,
            ICollection<SerialAssistantTrafficDisplayRecord> records,
            StringBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(builder);

            if (record is null)
            {
                return;
            }

            records.Add(record);
            builder.Append(record.PlainText);
        }

        /// <summary>
        /// 统一串口正文换行，并移除线路尾部分隔换行，避免每项消息产生额外空白正文。
        /// </summary>
        /// <param name="renderedData">UTF-8 或 HEX 转换后的原始显示文本。</param>
        /// <returns>内部使用 CRLF 且不含尾部 CR/LF 的画布正文。</returns>
        private static string NormalizeTrafficPayload(string renderedData)
        {
            ArgumentNullException.ThrowIfNull(renderedData);
            string normalized = renderedData
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .TrimEnd('\n');
            return normalized.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        /// <summary>
        /// 使用替代回退的同一 Decoder 解码 UTF-8，避免多字节字符被批次边界截断。
        /// </summary>
        /// <param name="bytes">当前批次原始字节。</param>
        /// <param name="decoder">跨批次复用的 UTF-8 Decoder。</param>
        /// <param name="flush">是否将当前批次视为输入末尾并输出替代字符。</param>
        /// <returns>当前调用可以完整解码的文本。</returns>
        private static string DecodeUtf8(
            ReadOnlySpan<byte> bytes,
            Decoder decoder,
            bool flush)
        {
            char[] characters = new char[Math.Max(1, bytes.Length + 1)];
            decoder.Convert(
                bytes,
                characters,
                flush,
                out int bytesUsed,
                out int charactersUsed,
                out bool completed);

            if (bytesUsed != bytes.Length || !completed)
            {
                throw new InvalidOperationException("UTF-8 接收解码缓冲不足，无法完成当前批次。");
            }

            return charactersUsed == 0
                ? string.Empty
                : new string(characters, 0, charactersUsed);
        }

        /// <summary>
        /// 创建非法序列显示替代字符、并支持跨批次状态的 UTF-8 Decoder。
        /// </summary>
        /// <returns>全新 UTF-8 增量显示 Decoder。</returns>
        private static Decoder CreateDisplayUtf8Decoder()
        {
            return new UTF8Encoding(false, false).GetDecoder();
        }

        /// <summary>
        /// 在会话终止时把尾部不完整 UTF-8 序列显示为替代字符，并重置下一会话 Decoder。
        /// </summary>
        private void CompleteCurrentUtf8Sequence()
        {
            if (ReceiveMode == SerialAssistantDataMode.Utf8)
            {
                AppendTrafficRecord(CompletePendingReceiveRecord());
            }

            liveUtf8Decoder = CreateDisplayUtf8Decoder();
            lastReceiveRecordedAtUtc = null;
        }

        /// <summary>
        /// 将不可变统计快照映射到状态栏属性。
        /// </summary>
        /// <param name="statistics">待显示的最新统计快照。</param>
        private void ApplyStatistics(SerialAssistantStatistics statistics)
        {
            TransmitOperationCount = statistics.TransmitOperationCount;
            TransmitBytes = statistics.TransmitBytes;
            ReceiveBytes = statistics.ReceiveBytes;
            DiscardedBytes = statistics.DiscardedBytes;
            PausedDiscardedBytes = statistics.PausedDiscardedBytes;
            appliedStatisticsRevision = statistics.StatisticsRevision;
        }

        /// <summary>
        /// 在界面线程替换助手端口列表，并保留未连接时的当前选择。
        /// </summary>
        /// <param name="result">串口发现服务返回的端口和状态。</param>
        private void ApplyDiscoveredPorts(SerialPortDiscoveryResult result)
        {
            AvailablePorts.Clear();

            foreach (SerialPortDescriptor descriptor in result.Ports)
            {
                AvailablePorts.Add(descriptor);
            }

            PortDiscoveryStatus = string.IsNullOrWhiteSpace(result.DiagnosticMessage)
                ? $"已识别 {result.Ports.Count} 个串口"
                : result.DiagnosticMessage;

            if (!IsConnected &&
                AvailablePorts.Count > 0 &&
                !AvailablePorts.Any(
                    descriptor => string.Equals(
                        descriptor.PortName,
                        PortName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                PortName = AvailablePorts[0].PortName;
            }
        }

        /// <summary>
        /// 在热插拔防抖结束后刷新端口，并仅释放属于本次请求的取消源。
        /// </summary>
        /// <param name="cancellation">本次刷新请求的唯一取消源。</param>
        /// <returns>延迟、查询和安全清理完成后的任务。</returns>
        private async Task RefreshPortsAfterDelayAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await Task.Delay(250, cancellation.Token).ConfigureAwait(false);
                await RefreshPortsAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 更新请求被更晚热插拔事件合并，保持静默即可。
            }
            finally
            {
                lock (portRefreshSyncRoot)
                {
                    if (ReferenceEquals(portRefreshCancellation, cancellation))
                    {
                        portRefreshCancellation = null;
                    }
                }

                cancellation.Dispose();
            }
        }

        /// <summary>
        /// 由刷新命令执行一次即时端口查询。
        /// </summary>
        /// <returns>端口查询完成后的任务。</returns>
        private Task RefreshPortsFromCommandAsync()
        {
            return RefreshPortsAsync(CancellationToken.None);
        }

        /// <summary>
        /// 获取当前是否允许连接独立助手串口。
        /// </summary>
        /// <returns>未连接、未忙且未退出时返回真。</returns>
        private bool CanConnect()
        {
            return CanEditSerialSettings && !string.IsNullOrWhiteSpace(PortName);
        }

        /// <summary>
        /// 获取当前是否允许断开独立助手串口。
        /// </summary>
        /// <returns>已连接且未执行另一连接操作时返回真。</returns>
        private bool CanDisconnect()
        {
            return IsConnected && !IsConnectionOperationBusy;
        }

        /// <summary>
        /// 获取当前是否允许刷新串口列表。
        /// </summary>
        /// <returns>未处于退出流程时返回真。</returns>
        private bool CanRefreshPorts()
        {
            return !IsShuttingDown;
        }

        /// <summary>
        /// 获取当前是否允许执行单次发送。
        /// </summary>
        /// <returns>已连接且未运行定时发送时返回真。</returns>
        private bool CanSend()
        {
            return IsConnected && !IsPeriodicSending && !IsShuttingDown;
        }

        /// <summary>
        /// 获取当前是否允许启动定时发送。
        /// </summary>
        /// <returns>已连接、尚未定时且间隔合法时返回真。</returns>
        private bool CanStartPeriodic()
        {
            return IsConnected &&
                !IsPeriodicSending &&
                !IsShuttingDown &&
                PeriodicIntervalMilliseconds is >= 100 and <= 3_600_000;
        }

        /// <summary>
        /// 获取当前是否允许停止定时发送。
        /// </summary>
        /// <returns>定时发送运行或收敛时返回真。</returns>
        private bool CanStopPeriodic()
        {
            return IsPeriodicSending;
        }

        /// <summary>
        /// 获取当前是否允许切换暂停状态。
        /// </summary>
        /// <returns>已连接且未退出时返回真。</returns>
        private bool CanTogglePause()
        {
            return IsConnected && !IsShuttingDown;
        }

        /// <summary>
        /// 获取当前是否允许清空缓存与统计。
        /// </summary>
        /// <returns>未进入退出流程时返回真。</returns>
        private bool CanClear()
        {
            return !IsShuttingDown;
        }

        /// <summary>
        /// 刷新全部连接、发送、定时、暂停和清空命令状态及派生编辑属性。
        /// </summary>
        private void NotifyCommandStates()
        {
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            RefreshPortsCommand.NotifyCanExecuteChanged();
            SendCommand.NotifyCanExecuteChanged();
            StartPeriodicCommand.NotifyCanExecuteChanged();
            StopPeriodicCommand.NotifyCanExecuteChanged();
            TogglePauseCommand.NotifyCanExecuteChanged();
            ClearCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanEditSerialSettings));
            OnPropertyChanged(nameof(CanEditPayload));
        }

        /// <summary>
        /// 在线路参数变化后刷新连接命令状态。
        /// </summary>
        /// <param name="value">新的波特率。</param>
        partial void OnBaudRateChanged(int value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 在发送输入变化后刷新发送和定时命令状态。
        /// </summary>
        /// <param name="value">新的发送输入。</param>
        partial void OnSendTextChanged(string value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 在定时间隔变化后刷新启动命令状态。
        /// </summary>
        /// <param name="value">新的间隔毫秒数。</param>
        partial void OnPeriodicIntervalMillisecondsChanged(int value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 在接收模式变化后从原始缓存完整重绘。
        /// </summary>
        /// <param name="value">新的接收模式。</param>
        partial void OnReceiveModeChanged(SerialAssistantDataMode value)
        {
            _ = value;
            RefreshTrafficTextFromSnapshot();
        }

        /// <summary>
        /// 在时间戳显示开关变化后从原始缓存完整重绘。
        /// </summary>
        /// <param name="value">新的时间戳显示开关。</param>
        partial void OnShowTimestampsChanged(bool value)
        {
            _ = value;
            RefreshTrafficTextFromSnapshot();
        }

        /// <summary>
        /// 在连接状态变化后刷新线路锁定和全部相关命令。
        /// </summary>
        /// <param name="value">新的连接状态。</param>
        partial void OnIsConnectedChanged(bool value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 在连接操作忙状态变化后刷新线路锁定和连接命令。
        /// </summary>
        /// <param name="value">新的忙状态。</param>
        partial void OnIsConnectionOperationBusyChanged(bool value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 在定时运行状态变化后刷新负载锁定和全部发送命令。
        /// </summary>
        /// <param name="value">新的定时运行状态。</param>
        partial void OnIsPeriodicSendingChanged(bool value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 在退出状态变化后禁用全部可变命令。
        /// </summary>
        /// <param name="value">新的退出状态。</param>
        partial void OnIsShuttingDownChanged(bool value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 在暂停状态变化后刷新按钮动作文本。
        /// </summary>
        /// <param name="value">新的暂停状态。</param>
        partial void OnIsPausedChanged(bool value)
        {
            _ = value;
            OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    /// <summary>
    /// 为数据模式下拉框关联持久化枚举值和面向用户的规范显示名称。
    /// </summary>
    public sealed class SerialAssistantDataModeOption
    {
        /// <summary>
        /// 初始化一个数据模式显示选项。
        /// </summary>
        /// <param name="value">发送或接收实际使用的持久化枚举值。</param>
        /// <param name="displayName">下拉框显示的规范名称。</param>
        public SerialAssistantDataModeOption(
            SerialAssistantDataMode value,
            string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw new ArgumentException("数据模式显示名称不能为空。", nameof(displayName));
            }

            Value = value;
            DisplayName = displayName.Trim();
        }

        /// <summary>
        /// 获取发送或接收实际使用的数据模式。
        /// </summary>
        public SerialAssistantDataMode Value { get; }

        /// <summary>
        /// 获取下拉框显示的规范名称。
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// 返回与数据模板一致的规范显示名称，供 ComboBox 文本和辅助功能使用。
        /// </summary>
        /// <returns>UTF-8 或 HEX 的规范名称。</returns>
        public override string ToString()
        {
            return DisplayName;
        }
    }
}
