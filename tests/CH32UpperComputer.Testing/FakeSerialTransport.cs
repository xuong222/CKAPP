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
        /// 按成功打开顺序保存的不可变串口设置。
        /// </summary>
        private readonly List<SerialSettings> openHistory = [];

        /// <summary>
        /// 当前打开会话；未打开或会话结束时为 <see langword="null"/>。
        /// </summary>
        private Session? currentSession;

        /// <summary>
        /// 当前打开会话使用的完整串口设置；未打开时为空。
        /// </summary>
        private SerialLineSettings? currentLineSettings;

        /// <summary>
        /// 公开端口代次；打开成功与活动会话关闭、断开或释放时均递增。
        /// </summary>
        private int portGeneration;

        /// <summary>
        /// 通过 <see cref="CloseAsync"/> 实际关闭活动会话的次数。
        /// </summary>
        private int closeOperationCount;

        /// <summary>
        /// 首次永久释放模拟传输的次数；幂等重复释放不会增加该值。
        /// </summary>
        private int disposeOperationCount;

        /// <summary>
        /// 下一次写入时需要抛出的脚本化 I/O 异常。
        /// </summary>
        private IOException? nextWriteException;

        /// <summary>
        /// 下一次物理写入需要等待的可控暂停门；被写操作领取后立即清空。
        /// </summary>
        private FakeWritePause? nextWritePause;

        /// <summary>
        /// 下一次写入已经记录完整线路帧后、返回调用方前需要等待的可控暂停门。
        /// </summary>
        private FakeWritePause? nextWriteCompletionPause;

        /// <summary>
        /// 指示传输对象是否已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 在模拟清理状态发布时保存观察者。
        /// </summary>
        public event Action<bool>? CleanupPendingChanged;

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
        /// 获取模拟传输是否仍有后台物理清理；内存实现始终同步完成。
        /// </summary>
        public bool IsCleanupPending => false;

        /// <summary>
        /// 获取公开端口代次；打开成功与活动会话失效都会令该值递增。
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
        /// 获取当前打开会话使用的不可变串口设置；未打开时为空。
        /// </summary>
        public SerialSettings? CurrentSettings
        {
            get
            {
                lock (syncRoot)
                {
                    return currentLineSettings as SerialSettings;
                }
            }
        }

        /// <summary>
        /// 获取当前打开会话使用的通用线路设置；未打开时为空。
        /// </summary>
        public SerialLineSettings? CurrentLineSettings
        {
            get
            {
                lock (syncRoot)
                {
                    return currentLineSettings;
                }
            }
        }

        /// <summary>
        /// 获取全部成功打开设置的独立只读快照。
        /// </summary>
        public IReadOnlyList<SerialSettings> OpenHistory
        {
            get
            {
                lock (syncRoot)
                {
                    return new ReadOnlyCollection<SerialSettings>(openHistory.ToArray());
                }
            }
        }

        /// <summary>
        /// 获取通过关闭 API 实际关闭活动会话的次数。
        /// </summary>
        public int CloseOperationCount
        {
            get
            {
                lock (syncRoot)
                {
                    return closeOperationCount;
                }
            }
        }

        /// <summary>
        /// 获取模拟传输实际进入永久释放路径的次数。
        /// </summary>
        public int DisposeOperationCount
        {
            get
            {
                lock (syncRoot)
                {
                    return disposeOperationCount;
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
            SerialLineSettings settings,
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
                currentLineSettings = settings;

                if (settings is SerialSettings serialSettings)
                {
                    openHistory.Add(serialSettings);
                }
                portGeneration = newGeneration;
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 保存完整发送帧的独占副本，或执行预先脚本化的一次性写异常。
        /// </summary>
        /// <param name="frame">待记录的非空发送帧。</param>
        /// <param name="tryBeginWrite">完成预写暂停后、记录线路帧前调用的同步授权入口。</param>
        /// <param name="cancellationToken">在状态变更前取消写入操作的令牌。</param>
        /// <returns>无暂停时同步完成；配置暂停时在测试释放门后异步完成；授权失败时不记录帧。</returns>
        /// <exception cref="ArgumentException"><paramref name="frame"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="tryBeginWrite"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">当前没有打开会话时抛出。</exception>
        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> frame,
            Func<bool> tryBeginWrite,
            CancellationToken cancellationToken)
        {
            if (frame.IsEmpty)
            {
                throw new ArgumentException("发送帧不能为空。", nameof(frame));
            }

            ArgumentNullException.ThrowIfNull(tryBeginWrite);

            cancellationToken.ThrowIfCancellationRequested();
            byte[] frameCopy = frame.ToArray();
            Session session;
            FakeWritePause? writePause;
            FakeWritePause? writeCompletionPause;

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                session = EnsureOpenUnderLock();

                if (nextWriteException is not null)
                {
                    IOException exception = nextWriteException;
                    nextWriteException = null;
                    throw exception;
                }

                writePause = nextWritePause;
                nextWritePause = null;
                writeCompletionPause = nextWriteCompletionPause;
                nextWriteCompletionPause = null;
            }

            if (writePause is not null)
            {
                writePause.SignalEntered();
                await writePause.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!tryBeginWrite())
            {
                return;
            }

            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();

                if (!ReferenceEquals(currentSession, session))
                {
                    throw new IOException("模拟写入暂停期间串口会话已经失效。");
                }

                if (writtenFrames.Count == writeHistoryCapacity)
                {
                    writtenFrames.RemoveAt(0);
                }

                writtenFrames.Add(frameCopy);
            }

            if (writeCompletionPause is not null)
            {
                writeCompletionPause.SignalEntered();
                await writeCompletionPause
                    .WaitForReleaseAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
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
        /// 关闭当前模拟会话、立即递增公开代次并完成其接收通道，使全部等待读取确定性退出。
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

                if (session is not null)
                {
                    currentSession = null;
                    currentLineSettings = null;
                    closeOperationCount = checked(closeOperationCount + 1);
                    portGeneration = checked(portGeneration + 1);
                }
            }

            session?.ReceiveChannel.Writer.TryComplete();
            CleanupPendingChanged?.Invoke(false);
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
        /// 使用调用方指定的日历时间和单调时间戳注入接收块，用于精确验证截止边界。
        /// </summary>
        /// <param name="data">待注入的非空线路字节块。</param>
        /// <param name="arrivedAtUtc">块进入传输层时的明确 UTC 日历时间。</param>
        /// <param name="monotonicTimestamp">由同一测试时间基准定义的非负单调时间戳。</param>
        /// <param name="cancellationToken">取消容量等待的令牌。</param>
        /// <returns>注入成功后完成的值任务。</returns>
        /// <exception cref="ArgumentException"><paramref name="data"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="monotonicTimestamp"/> 为负数时抛出。</exception>
        /// <exception cref="InvalidOperationException">当前没有打开会话时抛出。</exception>
        public async ValueTask InjectReceiveAtAsync(
            ReadOnlyMemory<byte> data,
            DateTimeOffset arrivedAtUtc,
            long monotonicTimestamp,
            CancellationToken cancellationToken = default)
        {
            if (data.IsEmpty)
            {
                throw new ArgumentException("注入的接收块不能为空。", nameof(data));
            }

            if (monotonicTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));
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
                        arrivedAtUtc,
                        monotonicTimestamp);
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
        /// 让下一次写入在进入物理写阶段后暂停，供测试精确制造 Sending 状态接收竞态。
        /// </summary>
        /// <returns>可等待写入进入并由测试显式释放的单次暂停门。</returns>
        /// <exception cref="InvalidOperationException">当前没有打开会话，或已经存在尚未被下一次写入领取的暂停门时抛出。</exception>
        public FakeWritePause PauseNextWrite()
        {
            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                EnsureOpenUnderLock();

                if (nextWritePause is not null)
                {
                    throw new InvalidOperationException("下一次写入已经配置暂停门。");
                }

                nextWritePause = new FakeWritePause();
                return nextWritePause;
            }
        }

        /// <summary>
        /// 让下一次完整线路帧已经交给模拟设备后、写调用返回协调器前暂停。
        /// </summary>
        /// <returns>可等待写入完成边界并由测试显式释放的单次暂停门。</returns>
        /// <exception cref="InvalidOperationException">当前没有打开会话，或已经存在尚未被下一次写入领取的完成暂停门时抛出。</exception>
        public FakeWritePause PauseNextWriteCompletion()
        {
            lock (syncRoot)
            {
                ThrowIfDisposedUnderLock();
                EnsureOpenUnderLock();

                if (nextWriteCompletionPause is not null)
                {
                    throw new InvalidOperationException("下一次写入已经配置完成暂停门。");
                }

                nextWriteCompletionPause = new FakeWritePause();
                return nextWriteCompletionPause;
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

                if (session is not null)
                {
                    currentSession = null;
                    currentLineSettings = null;
                    portGeneration = checked(portGeneration + 1);
                }
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
                disposeOperationCount = checked(disposeOperationCount + 1);
                session = currentSession;

                if (session is not null)
                {
                    currentSession = null;
                    currentLineSettings = null;
                    portGeneration = checked(portGeneration + 1);
                }
            }

            session?.ReceiveChannel.Writer.TryComplete();
            CleanupPendingChanged?.Invoke(false);
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

    /// <summary>
    /// 控制 FakeSerialTransport 下一次写入的进入与释放时刻。
    /// </summary>
    public sealed class FakeWritePause
    {
        /// <summary>
        /// 写入进入暂停点时完成的异步信号。
        /// </summary>
        private readonly TaskCompletionSource enteredSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 测试释放写入时完成的异步信号。
        /// </summary>
        private readonly TaskCompletionSource<IOException?> releaseSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 获取下一次写入已经进入暂停点的任务。
        /// </summary>
        public Task Entered => enteredSource.Task;

        /// <summary>
        /// 幂等释放已经进入暂停点的写入。
        /// </summary>
        public void Release()
        {
            releaseSource.TrySetResult(null);
        }

        /// <summary>
        /// 让已经进入暂停点的写操作以指定 I/O 故障退出，用于制造写失败与取消的完成门竞态。
        /// </summary>
        /// <param name="exception">写操作恢复后需要原样抛出的串口 I/O 异常。</param>
        public void ReleaseWithFailure(IOException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            releaseSource.TrySetResult(exception);
        }

        /// <summary>
        /// 由 Fake 写入路径发布已经进入暂停点。
        /// </summary>
        internal void SignalEntered()
        {
            enteredSource.TrySetResult();
        }

        /// <summary>
        /// 等待测试释放暂停点或调用方取消写入。
        /// </summary>
        /// <param name="cancellationToken">取消当前写入等待的令牌。</param>
        /// <returns>暂停门释放后完成的任务。</returns>
        internal async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            IOException? exception = await releaseSource.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (exception is not null)
            {
                throw exception;
            }
        }
    }
}
