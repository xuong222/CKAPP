namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 表示后台读取循环一次提交的不可变接收数据块，而不是单个字节级界面更新。
    /// </summary>
    public sealed class SerialReceiveChunk
    {
        /// <summary>
        /// 当前实例独占持有的数据副本。
        /// </summary>
        private readonly byte[] data;

        /// <summary>
        /// 初始化一个带会话身份和双时间基准的串口接收数据块。
        /// </summary>
        /// <param name="data">本次后台读取获得的非空字节块。</param>
        /// <param name="portGeneration">生成该数据块的串口会话代次，零表示尚未进入首个生产会话的测试上下文。</param>
        /// <param name="receiveSequence">同一会话内严格递增且从一开始的接收序号。</param>
        /// <param name="arrivedAtUtc">数据块进入传输层时的 UTC 日历时间。</param>
        /// <param name="monotonicTimestamp">由同一 <see cref="TimeProvider"/> 提供的非负单调时间戳。</param>
        /// <exception cref="ArgumentException"><paramref name="data"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">代次、序号或单调时间戳不满足约束时抛出。</exception>
        public SerialReceiveChunk(
            ReadOnlySpan<byte> data,
            int portGeneration,
            long receiveSequence,
            DateTimeOffset arrivedAtUtc,
            long monotonicTimestamp)
        {
            if (data.IsEmpty)
            {
                throw new ArgumentException("串口接收数据块不能为空。", nameof(data));
            }

            if (portGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(portGeneration),
                    portGeneration,
                    "串口会话代次不能为负数。");
            }

            if (receiveSequence <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(receiveSequence),
                    receiveSequence,
                    "接收序号必须为正数。");
            }

            if (monotonicTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(monotonicTimestamp),
                    monotonicTimestamp,
                    "单调时间戳不能为负数。");
            }

            this.data = data.ToArray();
            PortGeneration = portGeneration;
            ReceiveSequence = receiveSequence;
            ArrivedAtUtc = arrivedAtUtc.ToUniversalTime();
            MonotonicTimestamp = monotonicTimestamp;
        }

        /// <summary>
        /// 获取数据块的防御性副本；调用方无法借此修改实例内部缓冲区。
        /// </summary>
        public ReadOnlyMemory<byte> Data => (byte[])data.Clone();

        /// <summary>
        /// 获取生成该数据块的串口会话代次。
        /// </summary>
        public int PortGeneration { get; }

        /// <summary>
        /// 获取同一会话内严格递增的接收序号。
        /// </summary>
        public long ReceiveSequence { get; }

        /// <summary>
        /// 获取数据块到达传输层时的 UTC 日历时间。
        /// </summary>
        public DateTimeOffset ArrivedAtUtc { get; }

        /// <summary>
        /// 获取数据块到达时的单调时间戳，用于不受系统时钟校准影响的超时计算。
        /// </summary>
        public long MonotonicTimestamp { get; }
    }
}
