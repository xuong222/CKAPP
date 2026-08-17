namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 定义事务协调器所需的最小异步串口传输边界，并隔离具体 <c>SerialPort</c> 实现。
    /// </summary>
    public interface ISerialTransport : IAsyncDisposable
    {
        /// <summary>
        /// 获取当前是否存在可读写的已打开会话。
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// 获取旧串口会话的物理句柄是否仍在后台释放。
        /// </summary>
        bool IsCleanupPending { get; }

        /// <summary>
        /// 在后台物理清理开始或结束时发布新状态。
        /// </summary>
        event Action<bool>? CleanupPendingChanged;

        /// <summary>
        /// 获取公开端口代次；打开成功和活动会话失效时均递增，用于隔离关闭前的迟到数据。
        /// </summary>
        int PortGeneration { get; }

        /// <summary>
        /// 使用完整参数打开一个新串口会话。
        /// </summary>
        /// <param name="settings">已经验证的不可变串口参数。</param>
        /// <param name="cancellationToken">取消尚未完成打开操作的令牌。</param>
        /// <returns>表示异步打开操作的值任务。</returns>
        ValueTask OpenAsync(
            SerialLineSettings settings,
            CancellationToken cancellationToken);

        /// <summary>
        /// 把一项完整线路帧写入当前会话，并在第一个物理写动作前建立响应接收边界。
        /// </summary>
        /// <param name="frame">待发送的非空完整线路帧。</param>
        /// <param name="tryBeginWrite">
        /// 传输层完成写门和会话复核后、开始物理写入前同步调用的授权入口；
        /// 返回假时必须放弃本次物理写入。
        /// </param>
        /// <param name="cancellationToken">取消尚未完成写入操作的令牌。</param>
        /// <returns>表示异步写入操作的值任务。</returns>
        ValueTask WriteAsync(
            ReadOnlyMemory<byte> frame,
            Func<bool> tryBeginWrite,
            CancellationToken cancellationToken);

        /// <summary>
        /// 连续读取当前会话产生的数据块，直至关闭、取消或底层故障。
        /// </summary>
        /// <param name="cancellationToken">终止当前读取枚举的令牌。</param>
        /// <returns>按会话内接收序号排序的异步数据块序列。</returns>
        IAsyncEnumerable<SerialReceiveChunk> ReadAllAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// 关闭当前会话并使正在等待的读取枚举确定性退出。
        /// </summary>
        /// <param name="cancellationToken">取消尚未完成关闭操作的令牌。</param>
        /// <returns>表示异步关闭操作的值任务。</returns>
        ValueTask CloseAsync(CancellationToken cancellationToken);
    }
}
