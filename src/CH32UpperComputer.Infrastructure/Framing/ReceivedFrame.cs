namespace CH32UpperComputer.Infrastructure.Framing
{
    /// <summary>
    /// 指定一次接收捕获采用标准结构推导还是原始调试边界识别。
    /// </summary>
    public enum RtuFramingMode
    {
        /// <summary>
        /// 仅依据已知 Modbus 功能码结构推导长度。
        /// </summary>
        Standard = 0,

        /// <summary>
        /// 已知结构优先，并允许以 CRC 或静默边界保留未知原始数据。
        /// </summary>
        RawDebug = 1,
    }

    /// <summary>
    /// 指定组帧状态机产生的接收记录类别。
    /// </summary>
    public enum ReceivedFrameKind
    {
        /// <summary>
        /// 已依据 0x03、0x06、0x10 或异常响应结构得到完整帧。
        /// </summary>
        StandardFrame = 0,

        /// <summary>
        /// 未知结构在原始模式下通过有效 CRC 边界得到完整帧。
        /// </summary>
        RawCrcFrame = 1,

        /// <summary>
        /// 无法推导或验证结构，在静默或总超时后保存的原始捕获。
        /// </summary>
        UnparsedRawCapture = 2,

        /// <summary>
        /// 有界接收缓存或原始捕获达到容量上限。
        /// </summary>
        ReceiveOverflow = 3,

        /// <summary>
        /// 接收结构、会话身份或单调时间约束被违反。
        /// </summary>
        ProtocolError = 4,
    }

    /// <summary>
    /// 表示组帧状态机输出的一项不可变接收记录，并独占保存其原始字节。
    /// </summary>
    public sealed class ReceivedFrame
    {
        /// <summary>
        /// 当前记录独占持有的字节副本。
        /// </summary>
        private readonly byte[] data;

        /// <summary>
        /// 初始化一项包含完整来源与容量诊断信息的接收记录。
        /// </summary>
        /// <param name="kind">记录类别。</param>
        /// <param name="data">需要由记录独占保存的原始字节或截断摘要。</param>
        /// <param name="portGeneration">生成该记录的串口会话代次。</param>
        /// <param name="frameSequence">当前捕获内严格递增的记录序号。</param>
        /// <param name="firstReceiveSequence">贡献首字节的传输块接收序号；无来源块时为零。</param>
        /// <param name="lastReceiveSequence">贡献末字节的传输块接收序号；无来源块时为零。</param>
        /// <param name="startedTimestamp">该段数据开始捕获时的单调时间戳。</param>
        /// <param name="completedTimestamp">记录确定完成时的单调时间戳。</param>
        /// <param name="isCrcValid">当前记录是否具有有效 Modbus CRC。</param>
        /// <param name="diagnostic">面向日志的简明诊断；无诊断时为空字符串。</param>
        /// <param name="observedByteCount">本段实际观察到的字节总数。</param>
        /// <param name="overflowByteCount">因容量上限未被保存在摘要中的字节数。</param>
        /// <exception cref="ArgumentOutOfRangeException">任一计数、序号或时间戳不满足约束时抛出。</exception>
        public ReceivedFrame(
            ReceivedFrameKind kind,
            ReadOnlySpan<byte> data,
            int portGeneration,
            long frameSequence,
            long firstReceiveSequence,
            long lastReceiveSequence,
            long startedTimestamp,
            long completedTimestamp,
            bool isCrcValid,
            string? diagnostic,
            int observedByteCount,
            int overflowByteCount)
        {
            if (portGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(portGeneration));
            }

            if (frameSequence <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameSequence));
            }

            if (firstReceiveSequence < 0 || lastReceiveSequence < firstReceiveSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(firstReceiveSequence));
            }

            if (startedTimestamp < 0 || completedTimestamp < startedTimestamp)
            {
                throw new ArgumentOutOfRangeException(nameof(startedTimestamp));
            }

            if (observedByteCount < data.Length || overflowByteCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(observedByteCount));
            }

            this.data = data.ToArray();
            Kind = kind;
            PortGeneration = portGeneration;
            FrameSequence = frameSequence;
            FirstReceiveSequence = firstReceiveSequence;
            LastReceiveSequence = lastReceiveSequence;
            StartedTimestamp = startedTimestamp;
            CompletedTimestamp = completedTimestamp;
            IsCrcValid = isCrcValid;
            Diagnostic = diagnostic ?? string.Empty;
            ObservedByteCount = observedByteCount;
            OverflowByteCount = overflowByteCount;
        }

        /// <summary>
        /// 获取接收记录类别。
        /// </summary>
        public ReceivedFrameKind Kind { get; }

        /// <summary>
        /// 获取原始字节的防御性副本。
        /// </summary>
        public ReadOnlyMemory<byte> Data => (byte[])data.Clone();

        /// <summary>
        /// 获取生成该记录的串口会话代次。
        /// </summary>
        public int PortGeneration { get; }

        /// <summary>
        /// 获取当前捕获内严格递增的记录序号。
        /// </summary>
        public long FrameSequence { get; }

        /// <summary>
        /// 获取贡献首字节的传输块接收序号。
        /// </summary>
        public long FirstReceiveSequence { get; }

        /// <summary>
        /// 获取贡献末字节的传输块接收序号。
        /// </summary>
        public long LastReceiveSequence { get; }

        /// <summary>
        /// 获取为兼容日志排序而使用的末来源接收序号。
        /// </summary>
        public long ReceiveSequence => LastReceiveSequence;

        /// <summary>
        /// 获取该段数据开始捕获时的单调时间戳。
        /// </summary>
        public long StartedTimestamp { get; }

        /// <summary>
        /// 获取该记录确定完成时的单调时间戳。
        /// </summary>
        public long CompletedTimestamp { get; }

        /// <summary>
        /// 获取原始字节是否具有有效 Modbus CRC。
        /// </summary>
        public bool IsCrcValid { get; }

        /// <summary>
        /// 获取面向通信日志的诊断文本。
        /// </summary>
        public string Diagnostic { get; }

        /// <summary>
        /// 获取本段实际观察到的字节总数。
        /// </summary>
        public int ObservedByteCount { get; }

        /// <summary>
        /// 获取容量溢出后未被保存在摘要中的字节数。
        /// </summary>
        public int OverflowByteCount { get; }
    }
}
