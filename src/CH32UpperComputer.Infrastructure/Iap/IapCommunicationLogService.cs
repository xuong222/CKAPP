using CH32UpperComputer.Core.Iap;

namespace CH32UpperComputer.Infrastructure.Iap
{
    /// <summary>
    /// 定义 IAP 日志记录的方向。
    /// </summary>
    public enum IapCommunicationDirection
    {
        /// <summary>
        /// 上位机发送到 Bootloader。
        /// </summary>
        Transmit,

        /// <summary>
        /// Bootloader 返回到上位机。
        /// </summary>
        Receive,

        /// <summary>
        /// 连接、状态或异常诊断。
        /// </summary>
        System,
    }

    /// <summary>
    /// 保存一条不可变 IAP 通信或状态日志。
    /// </summary>
    public sealed class IapCommunicationLogEntry
    {
        /// <summary>
        /// 原始线路字节的私有不可变快照。
        /// </summary>
        private readonly byte[] rawBytes;

        /// <summary>
        /// 初始化一条完整 IAP 日志。
        /// </summary>
        /// <param name="timestamp">记录产生的 UTC 时间。</param>
        /// <param name="connectionId">应用内 TCP 连接编号。</param>
        /// <param name="direction">发送、接收或系统方向。</param>
        /// <param name="command">相关命令；纯系统日志可以为空。</param>
        /// <param name="sequence">相关 sequence；纯系统日志可以为空。</param>
        /// <param name="offset">DATA 偏移。</param>
        /// <param name="length">命令相关长度。</param>
        /// <param name="crc32">分片或镜像 CRC32。</param>
        /// <param name="status">ACK/NACK；发送或系统日志可以为空。</param>
        /// <param name="detail">响应 detail。</param>
        /// <param name="duration">请求到响应的耗时。</param>
        /// <param name="message">中文摘要。</param>
        /// <param name="rawBytes">原始发送或接收字节。</param>
        public IapCommunicationLogEntry(
            DateTimeOffset timestamp,
            long connectionId,
            IapCommunicationDirection direction,
            IapCommand? command,
            uint? sequence,
            uint offset,
            uint length,
            uint crc32,
            IapResponseStatus? status,
            uint? detail,
            TimeSpan? duration,
            string message,
            ReadOnlySpan<byte> rawBytes)
        {
            if (connectionId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(connectionId));
            }

            Timestamp = timestamp;
            ConnectionId = connectionId;
            Direction = direction;
            Command = command;
            Sequence = sequence;
            Offset = offset;
            Length = length;
            Crc32 = crc32;
            Status = status;
            Detail = detail;
            Duration = duration;
            Message = message ?? string.Empty;
            this.rawBytes = rawBytes.ToArray();
        }

        /// <summary>
        /// 获取日志 UTC 时间。
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// 获取应用内连接编号。
        /// </summary>
        public long ConnectionId { get; }

        /// <summary>
        /// 获取日志方向。
        /// </summary>
        public IapCommunicationDirection Direction { get; }

        /// <summary>
        /// 获取相关命令。
        /// </summary>
        public IapCommand? Command { get; }

        /// <summary>
        /// 获取相关 sequence。
        /// </summary>
        public uint? Sequence { get; }

        /// <summary>
        /// 获取 DATA 偏移。
        /// </summary>
        public uint Offset { get; }

        /// <summary>
        /// 获取命令相关长度。
        /// </summary>
        public uint Length { get; }

        /// <summary>
        /// 获取分片或镜像 CRC32。
        /// </summary>
        public uint Crc32 { get; }

        /// <summary>
        /// 获取响应状态。
        /// </summary>
        public IapResponseStatus? Status { get; }

        /// <summary>
        /// 获取响应 detail。
        /// </summary>
        public uint? Detail { get; }

        /// <summary>
        /// 获取事务耗时。
        /// </summary>
        public TimeSpan? Duration { get; }

        /// <summary>
        /// 获取中文摘要。
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 获取原始字节的防御性快照。
        /// </summary>
        public ReadOnlyMemory<byte> RawBytes => rawBytes;
    }

    /// <summary>
    /// 提供最多 5000 条的线程安全 IAP 日志缓存。
    /// </summary>
    public sealed class IapCommunicationLogService
    {
        /// <summary>
        /// 日志数据缓存容量。
        /// </summary>
        public const int MaximumEntryCount = 5000;

        /// <summary>
        /// 保护日志缓存和清空操作。
        /// </summary>
        private readonly object sync = new();

        /// <summary>
        /// 统一日志时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 按产生顺序保存日志。
        /// </summary>
        private readonly Queue<IapCommunicationLogEntry> entries = new();

        /// <summary>
        /// 使用指定时间源创建日志服务。
        /// </summary>
        /// <param name="timeProvider">为每条日志提供 UTC 时间。</param>
        public IapCommunicationLogService(TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 在追加日志后通知界面批处理器。
        /// </summary>
        public event Action<IapCommunicationLogEntry>? EntryRecorded;

        /// <summary>
        /// 获取当前日志时间。
        /// </summary>
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

        /// <summary>
        /// 追加一条日志并按容量移除最旧记录。
        /// </summary>
        /// <param name="entry">待追加的不可变日志。</param>
        public void Record(IapCommunicationLogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            lock (sync)
            {
                entries.Enqueue(entry);

                while (entries.Count > MaximumEntryCount)
                {
                    entries.Dequeue();
                }
            }

            Action<IapCommunicationLogEntry>? observers = EntryRecorded;

            if (observers is null)
            {
                return;
            }

            foreach (Action<IapCommunicationLogEntry> observer in
                observers.GetInvocationList().Cast<Action<IapCommunicationLogEntry>>())
            {
                try
                {
                    observer(entry);
                }
                catch (Exception)
                {
                    // 日志观察者故障不得中断协议状态机或网络读写线程。
                }
            }
        }

        /// <summary>
        /// 获取当前日志的稳定快照。
        /// </summary>
        /// <returns>按时间顺序排列的独立数组。</returns>
        public IReadOnlyList<IapCommunicationLogEntry> Snapshot()
        {
            lock (sync)
            {
                return entries.ToArray();
            }
        }

        /// <summary>
        /// 追加一条不带线路字节的连接或状态诊断。
        /// </summary>
        /// <param name="connectionId">相关连接编号；尚未连接时为零。</param>
        /// <param name="message">需要显示和导出的中文诊断。</param>
        public void RecordSystem(
            long connectionId,
            string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("IAP 系统日志内容不能为空。", nameof(message));
            }

            Record(
                new IapCommunicationLogEntry(
                    UtcNow,
                    connectionId,
                    IapCommunicationDirection.System,
                    null,
                    null,
                    0U,
                    0U,
                    0U,
                    null,
                    null,
                    null,
                    message,
                    ReadOnlySpan<byte>.Empty));
        }

        /// <summary>
        /// 清空所有缓存日志。
        /// </summary>
        public void Clear()
        {
            lock (sync)
            {
                entries.Clear();
            }
        }
    }
}
