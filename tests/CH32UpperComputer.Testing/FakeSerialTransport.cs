using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Testing
{
    /// <summary>
    /// 提供线程安全、有界且可主动注入数据或故障的内存串口传输，用于隔离生产协调器测试。
    /// </summary>
    public sealed class FakeSerialTransport : ISerialTransport
    {
        /// <summary>
        /// 默认单会话接收块容量，达到上限时生产者异步等待而不丢弃数据。
        /// </summary>
        public const int DefaultReceiveCapacity = 256;

        /// <summary>
        /// 默认保留的最近写帧数量，防止长期压力测试形成无界内存增长。
        /// </summary>
        public const int DefaultWriteHistoryCapacity = 1024;

        /// <summary>
        /// 保护会话切换、接收序号和写历史的同步门。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 串行化并发注入，确保分配的接收序号与通道中的实际块顺序严格一致。
        /// </summary>
        private readonly SemaphoreSlim injectGate = new(1, 1);

        /// <summary>
        /// 为注入数据生成日历时间和单调时间戳的时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 每个新会话创建的有界接收通道容量。
        /// </summary>
        private readonly int receiveCapacity;

        /// <summary>
        /// 最近写帧历史的最大保留数量。
        /// </summary>
        private readonly int writeHistoryCapacity;

        /// <summary>
        /// 按发送顺序保存的独占写帧副本。
        /// </summary>
        private readonly List<byte[]> writtenFrames = [];

        /// <summary>
        /// 当前打开会话；未打开或会话结束时为 <see langword="null"/>。
        /// </summary>
        private Session? currentSession;

        /// <summary>
        /// 最近一次成功打开所分配的会话代次。
        /// </summary>
        private int portGeneration;

        /// <summary>
        /// 下一次写入时需要抛出的脚本化 I/O 异常。
        /// </summary>
        private IOException? nextWriteException;

        /// <summary>
        /// 指示传输对象是否已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 初始化使用系统时间和默认有界容量的模拟传输。
        /// </summary>
        public FakeSerialTransport()
            : this(TimeProvider.System)
        {
        }

        /// <summary>
        /// 初始化使用指定时间源及有界容量的模拟传输。
        /// </summary>
        /// <param name="timeProvider">注入接收块时读取 UTC 和单调时间戳的时间源。</param>
        /// <param name="receiveCapacity">每个会话允许缓存的最大接收块数量。</param>
        /// <param name="writeHistoryCapacity">允许保留的最近写帧数量。</param>
        /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> 为 <see langword="null"/> 时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">任一容量不是正数时抛出。</exception>
        public FakeSerialTransport(
            TimeProvider timeProvider,
            int receiveCapacity = DefaultReceiveCapacity,
            int writeHistoryCapacity = DefaultWriteHistoryCapacity)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);

            if (receiveCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(receiveCapacity),
                    receiveCapacity,
                    "接收通道容量必须为正数。");
            }

            if (writeHistoryCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(writeHistoryCapacity),
                    writeHistoryCapacity,
                    "写历史容量必须为正数。");
            }

            this.timeProvider = timeProvider;
            this.receiveCapacity = receiveCapacity;
            this.writeHistoryCapacity = writeHistoryCapacity;
        }

        /// <summary>
        /// 获取当前是否存在仍可读写的打开会话。
        /// </summary>
        public bool IsOpen
        {
            get
            {
                lock (syncRoot)
                {
                    return currentSession is not null;
                }
            }
        }

        /// <summary>
        /// 获取最近一次成功打开所分配的递增会话代次。
        /// </summary>
        public int PortGeneration
        {
            get
            {
                lock (syncRoot)
                {
                    return portGeneration;
                }
            }
        }

        /// <summary>
        /// 获取最近写帧的防御性只读快照；修改其中内存不会影响内部历史。
        /// </summary>
        public IReadOnlyList<ReadOnlyMemory<byte>> WrittenFrames
        {
            get
            {
                lock (syncRoot)
                {
                    ReadOnlyMemory<byte>[] snapshot = writtenFrames
                        .Select(frame => new ReadOnlyMemory<byte>((byte[])frame.Clone()))
                        .ToArray();
                    return new ReadOnlyCollection<ReadOnlyMemory<byte>>(snapshot);
                }
            }
        }

        /// <summary>
        /// 打开全新模拟会话，递增代次并重置该会话的接收序号。
        /// </summary>
        /// <param name="settings">经过验证的串口参数；模拟器不访问操作系统端口。</param>
        /// <param name="cancellationToken">在状态变更前取消打开操作的令牌。</param>
        /// <returns>同步完成的值任务。</returns>
        /// <exception cref="InvalidOperationException">对象已释放或已有打开会话时抛出。</exception>
        public ValueTask OpenAsync(
            SerialSettings settings,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            cancellationToken.ThrowIfCancellationRequested();

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();

                if (currentSession is not null)
                {
                    throw new InvalidOperationException("模拟串口已经打开。");
                }

                int newGeneration = checked(portGeneration + 1);
                currentSession = new Session(newGeneration, receiveCapacity);
                portGeneration = newGeneration;
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 保存完整发送帧的独占副本，或执行预先脚本化的一次性写异常。
        /// </summary>
        /// <param name="frame">待记录的非空发送帧。</param>
        /// <param name="cancellationToken">在状态变更前取消写入操作的令牌。</param>
        /// <returns>同步完成的值任务。</returns>
        /// <exception cref="ArgumentException"><paramref name="frame"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">当前没有打开会话时抛出。</exception>
        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken)
        {
            if (frame.IsEmpty)
            {
                throw new ArgumentException("发送帧不能为空。", nameof(frame));
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] frameCopy = frame.ToArray();

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                EnsureOpenUnderLock();

                if (nextWriteException is not null)
                {
                    IOException exception = nextWriteException;
                    nextWriteException = null;
                    throw exception;
                }

                if (writtenFrames.Count == writeHistoryCapacity)
                {
                    writtenFrames.RemoveAt(0);
                }

                writtenFrames.Add(frameCopy);
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 返回当前会话的有界异步接收序列；关闭后自然结束，故障后传播底层异常。
        /// </summary>
        /// <param name="cancellationToken">取消等待或枚举的令牌。</param>
        /// <returns>捕获调用时会话身份的数据块序列。</returns>
        /// <exception cref="InvalidOperationException">当前没有打开会话时抛出。</exception>
        public IAsyncEnumerable<SerialReceiveChunk> ReadAllAsync(
            CancellationToken cancellationToken)
        {
            Session session;

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                session = EnsureOpenUnderLock();
            }

            return ReadSessionAsync(session, cancellationToken);
        }

        /// <summary>
        /// 关闭当前模拟会话并完成其接收通道，使全部等待读取确定性退出。
        /// </summary>
        /// <param name="cancellationToken">在状态变更前取消关闭操作的令牌。</param>
        /// <returns>同步完成的值任务；重复关闭不会失败。</returns>
        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session? session;

            lock (syncRoot)
            {
                session = currentSession;
                currentSession = null;
            }

            session?.ReceiveChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 将一个非空接收块注入当前会话；通道已满时异步等待可用空间。
        /// </summary>
        /// <param name="data">待注入的非空线路字节块。</param>
        /// <param name="cancellationToken">取消容量等待的令牌。</param>
        /// <returns>注入成功后完成的值任务。</returns>
        /// <exception cref="ArgumentException"><paramref name="data"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">当前没有打开会话时抛出。</exception>
        public async ValueTask InjectReceiveAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            if (data.IsEmpty)
            {
                throw new ArgumentException("注入的接收块不能为空。", nameof(data));
            }

            await injectGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                SerialReceiveChunk chunk;
                ChannelWriter<SerialReceiveChunk> writer;

                lock (syncRoot)
                {
                    ThrowIfDisposedUnderLock();
                    Session session = EnsureOpenUnderLock();
                    long sequence = checked(++session.ReceiveSequence);
                    chunk = new SerialReceiveChunk(
                        data.Span,
                        session.Generation,
                        sequence,
                        timeProvider.GetUtcNow(),
                        timeProvider.GetTimestamp());
                    writer = session.ReceiveChannel.Writer;
                }

                await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                injectGate.Release();
            }
        }

        /// <summary>
        /// 设置下一次写操作抛出的一次性 I/O 异常。
        /// </summary>
        /// <param name="exception">下一次写操作需要原样抛出的异常。</param>
        public void FailNextWrite(IOException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                nextWriteException = exception;
            }
        }

        /// <summary>
        /// 模拟远端断开，可选择使读取正常结束或以 I/O 异常结束。
        /// </summary>
        /// <param name="exception">需要传播给读取方的故障；为 <see langword="null"/> 时正常结束序列。</param>
        public void RemoteDisconnect(IOException? exception = null)
        {
            Session? session;

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                session = currentSession;
                currentSession = null;
            }

            session?.ReceiveChannel.Writer.TryComplete(exception);
        }

        /// <summary>
        /// 幂等关闭当前会话并永久释放模拟传输。
        /// </summary>
        /// <returns>同步完成的值任务。</returns>
        public ValueTask DisposeAsync()
        {
            Session? session;

            lock (syncRoot)
            {
                if (isDisposed)
                {
                    return ValueTask.CompletedTask;
                }

                isDisposed = true;
                session = currentSession;
                currentSession = null;
            }

            session?.ReceiveChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 枚举指定会话通道中的全部数据块，并保留通道完成时的异常语义。
        /// </summary>
        /// <param name="session">调用读取方法时捕获的独立会话。</param>
        /// <param name="cancellationToken">取消等待和枚举的令牌。</param>
        /// <returns>当前会话的异步数据块序列。</returns>
        private static async IAsyncEnumerable<SerialReceiveChunk> ReadSessionAsync(
            Session session,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (SerialReceiveChunk chunk in session.ReceiveChannel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        /// <summary>
        /// 在已持有同步门时获取当前打开会话。
        /// </summary>
        /// <returns>当前打开会话。</returns>
        /// <exception cref="InvalidOperationException">当前没有打开会话时抛出。</exception>
        private Session EnsureOpenUnderLock()
        {
            return currentSession ??
                throw new InvalidOperationException("模拟串口尚未打开或已经断开。");
        }

        /// <summary>
        /// 在已持有同步门时拒绝释放后的进一步操作。
        /// </summary>
        /// <exception cref="ObjectDisposedException">对象已经释放时抛出。</exception>
        private void ThrowIfDisposedUnderLock()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
        }

        /// <summary>
        /// 保存一次打开会话独享的有界通道、代次和接收序号。
        /// </summary>
        private sealed class Session
        {
            /// <summary>
            /// 初始化一个使用等待写满策略的有界接收会话。
            /// </summary>
            /// <param name="generation">本会话唯一递增代次。</param>
            /// <param name="capacity">允许缓存的接收块数量。</param>
            internal Session(int generation, int capacity)
            {
                Generation = generation;
                ReceiveChannel = Channel.CreateBounded<SerialReceiveChunk>(
                    new BoundedChannelOptions(capacity)
                    {
                        AllowSynchronousContinuations = false,
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = false,
                        SingleWriter = false,
                    });
            }

            /// <summary>
            /// 获取本会话唯一代次。
            /// </summary>
            internal int Generation { get; }

            /// <summary>
            /// 获取本会话独享的有界接收通道。
            /// </summary>
            internal Channel<SerialReceiveChunk> ReceiveChannel { get; }

            /// <summary>
            /// 获取或设置最近分配的会话内接收序号。
            /// </summary>
            internal long ReceiveSequence { get; set; }
        }
    }
}
