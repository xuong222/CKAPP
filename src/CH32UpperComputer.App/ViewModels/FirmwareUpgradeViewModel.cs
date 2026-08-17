using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

using CH32UpperComputer.App.Services;
using CH32UpperComputer.Core.Iap;
using CH32UpperComputer.Infrastructure.Iap;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Infrastructure.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 将固件文件、IAP 状态机、进度和独立日志投影到第四个标签页。
    /// </summary>
    public sealed partial class FirmwareUpgradeViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 实时页面最多保留的 IAP 日志项数。
        /// </summary>
        public const int LiveLogCapacity = 750;

        /// <summary>
        /// 读取并固定本地 .bin 文件。
        /// </summary>
        private readonly FirmwareFileService firmwareFileService;

        /// <summary>
        /// 执行完整 IAP 状态机。
        /// </summary>
        private readonly IapUpgradeCoordinator coordinator;

        /// <summary>
        /// 保存最多 5000 条独立 IAP 日志。
        /// </summary>
        private readonly IapCommunicationLogService logService;

        /// <summary>
        /// 开始升级前停止 Modbus 定时发送。
        /// </summary>
        private readonly PeriodicSendService periodicSendService;

        /// <summary>
        /// 提供活动 Modbus 计数并刷新开始按钮。
        /// </summary>
        private readonly Infrastructure.Coordination.IApplicationOperationGate
            applicationOperationGate;

        /// <summary>
        /// 把后台进度和日志切换到 WPF 线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 为 CSV 文件名提供统一时间。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 将单条后台日志最多五十项一批提交到 UI。
        /// </summary>
        private readonly DispatcherBatcher<IapCommunicationLogEntry> logBatcher;

        /// <summary>
        /// 当前已经校验的固件字节快照。
        /// </summary>
        private FirmwareImage? selectedImage;

        /// <summary>
        /// 指示 ViewModel 已停止接收事件。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 用户选择的固件完整路径。
        /// </summary>
        [ObservableProperty]
        private string firmwarePath = string.Empty;

        /// <summary>
        /// 固件文件名。
        /// </summary>
        [ObservableProperty]
        private string firmwareFileName = "--";

        /// <summary>
        /// 固件大小文本。
        /// </summary>
        [ObservableProperty]
        private string firmwareSizeText = "--";

        /// <summary>
        /// 固件整体 CRC32 文本。
        /// </summary>
        [ObservableProperty]
        private string firmwareCrc32Text = "--";

        /// <summary>
        /// 用户是否确认裸 bin 的链接来源。
        /// </summary>
        [ObservableProperty]
        private bool isLinkAddressConfirmed;

        /// <summary>
        /// 目标 Bootloader IPv4 文本。
        /// </summary>
        [ObservableProperty]
        private string targetAddress;

        /// <summary>
        /// 目标 Bootloader TCP 端口。
        /// </summary>
        [ObservableProperty]
        private int targetPort;

        /// <summary>
        /// 是否在日志中显示完整 DATA 载荷。
        /// </summary>
        [ObservableProperty]
        private bool showFullDataPayload;

        /// <summary>
        /// 当前是否存在活动升级。
        /// </summary>
        [ObservableProperty]
        private bool isUpgradeActive;

        /// <summary>
        /// 当前升级状态文本。
        /// </summary>
        [ObservableProperty]
        private string upgradeStateText = "等待固件";

        /// <summary>
        /// 当前 RUN 状态文本。
        /// </summary>
        [ObservableProperty]
        private string runStateText = "尚未请求";

        /// <summary>
        /// 当前连接和升级次数文本。
        /// </summary>
        [ObservableProperty]
        private string attemptText = "连接 0 · 升级 0/3";

        /// <summary>
        /// 当前 TCP 连接状态文本。
        /// </summary>
        [ObservableProperty]
        private string connectionStatusText = "未连接";

        /// <summary>
        /// Bootloader 版本文本。
        /// </summary>
        [ObservableProperty]
        private string bootVersionText = "--";

        /// <summary>
        /// App 基址文本。
        /// </summary>
        [ObservableProperty]
        private string appBaseText = "--";

        /// <summary>
        /// App 最大长度文本。
        /// </summary>
        [ObservableProperty]
        private string appMaxSizeText = "--";

        /// <summary>
        /// 当前 App 有效状态文本。
        /// </summary>
        [ObservableProperty]
        private string appValidText = "--";

        /// <summary>
        /// 当前 App 大小文本。
        /// </summary>
        [ObservableProperty]
        private string appSizeText = "--";

        /// <summary>
        /// 当前 App CRC32 文本。
        /// </summary>
        [ObservableProperty]
        private string appCrc32Text = "--";

        /// <summary>
        /// 进度条百分比。
        /// </summary>
        [ObservableProperty]
        private double progressPercent;

        /// <summary>
        /// 已确认字节文本。
        /// </summary>
        [ObservableProperty]
        private string confirmedBytesText = "0 / 0 字节";

        /// <summary>
        /// 传输速度文本。
        /// </summary>
        [ObservableProperty]
        private string speedText = "--";

        /// <summary>
        /// 已用时间文本。
        /// </summary>
        [ObservableProperty]
        private string elapsedText = "00:00";

        /// <summary>
        /// 预计剩余时间文本。
        /// </summary>
        [ObservableProperty]
        private string remainingText = "--";

        /// <summary>
        /// 页面当前状态或错误提示。
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "请选择按 0x0800C000 链接生成的 .bin 固件。";

        /// <summary>
        /// 日志导出目录。
        /// </summary>
        [ObservableProperty]
        private string exportDirectory;

        /// <summary>
        /// 初始化固件升级页面。
        /// </summary>
        /// <param name="firmwareFileService">负责读取和校验 .bin 的服务。</param>
        /// <param name="coordinator">负责完整 IAP 状态机的协调器。</param>
        /// <param name="logService">保存独立 IAP 日志的服务。</param>
        /// <param name="periodicSendService">开始升级前停止的 Modbus 定时服务。</param>
        /// <param name="applicationOperationGate">提供 Modbus/IAP 实时互斥状态的应用门。</param>
        /// <param name="dispatcher">将后台事件投递到 UI 线程的调度器。</param>
        /// <param name="timeProvider">导出文件名和状态使用的时间源。</param>
        /// <param name="settings">包含持久化 IP、端口和日志目录的安全设置。</param>
        public FirmwareUpgradeViewModel(
            FirmwareFileService firmwareFileService,
            IapUpgradeCoordinator coordinator,
            IapCommunicationLogService logService,
            PeriodicSendService periodicSendService,
            Infrastructure.Coordination.IApplicationOperationGate applicationOperationGate,
            IUiDispatcher dispatcher,
            TimeProvider timeProvider,
            AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(firmwareFileService);
            ArgumentNullException.ThrowIfNull(coordinator);
            ArgumentNullException.ThrowIfNull(logService);
            ArgumentNullException.ThrowIfNull(periodicSendService);
            ArgumentNullException.ThrowIfNull(applicationOperationGate);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentNullException.ThrowIfNull(settings);
            AppSettings safeSettings = settings.CreateValidatedCopy();
            this.firmwareFileService = firmwareFileService;
            this.coordinator = coordinator;
            this.logService = logService;
            this.periodicSendService = periodicSendService;
            this.applicationOperationGate = applicationOperationGate;
            this.dispatcher = dispatcher;
            this.timeProvider = timeProvider;
            targetAddress = safeSettings.IapTargetAddress;
            targetPort = safeSettings.IapTcpPort;
            exportDirectory = string.IsNullOrWhiteSpace(safeSettings.LogExportDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : safeSettings.LogExportDirectory;
            Entries = new ObservableCollection<IapLogItemViewModel>();
            logBatcher = new DispatcherBatcher<IapCommunicationLogEntry>(
                dispatcher,
                ConsumeLogBatch);
            StartUpgradeCommand = new AsyncRelayCommand(
                StartUpgradeAsync,
                CanStartUpgrade);
            CancelUpgradeCommand = new AsyncRelayCommand(
                CancelUpgradeAsync,
                () => IsUpgradeActive);
            RunApplicationCommand = new AsyncRelayCommand(
                RunApplicationAsync,
                CanRunApplication);
            ClearLogCommand = new RelayCommand(ClearLog);
            ExportLogCommand = new AsyncRelayCommand(ExportLogAsync);
            coordinator.ProgressChanged += HandleProgressChanged;
            logService.EntryRecorded += HandleLogRecorded;
            this.applicationOperationGate.ModbusActivityChanged +=
                HandleModbusActivityChanged;
            ReloadLogProjection();
        }

        /// <summary>
        /// 获取最多 750 条虚拟化 IAP 日志显示项。
        /// </summary>
        public ObservableCollection<IapLogItemViewModel> Entries { get; }

        /// <summary>
        /// 获取是否允许浏览并替换固件。
        /// </summary>
        public bool CanSelectFirmware =>
            !IsUpgradeActive &&
            coordinator.RunState != IapRunState.Starting &&
            !applicationOperationGate.IsIapActive;

        /// <summary>
        /// 获取是否允许编辑 IP、端口和确认状态。
        /// </summary>
        public bool CanEditUpgradeInputs =>
            !IsUpgradeActive &&
            coordinator.RunState != IapRunState.Starting &&
            !applicationOperationGate.IsIapActive;

        /// <summary>
        /// 获取开始升级命令。
        /// </summary>
        public IAsyncRelayCommand StartUpgradeCommand { get; }

        /// <summary>
        /// 获取取消升级命令。
        /// </summary>
        public IAsyncRelayCommand CancelUpgradeCommand { get; }

        /// <summary>
        /// 获取手动运行 App 命令。
        /// </summary>
        public IAsyncRelayCommand RunApplicationCommand { get; }

        /// <summary>
        /// 获取清空 IAP 日志命令。
        /// </summary>
        public IRelayCommand ClearLogCommand { get; }

        /// <summary>
        /// 获取导出 IAP CSV 命令。
        /// </summary>
        public IAsyncRelayCommand ExportLogCommand { get; }

        /// <summary>
        /// 读取用户通过文件对话框选择的固件。
        /// </summary>
        /// <param name="filePath">文件对话框返回的完整路径。</param>
        /// <returns>读取、CRC32 计算和界面更新完成后的任务。</returns>
        public async Task SelectFirmwareAsync(string filePath)
        {
            if (!CanSelectFirmware)
            {
                return;
            }

            selectedImage = null;
            coordinator.ClearFirmware();
            FirmwarePath = string.Empty;
            FirmwareFileName = "--";
            FirmwareSizeText = "--";
            FirmwareCrc32Text = "--";
            IsLinkAddressConfirmed = false;
            NotifyCommandStates();

            try
            {
                FirmwareImage image = await firmwareFileService
                    .LoadAsync(filePath, CancellationToken.None)
                    .ConfigureAwait(true);
                selectedImage = image;
                FirmwarePath = image.FullPath;
                FirmwareFileName = image.FileName;
                FirmwareSizeText = $"{image.Length:N0} 字节";
                FirmwareCrc32Text = $"0x{image.Crc32:X8}";
                IsLinkAddressConfirmed = false;
                coordinator.SetFirmwareReady(image);
                UpgradeStateText = "固件已就绪";
                StatusMessage = "请确认该裸 .bin 按 0x0800C000 链接生成。";
            }
            catch (Exception exception)
            {
                selectedImage = null;
                FirmwarePath = string.Empty;
                FirmwareFileName = "--";
                FirmwareSizeText = "--";
                FirmwareCrc32Text = "--";
                IsLinkAddressConfirmed = false;
                StatusMessage = $"固件读取失败：{exception.Message}";
            }
            finally
            {
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 立即排空尚未提交到 UI 的 IAP 日志。
        /// </summary>
        public void FlushPendingLogs()
        {
            logBatcher.Flush();
        }

        /// <summary>
        /// 解除事件订阅并停止日志批处理器。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            coordinator.ProgressChanged -= HandleProgressChanged;
            logService.EntryRecorded -= HandleLogRecorded;
            applicationOperationGate.ModbusActivityChanged -=
                HandleModbusActivityChanged;
            logBatcher.Dispose();
        }

        /// <summary>
        /// 校验 IP 和端口、停止定时发送并启动完整升级。
        /// </summary>
        /// <returns>升级到达终态后的任务。</returns>
        private async Task StartUpgradeAsync()
        {
            if (selectedImage is null)
            {
                StatusMessage = "请先选择合法固件。";
                return;
            }

            if (!TryCreateEndpoint(out IPAddress? address, out string? error))
            {
                StatusMessage = error!;
                return;
            }

            try
            {
                await periodicSendService
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(true);
                await coordinator
                    .StartUpgradeAsync(
                        selectedImage,
                        address!,
                        TargetPort,
                        CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
            finally
            {
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 请求取消当前升级。
        /// </summary>
        /// <returns>升级状态机收敛后的任务。</returns>
        private async Task CancelUpgradeAsync()
        {
            await coordinator.CancelAsync(CancellationToken.None).ConfigureAwait(true);
            NotifyCommandStates();
        }

        /// <summary>
        /// 在升级验证成功后手动发送一次 RUN。
        /// </summary>
        /// <returns>RUN 进入明确或不确定终态后的任务。</returns>
        private async Task RunApplicationAsync()
        {
            if (!TryCreateEndpoint(out IPAddress? address, out string? error))
            {
                StatusMessage = error!;
                return;
            }

            try
            {
                await coordinator
                    .RunApplicationAsync(
                        address!,
                        TargetPort,
                        CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
            finally
            {
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 清空 IAP 数据缓存和实时投影。
        /// </summary>
        private void ClearLog()
        {
            logService.Clear();
            Entries.Clear();
            StatusMessage = "IAP 通信日志已清空。";
        }

        /// <summary>
        /// 在线程池将当前 5000 条以内的 IAP 日志快照导出为 UTF-8 CSV。
        /// </summary>
        /// <returns>导出完成后的任务。</returns>
        private async Task ExportLogAsync()
        {
            try
            {
                string directory = string.IsNullOrWhiteSpace(ExportDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : Path.GetFullPath(ExportDirectory.Trim());
                Directory.CreateDirectory(directory);
                string filePath = Path.Combine(
                    directory,
                    $"IAP日志_{timeProvider.GetUtcNow().ToLocalTime():yyyyMMdd_HHmmss}.csv");
                IapCommunicationLogEntry[] snapshot = logService.Snapshot().ToArray();
                bool includeFullDataPayload = ShowFullDataPayload;
                await Task.Run(
                    async () =>
                    {
                        StringBuilder csv = new();
                        csv.AppendLine(
                            "时间,连接,方向,命令,Sequence,Offset,Length,CRC32,Status,Detail,耗时ms,摘要,原始字节");

                        foreach (IapCommunicationLogEntry entry in snapshot)
                        {
                            csv.AppendLine(CreateCsvLine(
                                entry,
                                includeFullDataPayload));
                        }

                        await File.WriteAllTextAsync(
                            filePath,
                            csv.ToString(),
                            new UTF8Encoding(true),
                            CancellationToken.None).ConfigureAwait(false);
                    }).ConfigureAwait(true);
                ExportDirectory = directory;
                StatusMessage = $"IAP 日志已导出：{filePath}";
            }
            catch (Exception exception)
            {
                StatusMessage = $"IAP 日志导出失败：{exception.Message}";
            }
        }

        /// <summary>
        /// 接收协调器进度并在 UI 线程更新页面。
        /// </summary>
        /// <param name="progress">后台产生的不可变进度快照。</param>
        private void HandleProgressChanged(IapUpgradeProgress progress)
        {
            dispatcher.Post(
                () =>
                {
                    IsUpgradeActive = progress.State is
                        IapUpgradeState.Connecting or
                        IapUpgradeState.BootloaderConnected or
                        IapUpgradeState.Erasing or
                        IapUpgradeState.Transferring or
                        IapUpgradeState.Verifying;
                    UpgradeStateText = TranslateUpgradeState(progress.State);
                    RunStateText = TranslateRunState(progress.RunState);
                    AttemptText =
                        $"连接 {progress.ConnectionAttempt} · 升级 {progress.UpgradeAttempt}/{IapUpgradeCoordinator.MaximumUpgradeAttempts}";
                    ConnectionStatusText = progress.IsConnected
                        ? $"已连接 #{progress.ConnectionId}"
                        : progress.State == IapUpgradeState.Connecting
                            ? "等待连接"
                            : "未连接";
                    ProgressPercent = progress.TotalBytes == 0
                        ? 0d
                        : progress.ConfirmedBytes * 100d / progress.TotalBytes;
                    ConfirmedBytesText =
                        $"{progress.ConfirmedBytes:N0} / {progress.TotalBytes:N0} 字节";
                    SpeedText = progress.BytesPerSecond > 0d
                        ? $"{progress.BytesPerSecond / 1024d:0.0} KiB/s"
                        : "--";
                    ElapsedText = FormatDuration(progress.Elapsed);
                    RemainingText = progress.EstimatedRemaining.HasValue
                        ? FormatDuration(progress.EstimatedRemaining.Value)
                        : "--";
                    StatusMessage = progress.Message;
                    ApplyHelloInfo(progress.HelloInfo);
                    OnPropertyChanged(nameof(CanSelectFirmware));
                    OnPropertyChanged(nameof(CanEditUpgradeInputs));
                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 将一条后台日志交给五十项批处理器。
        /// </summary>
        /// <param name="entry">新追加的不可变日志。</param>
        private void HandleLogRecorded(IapCommunicationLogEntry entry)
        {
            logBatcher.Enqueue(entry);
        }

        /// <summary>
        /// 在 UI 线程追加一批日志并维持 750 项容量。
        /// </summary>
        /// <param name="batch">按产生顺序排列的日志批次。</param>
        private void ConsumeLogBatch(IReadOnlyList<IapCommunicationLogEntry> batch)
        {
            foreach (IapCommunicationLogEntry entry in batch)
            {
                Entries.Add(new IapLogItemViewModel(entry, ShowFullDataPayload));

                while (Entries.Count > LiveLogCapacity)
                {
                    Entries.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// 从完整缓存重新构建最多 750 项实时投影。
        /// </summary>
        private void ReloadLogProjection()
        {
            Entries.Clear();

            foreach (IapCommunicationLogEntry entry in
                logService.Snapshot().TakeLast(LiveLogCapacity))
            {
                Entries.Add(new IapLogItemViewModel(entry, ShowFullDataPayload));
            }
        }

        /// <summary>
        /// 把 HELLO 信息格式化到六项设备字段。
        /// </summary>
        /// <param name="hello">最近一次 HELLO 信息；为空时保留当前值。</param>
        private void ApplyHelloInfo(IapHelloInfo? hello)
        {
            if (hello is null)
            {
                return;
            }

            BootVersionText = $"0x{hello.BootVersion:X8}";
            AppBaseText = $"0x{hello.AppBase:X8}";
            AppMaxSizeText = $"{hello.AppMaxSize:N0} 字节";
            AppValidText = hello.AppValid ? "有效" : "无效";
            AppSizeText = $"{hello.AppSize:N0} 字节";
            AppCrc32Text = $"0x{hello.AppCrc32:X8}";
        }

        /// <summary>
        /// 校验用户输入并创建 IPv4 目标。
        /// </summary>
        /// <param name="address">成功时接收解析后的 IPv4 地址。</param>
        /// <param name="error">失败时接收中文错误。</param>
        /// <returns>IP 和端口均合法时返回真。</returns>
        private bool TryCreateEndpoint(
            out IPAddress? address,
            out string? error)
        {
            if (!IPAddress.TryParse(TargetAddress?.Trim(), out address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.Broadcast))
            {
                error = "目标地址必须是明确的 IPv4 地址。";
                return false;
            }

            if (TargetPort is < 1 or > 65535)
            {
                error = "TCP 端口必须位于 1 至 65535。";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// 获取开始按钮是否满足固件、确认、端点和状态条件。
        /// </summary>
        /// <returns>允许开始时返回真。</returns>
        private bool CanStartUpgrade()
        {
            return selectedImage is not null &&
                IsLinkAddressConfirmed &&
                !IsUpgradeActive &&
                !applicationOperationGate.IsIapActive &&
                applicationOperationGate.ActiveModbusOperationCount == 0 &&
                TryCreateEndpoint(out _, out _);
        }

        /// <summary>
        /// 获取当前是否允许发送 RUN。
        /// </summary>
        /// <returns>固件已经验证成功且 RUN 未在进行时返回真。</returns>
        private bool CanRunApplication()
        {
            return coordinator.State == IapUpgradeState.UpgradeSucceeded &&
                coordinator.RunState != IapRunState.Starting &&
                !applicationOperationGate.IsIapActive &&
                applicationOperationGate.ActiveModbusOperationCount == 0;
        }

        /// <summary>
        /// 通知四个状态相关命令重新计算可执行性。
        /// </summary>
        private void NotifyCommandStates()
        {
            StartUpgradeCommand.NotifyCanExecuteChanged();
            CancelUpgradeCommand.NotifyCanExecuteChanged();
            RunApplicationCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 在活动 Modbus 数量变化后实时刷新开始升级按钮。
        /// </summary>
        /// <param name="activeCount">新的活动 Modbus 操作数量。</param>
        private void HandleModbusActivityChanged(int activeCount)
        {
            dispatcher.Post(
                () =>
                {
                    if (activeCount > 0 && selectedImage is not null)
                    {
                        StatusMessage = "当前有 Modbus 请求等待响应，完成后才能开始固件升级。";
                    }

                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 创建单条 CSV 文本并转义逗号、引号和换行。
        /// </summary>
        /// <param name="entry">需要导出的 IAP 日志。</param>
        /// <param name="includeFullDataPayload">是否在 CSV 中包含完整 DATA 载荷。</param>
        /// <returns>不含尾换行的 CSV 行。</returns>
        private static string CreateCsvLine(
            IapCommunicationLogEntry entry,
            bool includeFullDataPayload)
        {
            ReadOnlySpan<byte> rawBytes = entry.RawBytes.Span;

            if (!includeFullDataPayload &&
                entry.Command == IapCommand.Data &&
                entry.Direction == IapCommunicationDirection.Transmit &&
                rawBytes.Length > IapFrameCodec.RequestHeaderSize)
            {
                rawBytes = rawBytes[..IapFrameCodec.RequestHeaderSize];
            }

            string[] fields =
            {
                entry.Timestamp.ToLocalTime().ToString("O", CultureInfo.InvariantCulture),
                entry.ConnectionId.ToString(CultureInfo.InvariantCulture),
                entry.Direction.ToString(),
                entry.Command?.ToString().ToUpperInvariant() ?? string.Empty,
                entry.Sequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                entry.Offset.ToString(CultureInfo.InvariantCulture),
                entry.Length.ToString(CultureInfo.InvariantCulture),
                $"0x{entry.Crc32:X8}",
                entry.Status?.ToString() ?? string.Empty,
                entry.Detail?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                entry.Duration?.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                entry.Message,
                Core.Protocol.HexFrameParser.Format(rawBytes),
            };
            return string.Join(",", fields.Select(EscapeCsv));
        }

        /// <summary>
        /// 转义一个 CSV 字段。
        /// </summary>
        /// <param name="value">原始字段文本。</param>
        /// <returns>符合 RFC 4180 常见规则的字段。</returns>
        private static string EscapeCsv(string value)
        {
            return $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        /// <summary>
        /// 把升级枚举转换为稳定中文。
        /// </summary>
        /// <param name="state">升级状态。</param>
        /// <returns>用户可读状态文本。</returns>
        private static string TranslateUpgradeState(IapUpgradeState state)
        {
            return state switch
            {
                IapUpgradeState.Idle => "等待固件",
                IapUpgradeState.FirmwareReady => "固件已就绪",
                IapUpgradeState.Connecting => "正在连接",
                IapUpgradeState.BootloaderConnected => "设备已识别",
                IapUpgradeState.Erasing => "正在擦除",
                IapUpgradeState.Transferring => "正在传输",
                IapUpgradeState.Verifying => "正在验证",
                IapUpgradeState.UpgradeSucceeded => "升级成功",
                IapUpgradeState.Cancelled => "已取消",
                IapUpgradeState.Failed => "升级失败",
                IapUpgradeState.Uncertain => "结果不确定",
                _ => state.ToString(),
            };
        }

        /// <summary>
        /// 把 RUN 枚举转换为稳定中文。
        /// </summary>
        /// <param name="state">RUN 状态。</param>
        /// <returns>用户可读状态文本。</returns>
        private static string TranslateRunState(IapRunState state)
        {
            return state switch
            {
                IapRunState.NotRequested => "尚未请求",
                IapRunState.Starting => "正在启动",
                IapRunState.Confirmed => "已确认启动",
                IapRunState.Uncertain => "启动结果不确定",
                IapRunState.Rejected => "启动被拒绝",
                _ => state.ToString(),
            };
        }

        /// <summary>
        /// 把时间跨度格式化为小时、分钟和秒。
        /// </summary>
        /// <param name="duration">非负时间跨度。</param>
        /// <returns>短时使用 mm:ss，超过一小时使用 hh:mm:ss。</returns>
        private static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1d
                ? duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 确认框变化后刷新开始按钮。
        /// </summary>
        /// <param name="value">新的确认状态。</param>
        partial void OnIsLinkAddressConfirmedChanged(bool value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// IP 文本变化后刷新开始按钮。
        /// </summary>
        /// <param name="value">新的目标地址文本。</param>
        partial void OnTargetAddressChanged(string value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// 端口变化后刷新开始按钮。
        /// </summary>
        /// <param name="value">新的 TCP 端口。</param>
        partial void OnTargetPortChanged(int value)
        {
            _ = value;
            NotifyCommandStates();
        }

        /// <summary>
        /// DATA 显示策略变化后重建当前日志投影。
        /// </summary>
        /// <param name="value">是否显示完整 DATA 载荷。</param>
        partial void OnShowFullDataPayloadChanged(bool value)
        {
            _ = value;
            ReloadLogProjection();
        }
    }
}
