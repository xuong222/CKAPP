using CH32UpperComputer.Infrastructure.Transactions;

namespace CH32UpperComputer.Infrastructure.Logging
{
    /// <summary>
    /// 保存一条具有稳定序号、事务身份、完整原始字节和中文摘要的不可变通信日志。
    /// </summary>
    public sealed class CommunicationLogEntry
    {
        /// <summary>
        /// 当前日志独占持有的原始线路字节。
        /// </summary>
        private readonly byte[] rawData;

        /// <summary>
        /// 初始化一条已经完成全部字段校验的不可变通信日志。
        /// </summary>
        /// <param name="sequenceId">日志服务分配的严格递增正序号。</param>
        /// <param name="transactionId">关联事务编号；系统记录可为空。</param>
        /// <param name="direction">发送、接收、错误、迟到或系统类别。</param>
        /// <param name="timestamp">该记录发生时的 UTC 或可转换为 UTC 的日历时间。</param>
        /// <param name="state">关联事务终态；尚未完成的发送记录和系统记录可为空。</param>
        /// <param name="portGeneration">产生该记录的串口会话代次；零表示尚未打开首个会话。</param>
        /// <param name="receiveSequence">接收块序号；非接收记录使用零。</param>
        /// <param name="rawData">需要完整保留的线路字节；无线路字节时为空。</param>
        /// <param name="summary">面向界面和导出文件的非空中文摘要。</param>
        /// <exception cref="ArgumentOutOfRangeException">序号、事务编号、端口代次、接收序号或枚举非法时抛出。</exception>
        /// <exception cref="ArgumentException"><paramref name="summary"/> 为空时抛出。</exception>
        public CommunicationLogEntry(
            long sequenceId,
            long? transactionId,
            CommunicationDirection direction,
            DateTimeOffset timestamp,
            TransactionCompletionState? state,
            int portGeneration,
            long receiveSequence,
            ReadOnlySpan<byte> rawData,
            string summary)
        {
            if (sequenceId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequenceId),
                    sequenceId,
                    "通信日志序号必须为正数。");
            }

            if (transactionId is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transactionId),
                    transactionId,
                    "事务编号存在时必须为正数。");
            }

            if (!Enum.IsDefined(direction))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(direction),
                    direction,
                    "通信方向必须是已定义的枚举值。");
            }

            if (state.HasValue && !Enum.IsDefined(state.Value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(state),
                    state,
                    "事务终态必须是已定义的枚举值。");
            }

            if (portGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(portGeneration),
                    portGeneration,
                    "串口会话代次不能为负数。");
            }

            if (receiveSequence < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(receiveSequence),
                    receiveSequence,
                    "接收序号不能为负数。");
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new ArgumentException("通信日志摘要不能为空。", nameof(summary));
            }

            SequenceId = sequenceId;
            TransactionId = transactionId;
            Direction = direction;
            Timestamp = timestamp.ToUniversalTime();
            State = state;
            PortGeneration = portGeneration;
            ReceiveSequence = receiveSequence;
            this.rawData = rawData.ToArray();
            RawHex = FormatHex(rawData);
            Summary = summary.Trim();
        }

        /// <summary>
        /// 获取日志服务分配的严格递增序号。
        /// </summary>
        public long SequenceId { get; }

        /// <summary>
        /// 获取关联事务编号；系统记录可为空。
        /// </summary>
        public long? TransactionId { get; }

        /// <summary>
        /// 获取该记录的通信方向或诊断类别。
        /// </summary>
        public CommunicationDirection Direction { get; }

        /// <summary>
        /// 获取规范化为 UTC 的记录时间。
        /// </summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>
        /// 获取关联事务终态；尚未完成的发送记录和系统记录可为空。
        /// </summary>
        public TransactionCompletionState? State { get; }

        /// <summary>
        /// 获取产生该记录的串口会话代次。
        /// </summary>
        public int PortGeneration { get; }

        /// <summary>
        /// 获取接收块序号；非接收记录为零。
        /// </summary>
        public long ReceiveSequence { get; }

        /// <summary>
        /// 获取完整原始线路字节的防御性副本。
        /// </summary>
        public ReadOnlyMemory<byte> RawData => (byte[])rawData.Clone();

        /// <summary>
        /// 获取使用大写两位十六进制和单空格分隔的稳定原始数据显示。
        /// </summary>
        public string RawHex { get; }

        /// <summary>
        /// 获取面向界面和导出文件的中文摘要。
        /// </summary>
        public string Summary { get; }

        /// <summary>
        /// 将原始字节格式化为大写、两位和单空格分隔的十六进制文本。
        /// </summary>
        /// <param name="data">需要格式化的完整线路字节。</param>
        /// <returns>空数据返回空字符串；否则返回例如 <c>01 03 00 00</c> 的文本。</returns>
        private static string FormatHex(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return string.Empty;
            }

            return string.Join(" ", data.ToArray().Select(value => value.ToString("X2")));
        }
    }
}
