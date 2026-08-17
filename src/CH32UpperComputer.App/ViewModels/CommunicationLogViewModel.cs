using CH32UpperComputer.App.Services;
using CH32UpperComputer.Infrastructure.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 将有界通信日志映射为可筛选、可清空、可导出且最多七百五十项的虚拟化实时列表。
    /// </summary>
    public sealed partial class CommunicationLogViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 保存 5000 条数据缓存和 750 条实时投影的日志服务。
        /// </summary>
        private readonly CommunicationLogService logService;

        /// <summary>
        /// 为导出文件名提供可测试的统一日历时间。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 将日志清空事件安全投递到界面线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 将后台日志批次以最多五十项提交到界面线程。
        /// </summary>
        private readonly DispatcherBatcher<CommunicationLogEntry> dispatcherBatcher;

        /// <summary>
        /// 指示 ViewModel 已经释放并停止接收日志事件。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 是否显示发送记录。
        /// </summary>
        [ObservableProperty]
        private bool showTransmit = true;

        /// <summary>
        /// 是否显示接收记录。
        /// </summary>
        [ObservableProperty]
        private bool showReceive = true;

        /// <summary>
        /// 是否显示错误记录。
        /// </summary>
        [ObservableProperty]
        private bool showError = true;

        /// <summary>
        /// 是否显示迟到或无归属记录。
        /// </summary>
        [ObservableProperty]
        private bool showLate = true;

        /// <summary>
        /// 是否显示系统记录。
        /// </summary>
        [ObservableProperty]
        private bool showSystem = true;

        /// <summary>
        /// 当前摘要或原始十六进制关键字筛选。
        /// </summary>
        [ObservableProperty]
        private string keyword = string.Empty;

        /// <summary>
        /// 当前可选事务编号筛选文本。
        /// </summary>
        [ObservableProperty]
        private string transactionIdText = string.Empty;

        /// <summary>
        /// 当前日志导出目录。
        /// </summary>
        [ObservableProperty]
        private string exportDirectory;

        /// <summary>
        /// 最近一次筛选、清空或导出结果说明。
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "通信日志缓存上限 5000 条，实时消息流上限 750 条。";

        /// <summary>
        /// 初始化日志页面并订阅批量发布事件。
        /// </summary>
        /// <param name="logService">有界通信日志服务。</param>
        /// <param name="dispatcher">负责把后台批次投递到界面线程的调度器。</param>
        /// <param name="timeProvider">为导出文件名提供统一日历时间的时间源。</param>
        /// <param name="initialExportDirectory">设置文件中最近使用的导出目录。</param>
        public CommunicationLogViewModel(
            CommunicationLogService logService,
            IUiDispatcher dispatcher,
            TimeProvider timeProvider,
            string initialExportDirectory)
        {
            ArgumentNullException.ThrowIfNull(logService);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.logService = logService;
            this.dispatcher = dispatcher;
            this.timeProvider = timeProvider;
            exportDirectory = string.IsNullOrWhiteSpace(initialExportDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : initialExportDirectory;
            Entries = new ObservableCollection<CommunicationLogEntry>();
            dispatcherBatcher = new DispatcherBatcher<CommunicationLogEntry>(dispatcher, ConsumePublishedBatch);
            ApplyFilterCommand = new RelayCommand(RefreshFilteredEntries);
            ClearCommand = new RelayCommand(Clear);
            ExportCommand = new AsyncRelayCommand(ExportAsync);
            logService.EntriesPublished += HandleEntriesPublished;
            logService.CacheCleared += HandleCacheCleared;
            RefreshFilteredEntries();
        }

        /// <summary>
        /// 获取最多七百五十条、供 Recycling ListView 绑定的轻量日志记录。
        /// </summary>
        public ObservableCollection<CommunicationLogEntry> Entries { get; }

        /// <summary>
        /// 获取应用当前筛选条件的命令。
        /// </summary>
        public IRelayCommand ApplyFilterCommand { get; }

        /// <summary>
        /// 获取清空日志缓存和当前实时投影的命令。
        /// </summary>
        public IRelayCommand ClearCommand { get; }

        /// <summary>
        /// 获取在线程池导出不可变日志快照的命令。
        /// </summary>
        public IAsyncRelayCommand ExportCommand { get; }

        /// <summary>
        /// 取消日志事件订阅并丢弃尚未提交的界面批次。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            logService.EntriesPublished -= HandleEntriesPublished;
            logService.CacheCleared -= HandleCacheCleared;
            dispatcherBatcher.Dispose();
        }

        /// <summary>
        /// 根据方向、关键字和可选事务编号重新生成非破坏性实时投影。
        /// </summary>
        public void RefreshFilteredEntries()
        {
            if (!TryParseTransactionId(out long? transactionId))
            {
                return;
            }

            IReadOnlyList<CommunicationLogEntry> filtered = logService.CreateFilteredSnapshot(
                CreateSelectedDirections(),
                string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim(),
                transactionId);
            CommunicationLogEntry[] live = filtered
                .TakeLast(CommunicationLogService.LiveViewCapacity)
                .ToArray();
            Entries.Clear();

            foreach (CommunicationLogEntry entry in live)
            {
                Entries.Add(entry);
            }

            StatusMessage = $"当前显示 {Entries.Count} 条；筛选不会删除 5000 条数据缓存。";
        }

        /// <summary>
        /// 立即排空日志服务和界面批处理器的待发布项目，不依赖五十毫秒定时器。
        /// </summary>
        public void FlushPendingEntries()
        {
            logService.FlushPendingPublication();
            dispatcherBatcher.Flush();
        }

        /// <summary>
        /// 清空日志服务双缓存和当前可视投影，不影响正在进行的事务。
        /// </summary>
        private void Clear()
        {
            logService.Clear();
            Entries.Clear();
            StatusMessage = "通信日志已清空；活动事务未被取消。";
        }

        /// <summary>
        /// 在线程池导出调用时刻的不可变 CSV 快照，导出期间允许通信继续追加。
        /// </summary>
        /// <returns>导出完成后的任务。</returns>
        private async Task ExportAsync()
        {
            try
            {
                string directory = string.IsNullOrWhiteSpace(ExportDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : Path.GetFullPath(ExportDirectory.Trim());
                Directory.CreateDirectory(directory);
                string filePath = Path.Combine(
                    directory,
                    $"通信日志_{timeProvider.GetUtcNow().ToLocalTime():yyyyMMdd_HHmmss}.csv");
                await logService.ExportCsvAsync(filePath, CancellationToken.None).ConfigureAwait(true);
                ExportDirectory = directory;
                StatusMessage = $"日志已导出：{filePath}";
            }
            catch (Exception exception)
            {
                StatusMessage = $"日志导出失败：{exception.Message}；请检查目录权限或更换导出目录。";
            }
        }

        /// <summary>
        /// 把日志服务批量事件继续交给界面批处理器，禁止逐字节更新 UI。
        /// </summary>
        /// <param name="batch">按日志序号升序排列的只读批次。</param>
        private void HandleEntriesPublished(IReadOnlyList<CommunicationLogEntry> batch)
        {
            dispatcherBatcher.EnqueueRange(batch);
        }

        /// <summary>
        /// 在界面线程消费最多五十项日志批次并维持七百五十项容量。
        /// </summary>
        /// <param name="batch">需要追加到实时列表的轻量记录。</param>
        private void ConsumePublishedBatch(IReadOnlyList<CommunicationLogEntry> batch)
        {
            if (HasActiveFilter())
            {
                RefreshFilteredEntries();
                return;
            }

            foreach (CommunicationLogEntry entry in batch)
            {
                Entries.Add(entry);

                while (Entries.Count > CommunicationLogService.LiveViewCapacity)
                {
                    Entries.RemoveAt(0);
                }
            }

            StatusMessage = $"实时消息 {Entries.Count}/{CommunicationLogService.LiveViewCapacity}";
        }

        /// <summary>
        /// 在日志服务清空后清理当前实时投影。
        /// </summary>
        private void HandleCacheCleared()
        {
            dispatcher.Post(Entries.Clear);
        }

        /// <summary>
        /// 根据五个方向开关创建日志服务使用的筛选集合。
        /// </summary>
        /// <returns>只包含用户当前启用方向的新集合。</returns>
        private IReadOnlySet<CommunicationDirection> CreateSelectedDirections()
        {
            HashSet<CommunicationDirection> directions = new();

            if (ShowTransmit)
            {
                directions.Add(CommunicationDirection.Transmit);
            }

            if (ShowReceive)
            {
                directions.Add(CommunicationDirection.Receive);
            }

            if (ShowError)
            {
                directions.Add(CommunicationDirection.Error);
            }

            if (ShowLate)
            {
                directions.Add(CommunicationDirection.LateOrUnsolicited);
            }

            if (ShowSystem)
            {
                directions.Add(CommunicationDirection.System);
            }

            return directions;
        }

        /// <summary>
        /// 尝试解析可选正事务编号；空文本表示不过滤，非法文本显示明确错误。
        /// </summary>
        /// <param name="transactionId">成功时接收空值或正事务编号；失败时为空。</param>
        /// <returns>输入为空或为正整数时返回真；格式或范围无效时返回假。</returns>
        private bool TryParseTransactionId(out long? transactionId)
        {
            if (string.IsNullOrWhiteSpace(TransactionIdText))
            {
                transactionId = null;
                return true;
            }

            if (long.TryParse(
                TransactionIdText.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long value) &&
                value > 0)
            {
                transactionId = value;
                return true;
            }

            transactionId = null;
            StatusMessage = "事务编号筛选必须是正整数。";
            return false;
        }

        /// <summary>
        /// 获取当前是否存在任一会改变实时增量显示的筛选条件。
        /// </summary>
        /// <returns>方向未全选、关键字或事务编号非空时返回真。</returns>
        private bool HasActiveFilter()
        {
            return !ShowTransmit ||
                !ShowReceive ||
                !ShowError ||
                !ShowLate ||
                !ShowSystem ||
                !string.IsNullOrWhiteSpace(Keyword) ||
                !string.IsNullOrWhiteSpace(TransactionIdText);
        }
    }
}
