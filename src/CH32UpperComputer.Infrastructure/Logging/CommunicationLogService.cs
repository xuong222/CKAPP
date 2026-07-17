using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CH32UpperComputer.Infrastructure.Transactions;

namespace CH32UpperComputer.Infrastructure.Logging
{
    /// <summary>
    /// 提供通信日志的双容量缓存、批量界面发布、非破坏筛选和线程池 UTF-8 导出。
    /// </summary>
    public sealed class CommunicationLogService : IDisposable
    {
        /// <summary>
        /// 生产环境允许保存的完整通信日志数量。
        /// </summary>
        public const int DataCacheCapacity = 5000;

        /// <summary>
        /// 生产环境允许投影到实时消息流的轻量记录数量。
        /// </summary>
        public const int LiveViewCapacity = 750;

        /// <summary>
        /// 单次切换到界面线程的最大记录数量。
        /// </summary>
        public const int MaximumPublishedBatchSize = 50;

        /// <summary>
        /// 尚未达到批量数量时允许等待的最大发布时长。
        /// </summary>
        public static readonly TimeSpan MaximumPublishDelay = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// 导出文件明确使用带 BOM 的 UTF-8 编码。
        /// </summary>
        private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

        /// <summary>
        /// 保护双缓存、待发布批次、计时器状态和释放状态的同步门。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 保存最多 5000 条完整记录的数据缓存。
        /// </summary>
        private readonly Queue<CommunicationLogEntry> dataCache = new(DataCacheCapacity);

        /// <summary>
        /// 保存最多 750 条实时消息流记录的独立投影缓存。
        /// </summary>
        private readonly Queue<CommunicationLogEntry> liveView = new(LiveViewCapacity);

        /// <summary>
        /// 等待按数量或时间批量发布到界面的记录。
        /// </summary>
        private readonly List<CommunicationLogEntry> pendingPublication = new(MaximumPublishedBatchSize);

        /// <summary>
        /// 为日志时间戳和批量计时提供统一、可测试的时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 从首条待发布记录起计算 50 毫秒的单次计时器。
        /// </summary>
        private readonly ITimer publicationTimer;

        /// <summary>
        /// 下一个通信日志序号；只在同步门内递增。
        /// </summary>
        private long nextSequenceId;

        /// <summary>
        /// 指示批量发布计时器当前是否已经安排。
        /// </summary>
        private bool isPublicationTimerArmed;

        /// <summary>
        /// 指示服务是否已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 使用系统时间创建生产通信日志服务。
        /// </summary>
        public CommunicationLogService()
            : this(TimeProvider.System)
        {
        }

        /// <summary>
        /// 使用调用方提供的统一时间源创建通信日志服务。
        /// </summary>
        /// <param name="timeProvider">记录时间戳和驱动 50 毫秒批量计时器的时间源。</param>
        public CommunicationLogService(TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.timeProvider = timeProvider;
            publicationTimer = timeProvider.CreateTimer(
                static state => ((CommunicationLogService)state!).FlushPendingPublication(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// 当一批一至五十条轻量记录准备提交到界面层时发布。
        /// 观察者异常会被隔离，不得中断串口通信或后续日志记录。
        /// </summary>
        public event Action<IReadOnlyList<CommunicationLogEntry>>? EntriesPublished;

        /// <summary>
        /// 当缓存被用户显式清空时发布，供界面同步清空虚拟化集合。
        /// </summary>
        public event Action? CacheCleared;

        /// <summary>
        /// 创建并追加一条完整通信日志，同时维护 5000/750 双容量和 50 条/50 毫秒批量发布。
        /// </summary>
        /// <param name="transactionId">关联事务编号；系统记录可为空。</param>
        /// <param name="direction">发送、接收、错误、迟到或系统类别。</param>
        /// <param name="state">关联事务终态；尚未完成的发送记录和系统记录可为空。</param>
        /// <param name="portGeneration">产生该记录的串口会话代次；零表示尚未打开首个会话。</param>
        /// <param name="receiveSequence">接收块序号；非接收记录使用零。</param>
        /// <param name="rawData">需要按完整帧保存的线路字节，禁止按单字节调用本方法。</param>
        /// <param name="summary">面向界面和导出的非空中文摘要。</param>
        /// <param name="timestamp">可选的实际发生时间；为空时使用统一时间源的当前 UTC 时间。</param>
        /// <returns>已经进入双缓存的不可变日志记录。</returns>
        /// <exception cref="ObjectDisposedException">服务已释放时抛出。</exception>
        public CommunicationLogEntry Append(
            long? transactionId,
            CommunicationDirection direction,
            TransactionCompletionState? state,
            int portGeneration,
            long receiveSequence,
            ReadOnlySpan<byte> rawData,
            string summary,
            DateTimeOffset? timestamp = null)
        {
            CommunicationLogEntry entry;
            IReadOnlyList<CommunicationLogEntry>? immediateBatch = null;

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                long sequenceId = checked(nextSequenceId + 1);
                entry = new CommunicationLogEntry(
                    sequenceId,
                    transactionId,
                    direction,
                    timestamp ?? timeProvider.GetUtcNow(),
                    state,
                    portGeneration,
                    receiveSequence,
                    rawData,
                    summary);
                nextSequenceId = sequenceId;
                EnqueueBounded(dataCache, entry, DataCacheCapacity);
                EnqueueBounded(liveView, entry, LiveViewCapacity);
                pendingPublication.Add(entry);

                if (pendingPublication.Count >= MaximumPublishedBatchSize)
                {
                    publicationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    isPublicationTimerArmed = false;
                    immediateBatch = TakePendingPublicationUnderLock();
                }
                else if (!isPublicationTimerArmed)
                {
                    publicationTimer.Change(MaximumPublishDelay, Timeout.InfiniteTimeSpan);
                    isPublicationTimerArmed = true;
                }
            }

            PublishBatch(immediateBatch);
            return entry;
        }

        /// <summary>
        /// 获取最多 5000 条完整通信记录的独立只读快照。
        /// </summary>
        /// <returns>按日志序号升序排列、不会随后续追加变化的只读集合。</returns>
        public IReadOnlyList<CommunicationLogEntry> CreateDataSnapshot()
        {
            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                return new ReadOnlyCollection<CommunicationLogEntry>(dataCache.ToArray());
            }
        }

        /// <summary>
        /// 获取最多 750 条实时消息流记录的独立只读快照。
        /// </summary>
        /// <returns>按日志序号升序排列、不会随数据缓存变化的只读集合。</returns>
        public IReadOnlyList<CommunicationLogEntry> CreateLiveViewSnapshot()
        {
            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                return new ReadOnlyCollection<CommunicationLogEntry>(liveView.ToArray());
            }
        }

        /// <summary>
        /// 在完整数据缓存快照上执行非破坏筛选，不修改原缓存或实时消息流容量。
        /// </summary>
        /// <param name="directions">允许的方向集合；为空表示不过滤方向。</param>
        /// <param name="keyword">匹配摘要、十六进制、方向或终态的关键字；空白表示不过滤关键字。</param>
        /// <param name="transactionId">需要精确匹配的事务编号；为空表示不过滤事务。</param>
        /// <returns>按原始日志顺序排列的筛选结果快照。</returns>
        public IReadOnlyList<CommunicationLogEntry> CreateFilteredSnapshot(
            IReadOnlySet<CommunicationDirection>? directions,
            string? keyword,
            long? transactionId)
        {
            if (transactionId is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transactionId),
                    transactionId,
                    "筛选事务编号存在时必须为正数。");
            }

            CommunicationLogEntry[] snapshot;

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                snapshot = dataCache.ToArray();
            }

            string normalizedKeyword = keyword?.Trim() ?? string.Empty;
            IEnumerable<CommunicationLogEntry> filtered = snapshot;
            HashSet<CommunicationDirection>? directionSnapshot = directions is null
                ? null
                : new HashSet<CommunicationDirection>(directions);

            if (directionSnapshot is not null)
            {
                filtered = filtered.Where(entry => directionSnapshot.Contains(entry.Direction));
            }

            if (transactionId.HasValue)
            {
                filtered = filtered.Where(entry => entry.TransactionId == transactionId.Value);
            }

            if (normalizedKeyword.Length > 0)
            {
                filtered = filtered.Where(entry => MatchesKeyword(entry, normalizedKeyword));
            }

            return new ReadOnlyCollection<CommunicationLogEntry>(filtered.ToArray());
        }

        /// <summary>
        /// 立即发布当前不足五十条的待处理批次，供应用退出或界面初始化前显式收敛。
        /// </summary>
        public void FlushPendingPublication()
        {
            IReadOnlyList<CommunicationLogEntry>? batch;

            lock (syncRoot)
            {
                if (isDisposed)
                {
                    return;
                }

                publicationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                isPublicationTimerArmed = false;
                batch = TakePendingPublicationUnderLock();
            }

            PublishBatch(batch);
        }

        /// <summary>
        /// 清空双缓存和尚未发布批次，但不影响任何活动事务、串口读取循环或日志序号单调性。
        /// </summary>
        public void Clear()
        {
            Action? observers;

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                dataCache.Clear();
                liveView.Clear();
                pendingPublication.Clear();
                publicationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                isPublicationTimerArmed = false;
                observers = CacheCleared;
            }

            InvokeObservers(observers);
        }

        /// <summary>
        /// 对调用瞬间的完整数据缓存快照执行线程池 CSV 导出，导出期间允许通信继续追加。
        /// </summary>
        /// <param name="filePath">目标 CSV 文件绝对或相对路径；父目录不存在时自动创建。</param>
        /// <param name="cancellationToken">取消尚未完成的目录创建和文件写入。</param>
        /// <returns>带 UTF-8 BOM 的导出文件已经完整关闭后完成的任务。</returns>
        public Task ExportCsvAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("日志导出路径不能为空。", nameof(filePath));
            }

            CommunicationLogEntry[] snapshot;

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                snapshot = dataCache.ToArray();
            }

            string absolutePath = Path.GetFullPath(filePath.Trim());

            return Task.Run(
                async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? directory = Path.GetDirectoryName(absolutePath);

                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    await using FileStream stream = new(
                        absolutePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using StreamWriter writer = new(stream, Utf8WithBom);
                    await writer.WriteLineAsync(
                        "Sequence,TimestampUtc,TransactionId,Direction,State,PortGeneration,ReceiveSequence,RawHex,Summary"
                            .AsMemory(),
                        cancellationToken).ConfigureAwait(false);

                    foreach (CommunicationLogEntry entry in snapshot)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string[] fields =
                        [
                            entry.SequenceId.ToString(CultureInfo.InvariantCulture),
                            EscapeCsv(entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                            entry.TransactionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            entry.Direction.ToString(),
                            entry.State?.ToString() ?? string.Empty,
                            entry.PortGeneration.ToString(CultureInfo.InvariantCulture),
                            entry.ReceiveSequence.ToString(CultureInfo.InvariantCulture),
                            EscapeCsv(entry.RawHex),
                            EscapeCsv(entry.Summary),
                        ];
                        string line = string.Join(",", fields);
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                },
                cancellationToken);
        }

        /// <summary>
        /// 幂等停止批量计时器并释放其资源；已取得的日志快照仍可由调用方继续读取。
        /// </summary>
        public void Dispose()
        {
            lock (syncRoot)
            {
                if (isDisposed)
                {
                    return;
                }

                isDisposed = true;
                pendingPublication.Clear();
                publicationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                isPublicationTimerArmed = false;
            }

            publicationTimer.Dispose();
        }

        /// <summary>
        /// 向指定队列追加一项，并从队首裁剪超过容量的最旧记录。
        /// </summary>
        /// <param name="queue">需要维护的时间升序队列。</param>
        /// <param name="entry">需要追加的不可变日志记录。</param>
        /// <param name="capacity">队列允许保留的最大记录数。</param>
        private static void EnqueueBounded(
            Queue<CommunicationLogEntry> queue,
            CommunicationLogEntry entry,
            int capacity)
        {
            queue.Enqueue(entry);

            while (queue.Count > capacity)
            {
                queue.Dequeue();
            }
        }

        /// <summary>
        /// 判断一条日志的常用显示字段是否包含指定关键字。
        /// </summary>
        /// <param name="entry">需要检查的不可变日志记录。</param>
        /// <param name="keyword">已经去除首尾空白的非空关键字。</param>
        /// <returns>任一字段使用不区分大小写的包含匹配成功时返回 <see langword="true"/>。</returns>
        private static bool MatchesKeyword(
            CommunicationLogEntry entry,
            string keyword)
        {
            return entry.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                entry.RawHex.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                entry.Direction.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (entry.State?.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        /// <summary>
        /// 对可能包含逗号、引号或换行的字段执行 RFC 4180 兼容转义。
        /// </summary>
        /// <param name="value">需要写入单个 CSV 字段的文本。</param>
        /// <returns>可直接拼接到 CSV 行中的安全字段。</returns>
        private static string EscapeCsv(string value)
        {
            if (value.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) < 0)
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        /// <summary>
        /// 在同步门内取出当前待发布记录，并恢复为空批次。
        /// </summary>
        /// <returns>一至五十条只读批次；没有待发布记录时为空。</returns>
        private IReadOnlyList<CommunicationLogEntry>? TakePendingPublicationUnderLock()
        {
            if (pendingPublication.Count == 0)
            {
                return null;
            }

            CommunicationLogEntry[] batch = pendingPublication.ToArray();
            pendingPublication.Clear();
            return new ReadOnlyCollection<CommunicationLogEntry>(batch);
        }

        /// <summary>
        /// 在同步门外逐个发布批次，并隔离单个观察者异常。
        /// </summary>
        /// <param name="batch">需要发布的只读批次；为空时不执行任何操作。</param>
        private void PublishBatch(IReadOnlyList<CommunicationLogEntry>? batch)
        {
            if (batch is null || batch.Count == 0)
            {
                return;
            }

            Action<IReadOnlyList<CommunicationLogEntry>>? observers = EntriesPublished;

            if (observers is null)
            {
                return;
            }

            foreach (Action<IReadOnlyList<CommunicationLogEntry>> observer in
                observers.GetInvocationList().Cast<Action<IReadOnlyList<CommunicationLogEntry>>>())
            {
                try
                {
                    observer(batch);
                }
                catch (Exception)
                {
                    // 界面观察者故障必须与串口通信和后续日志发布隔离。
                }
            }
        }

        /// <summary>
        /// 在同步门外逐个发布无参数通知，并隔离单个观察者异常。
        /// </summary>
        /// <param name="observers">需要调用的可空多播委托。</param>
        private static void InvokeObservers(Action? observers)
        {
            if (observers is null)
            {
                return;
            }

            foreach (Action observer in observers.GetInvocationList().Cast<Action>())
            {
                try
                {
                    observer();
                }
                catch (Exception)
                {
                    // 清空通知观察者故障不得影响服务内部缓存状态。
                }
            }
        }

        /// <summary>
        /// 在已持有同步门时拒绝释放后的进一步使用。
        /// </summary>
        /// <exception cref="ObjectDisposedException">服务已经永久释放时抛出。</exception>
        private void ThrowIfDisposedUnderLock()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
        }
    }
}
