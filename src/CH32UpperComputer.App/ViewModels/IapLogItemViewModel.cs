using CH32UpperComputer.Core.Iap;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Iap;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 把一条 IAP 日志转换为适合深色列表换行显示的轻量文本。
    /// </summary>
    public sealed class IapLogItemViewModel
    {
        /// <summary>
        /// 原始不可变日志。
        /// </summary>
        private readonly IapCommunicationLogEntry entry;

        /// <summary>
        /// 初始化一条 IAP 日志显示项。
        /// </summary>
        /// <param name="entry">后台日志服务产生的不可变记录。</param>
        /// <param name="showFullDataPayload">DATA 是否显示完整载荷。</param>
        public IapLogItemViewModel(
            IapCommunicationLogEntry entry,
            bool showFullDataPayload)
        {
            ArgumentNullException.ThrowIfNull(entry);
            this.entry = entry;
            TimeText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
            DirectionText = entry.Direction switch
            {
                IapCommunicationDirection.Transmit => "TX",
                IapCommunicationDirection.Receive => "RX",
                _ => "SYS",
            };
            SummaryText = CreateSummary(entry);
            RawText = CreateRawText(entry, showFullDataPayload);
        }

        /// <summary>
        /// 获取本地时间文本。
        /// </summary>
        public string TimeText { get; }

        /// <summary>
        /// 获取方向缩写。
        /// </summary>
        public string DirectionText { get; }

        /// <summary>
        /// 获取命令、sequence、offset、状态和耗时摘要。
        /// </summary>
        public string SummaryText { get; }

        /// <summary>
        /// 获取按调试开关裁剪后的原始十六进制。
        /// </summary>
        public string RawText { get; }

        /// <summary>
        /// 获取原始日志，供重新应用 DATA 显示策略。
        /// </summary>
        public IapCommunicationLogEntry Entry => entry;

        /// <summary>
        /// 创建一行结构化摘要。
        /// </summary>
        /// <param name="logEntry">需要格式化的日志。</param>
        /// <returns>适合换行显示和导出的摘要。</returns>
        private static string CreateSummary(IapCommunicationLogEntry logEntry)
        {
            string command = logEntry.Command.HasValue
                ? $"{logEntry.Command.Value.ToString().ToUpperInvariant()} " +
                    $"(0x{(uint)logEntry.Command.Value:X8})"
                : "--";
            string sequence = logEntry.Sequence?.ToString() ?? "--";
            string status = logEntry.Status?.ToString().ToUpperInvariant() ?? "--";
            string detail = logEntry.Detail?.ToString() ?? "--";
            string duration = logEntry.Duration.HasValue
                ? $"{logEntry.Duration.Value.TotalMilliseconds:0} ms"
                : "--";
            return $"连接#{logEntry.ConnectionId} · {command} · seq {sequence} · " +
                $"offset {logEntry.Offset} · length {logEntry.Length} · " +
                $"{status}/{detail} · {duration} · {logEntry.Message}";
        }

        /// <summary>
        /// 根据 DATA 调试开关创建原始字节文本。
        /// </summary>
        /// <param name="logEntry">包含完整原始字节的日志。</param>
        /// <param name="showFullDataPayload">是否展开 DATA 的完整载荷。</param>
        /// <returns>完整、仅请求头或空原始字节文本。</returns>
        private static string CreateRawText(
            IapCommunicationLogEntry logEntry,
            bool showFullDataPayload)
        {
            if (logEntry.RawBytes.Length == 0)
            {
                return string.Empty;
            }

            ReadOnlySpan<byte> bytes = logEntry.RawBytes.Span;

            if (!showFullDataPayload &&
                logEntry.Command == IapCommand.Data &&
                logEntry.Direction == IapCommunicationDirection.Transmit &&
                bytes.Length > IapFrameCodec.RequestHeaderSize)
            {
                bytes = bytes[..IapFrameCodec.RequestHeaderSize];
                return $"{HexFrameParser.Format(bytes)}  … DATA 载荷已隐藏";
            }

            return HexFrameParser.Format(bytes);
        }
    }
}
