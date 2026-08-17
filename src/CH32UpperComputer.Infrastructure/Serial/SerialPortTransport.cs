using System.IO;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 使用 <see cref="SerialPort.BaseStream"/> 和单一后台读取循环实现生产异步串口传输。
    /// </summary>
    public sealed class SerialPortTransport : ISerialTransport
    {
        /// <summary>
        /// 用户断开时等待驱动物理释放的最长前台时间。
        /// </summary>
        public static readonly TimeSpan ForegroundCloseTimeout =
            TimeSpan.FromMilliseconds(750);

        /// <summary>
        /// 每个串口会话允许等待消费的接收块数量。
        /// </summary>
        public const int ReceiveChannelCapacity = 8;

        /// <summary>
        /// 保护当前会话、公开代次和永久释放状态的同步门。
        /// </summary>
        private readonly object stateSyncRoot = new();

        /// <summary>
        /// 串行化打开、关闭和永久释放操作。
        /// </summary>
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);

        /// <summary>
        /// 串行化物理线路写入，避免多个完整帧在基础流交错。
        /// </summary>
        private readonly SemaphoreSlim writeGate = new(1, 1);

        /// <summary>
        /// 为接收块提供 UTC 与单调时间戳的统一时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 创建并配置串口对象的工厂；生产默认工厂使用完整 <see cref="SerialLineSettings"/>。
        /// </summary>
        private readonly Func<SerialLineSettings, SerialPort> serialPortFactory;

        /// <summary>
        /// 在 Modbus 页面与串口助手之间协调同一物理端口的进程内互斥。
        /// </summary>
        private readonly SerialPortUsageRegistry usageRegistry;

        /// <summary>
        /// 在端口冲突消息中标识当前传输所属页面。
        /// </summary>
        private readonly string ownerName;

        /// <summary>
        /// 当前可读写会话；关闭开始或读取循环终止后立即置空。
        /// </summary>
        private Session? currentSession;

        /// <summary>
        /// 最近启动的会话清理任务；下一次打开前必须已经退出。
        /// </summary>
        private Task previousSessionCompletion = Task.CompletedTask;

        /// <summary>
        /// 当前公开的端口代次；每次打开成功和每次活动会话失效时递增。
        /// </summary>
        private int portGeneration;

        /// <summary>
        /// 指示传输对象是否已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 指示旧会话的底层流或串口对象仍在后台释放。
        /// </summary>
        private int cleanupPendingFlag;

        /// <summary>
        /// 初始化使用系统时间源和真实串口工厂的生产传输。
        /// </summary>
        public SerialPortTransport()
            : this(
                TimeProvider.System,
                CreateConfiguredSerialPort,
                new SerialPortUsageRegistry(),
                "串口会话")
        {
        }

        /// <summary>
        /// 初始化使用指定时间源和真实串口工厂的生产传输。
        /// </summary>
        /// <param name="timeProvider">为接收块提供 UTC 和单调时间戳的统一时间源。</param>
        public SerialPortTransport(TimeProvider timeProvider)
            : this(
                timeProvider,
                CreateConfiguredSerialPort,
                new SerialPortUsageRegistry(),
                "串口会话")
        {
        }

        /// <summary>
        /// 初始化共享端口占用登记器的真实串口传输。
        /// </summary>
        /// <param name="timeProvider">为接收块提供 UTC 和单调时间戳的统一时间源。</param>
        /// <param name="usageRegistry">供多套传输共享的进程内端口占用登记器。</param>
        /// <param name="ownerName">在端口冲突消息中显示的当前页面名称。</param>
        public SerialPortTransport(
            TimeProvider timeProvider,
            SerialPortUsageRegistry usageRegistry,
            string ownerName)
            : this(
                timeProvider,
                CreateConfiguredSerialPort,
                usageRegistry,
                ownerName)
        {
        }

        /// <summary>
        /// 初始化使用指定时间源与串口工厂的传输，便于进行无硬件生命周期验证。
        /// </summary>
        /// <param name="timeProvider">为接收块提供 UTC 和单调时间戳的统一时间源。</param>
        /// <param name="serialPortFactory">根据不可变设置创建串口对象的工厂。</param>
        /// <exception cref="ArgumentNullException">任一依赖为空时抛出。</exception>
        public SerialPortTransport(
            TimeProvider timeProvider,
            Func<SerialLineSettings, SerialPort> serialPortFactory)
            : this(
                timeProvider,
                serialPortFactory,
                new SerialPortUsageRegistry(),
                "串口会话")
        {
        }

        /// <summary>
        /// 初始化使用可测试工厂、共享占用登记器和明确占用方名称的串口传输。
        /// </summary>
        /// <param name="timeProvider">为接收块提供 UTC 和单调时间戳的统一时间源。</param>
        /// <param name="serialPortFactory">根据不可变线路设置创建串口对象的工厂。</param>
        /// <param name="usageRegistry">供多套传输共享的进程内端口占用登记器。</param>
        /// <param name="ownerName">在端口冲突消息中显示的当前页面名称。</param>
        /// <exception cref="ArgumentException"><paramref name="ownerName"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentNullException">任一对象依赖为空时抛出。</exception>
        public SerialPortTransport(
            TimeProvider timeProvider,
            Func<SerialLineSettings, SerialPort> serialPortFactory,
            SerialPortUsageRegistry usageRegistry,
            string ownerName)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentNullException.ThrowIfNull(serialPortFactory);
            ArgumentNullException.ThrowIfNull(usageRegistry);

            if (string.IsNullOrWhiteSpace(ownerName))
            {
                throw new ArgumentException("串口占用方名称不能为空。", nameof(ownerName));
            }

            this.timeProvider = timeProvider;
            this.serialPortFactory = serialPortFactory;
            this.usageRegistry = usageRegistry;
            this.ownerName = ownerName.Trim();
        }

        /// <summary>
        /// 获取当前是否存在仍可读写的活动串口会话。
        /// </summary>
        public bool IsOpen
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return currentSession is not null;
                }
            }
        }

        /// <summary>
        /// 获取旧串口会话的物理句柄是否仍在后台释放。
        /// </summary>
        public bool IsCleanupPending => Volatile.Read(ref cleanupPendingFlag) != 0;

        /// <summary>
        /// 在后台物理清理开始或结束时发布新状态。
        /// </summary>
        public event Action<bool>? CleanupPendingChanged;

        /// <summary>
        /// 获取当前公开端口代次；关闭开始即变化，从而隔离旧会话迟到数据。
        /// </summary>
        public int PortGeneration
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return portGeneration;
                }
            }
        }

        /// <summary>
        /// 打开使用指定参数的新串口会话，并启动唯一后台读取循环。
        /// </summary>
        /// <param name="settings">已经验证的不可变串口参数。</param>
        /// <param name="cancellationToken">取消尚未开始或尚未完成的打开操作。</param>
        /// <returns>串口已打开且读取循环已启动后完成的值任务。</returns>
        /// <exception cref="InvalidOperationException">已有活动会话或上一读取循环尚未完全退出时抛出。</exception>
        /// <exception cref="ObjectDisposedException">传输对象已经释放时抛出。</exception>
        public async ValueTask OpenAsync(
            SerialLineSettings settings,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                Task priorCompletion;

                lock (stateSyncRoot)
                {
                    ThrowIfDisposedUnderLock();

                    if (currentSession is not null)
                    {
                        throw new InvalidOperationException("串口已经打开。");
                    }

                    priorCompletion = previousSessionCompletion;

                    if (!priorCompletion.IsCompleted)
                    {
                        throw new InvalidOperationException("上一串口读取循环仍在退出，请稍后重试。");
                    }
                }

                await priorCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                SerialPortLease? portLease = usageRegistry.Acquire(
                    settings.PortName,
                    ownerName);
                SerialPort serialPort;

                try
                {
                    serialPort = serialPortFactory(settings) ??
                        throw new InvalidOperationException("串口工厂返回了空对象。");
                }
                catch
                {
                    portLease.Dispose();
                    throw;
                }

                Stream? baseStream = null;

                try
                {
                    serialPort.Open();
                    cancellationToken.ThrowIfCancellationRequested();
                    baseStream = serialPort.BaseStream;

                    int newGeneration;

                    lock (stateSyncRoot)
                    {
                        ThrowIfDisposedUnderLock();

                        if (currentSession is not null)
                        {
                            throw new InvalidOperationException("串口打开期间出现了另一个活动会话。");
                        }

                        newGeneration = checked(portGeneration + 1);
                        portGeneration = newGeneration;
                    }

                    Session session = new(
                        serialPort,
                        baseStream,
                        newGeneration,
                        ReceiveChannelCapacity,
                        portLease);

                    lock (stateSyncRoot)
                    {
                        currentSession = session;
                    }

                    Task completion = RunSessionAsync(session);
                    session.SetPumpCompletion(completion);

                    lock (stateSyncRoot)
                    {
                        previousSessionCompletion = completion;
                    }

                    serialPort = null!;
                    baseStream = null;
                    portLease = null;
                }
                finally
                {
                    if (baseStream is not null)
                    {
                        baseStream.Dispose();
                    }

                    if (serialPort is not null)
                    {
                        serialPort.Dispose();
                    }

                    portLease?.Dispose();
                }
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 把一个非空完整帧异步写入当前会话的基础流并刷新。
        /// </summary>
        /// <param name="frame">待发送的非空完整线路帧。</param>
        /// <param name="tryBeginWrite">
        /// 写门和会话复核通过后、调用基础流写入前建立响应边界的同步授权入口。
        /// </param>
        /// <param name="cancellationToken">取消写门等待、物理写入或刷新。</param>
        /// <returns>授权成功时在全部字节交给基础流并刷新后完成；授权失败时不写串口并同步结束。</returns>
        /// <exception cref="ArgumentException"><paramref name="frame"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="tryBeginWrite"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">没有活动会话或会话在等待写门期间变化时抛出。</exception>
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

            Session session;

            lock (stateSyncRoot)
            {
                ThrowIfDisposedUnderLock();
                session = currentSession ??
                    throw new InvalidOperationException("串口尚未打开或已经断开。");
            }

            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                lock (stateSyncRoot)
                {
                    if (!ReferenceEquals(currentSession, session) ||
                        portGeneration != session.Generation)
                    {
                        throw new InvalidOperationException("等待发送期间串口会话已经变化。");
                    }
                }

                if (!tryBeginWrite())
                {
                    return;
                }

                await session.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await session.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writeGate.Release();
            }
        }

        /// <summary>
        /// 返回调用时活动会话的有界异步接收序列，并保留通道完成异常。
        /// </summary>
        /// <param name="cancellationToken">取消等待或枚举当前会话数据。</param>
        /// <returns>当前会话按接收序号排列的异步数据块。</returns>
        /// <exception cref="InvalidOperationException">当前没有活动会话时抛出。</exception>
        public IAsyncEnumerable<SerialReceiveChunk> ReadAllAsync(
            CancellationToken cancellationToken)
        {
            Session session;

            lock (stateSyncRoot)
            {
                ThrowIfDisposedUnderLock();
                session = currentSession ??
                    throw new InvalidOperationException("串口尚未打开或已经断开。");
            }

            return ReadSessionAsync(session, cancellationToken);
        }

        /// <summary>
        /// 关闭当前会话，使代次立即失效，并等待唯一后台读取循环退出。
        /// </summary>
        /// <param name="cancellationToken">仅取消尚未取得生命周期门的关闭请求；清理开始后必定完成。</param>
        /// <returns>资源已释放、读取通道已完成且后台循环已退出后完成的值任务。</returns>
        public async ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await CloseCurrentSessionUnderLifecycleGateAsync().ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 幂等关闭当前会话并永久释放同步原语。
        /// </summary>
        /// <returns>全部会话资源与后台读取循环已经收敛后完成的值任务。</returns>
        public async ValueTask DisposeAsync()
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);

            try
            {
                lock (stateSyncRoot)
                {
                    if (isDisposed)
                    {
                        return;
                    }

                    isDisposed = true;
                }

                await CloseCurrentSessionUnderLifecycleGateAsync().ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 运行一个会话的唯一读取循环，并在其自然结束或故障后使会话失效和释放资源。
        /// </summary>
        /// <param name="session">本次后台循环独占的会话对象。</param>
        /// <returns>通道完成、会话失效且底层资源释放后完成的任务。</returns>
        private async Task RunSessionAsync(Session session)
        {
            try
            {
                await SerialReadPump.RunAsync(
                    session.BaseStream,
                    session.ReceiveChannel.Writer,
                    session.Generation,
                    timeProvider,
                    session.Cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // SerialReadPump 已用原异常完成通道；在此观察任务异常，避免形成未观察后台异常。
            }
            finally
            {
                lock (stateSyncRoot)
                {
                    if (ReferenceEquals(currentSession, session))
                    {
                        currentSession = null;
                        portGeneration = checked(portGeneration + 1);
                    }
                }

                Task physicalCleanup = session.StartPhysicalCleanup();
                SetCleanupPending(true);

                try
                {
                    await physicalCleanup.ConfigureAwait(false);
                }
                finally
                {
                    session.ReceiveChannel.Writer.TryComplete();
                    session.DisposeCancellation();
                    session.DisposePortLease();
                    SetCleanupPending(false);
                }
            }
        }

        /// <summary>
        /// 在已持有生命周期门时移出并关闭当前会话。
        /// </summary>
        /// <returns>不存在会话时同步完成；否则在后台读取循环退出后完成。</returns>
        private async Task CloseCurrentSessionUnderLifecycleGateAsync()
        {
            Session? session;
            Task completion;

            lock (stateSyncRoot)
            {
                session = currentSession;

                if (session is null)
                {
                    completion = previousSessionCompletion;
                }
                else
                {
                    currentSession = null;
                    portGeneration = checked(portGeneration + 1);
                    completion = session.PumpCompletion;
                }
            }

            if (session is null)
            {
                // 后台循环可能刚清空活动会话、但仍在 finally 中释放资源；关闭方必须等待其完全收敛。
                await WaitForForegroundCleanupAsync(completion).ConfigureAwait(false);

                if (completion.IsCompleted)
                {
                    await completion.ConfigureAwait(false);
                }

                return;
            }

            session.RequestCancellation();
            session.ReceiveChannel.Writer.TryComplete();
            _ = session.StartPhysicalCleanup();
            SetCleanupPending(true);
            await WaitForForegroundCleanupAsync(completion).ConfigureAwait(false);

            if (completion.IsCompleted)
            {
                await completion.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 最多等待前台关闭预算；超时后让旧会话清理继续在后台收敛。
        /// </summary>
        /// <param name="completion">包含读取循环和物理资源释放的完整会话任务。</param>
        /// <returns>会话完成或前台等待预算耗尽后的任务。</returns>
        private static async Task WaitForForegroundCleanupAsync(Task completion)
        {
            ArgumentNullException.ThrowIfNull(completion);

            if (completion.IsCompleted)
            {
                await completion.ConfigureAwait(false);
                return;
            }

            Task delay = Task.Delay(ForegroundCloseTimeout);
            await Task.WhenAny(completion, delay).ConfigureAwait(false);
        }

        /// <summary>
        /// 原子更新后台清理状态，并隔离观察者异常以保护串口生命周期。
        /// </summary>
        /// <param name="isPending">存在后台物理清理时为真。</param>
        private void SetCleanupPending(bool isPending)
        {
            int newValue = isPending ? 1 : 0;
            int previous = Interlocked.Exchange(ref cleanupPendingFlag, newValue);

            if (previous == newValue || CleanupPendingChanged is null)
            {
                return;
            }

            foreach (Action<bool> observer in CleanupPendingChanged
                .GetInvocationList()
                .Cast<Action<bool>>())
            {
                try
                {
                    observer(isPending);
                }
                catch (Exception)
                {
                    // 观察者失败不得把已经失效的串口会话重新带回前台关闭路径。
                }
            }
        }

        /// <summary>
        /// 枚举指定会话的全部接收块，并保留通道故障完成语义。
        /// </summary>
        /// <param name="session">调用读取方法时捕获的独立会话。</param>
        /// <param name="cancellationToken">取消枚举等待的令牌。</param>
        /// <returns>指定会话的异步接收块序列。</returns>
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
        /// 根据不可变设置创建使用无握手方式的真实串口对象。
        /// </summary>
        /// <param name="settings">已经验证的串口参数。</param>
        /// <returns>尚未打开、等待进入会话生命周期的串口对象。</returns>
        private static SerialPort CreateConfiguredSerialPort(SerialLineSettings settings)
        {
            return new SerialPort(
                settings.PortName,
                settings.BaudRate,
                settings.Parity,
                settings.DataBits,
                settings.StopBits)
            {
                Handshake = Handshake.None,
            };
        }

        /// <summary>
        /// 在已持有状态门时拒绝永久释放后的进一步操作。
        /// </summary>
        /// <exception cref="ObjectDisposedException">对象已经永久释放时抛出。</exception>
        private void ThrowIfDisposedUnderLock()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
        }

        /// <summary>
        /// 保存一次串口打开会话独享的基础流、通道、取消源与后台任务。
        /// </summary>
        private sealed class Session
        {
            /// <summary>
            /// 保护物理资源后台清理任务的唯一创建。
            /// </summary>
            private readonly object physicalCleanupSyncRoot = new();

            /// <summary>
            /// 当前会话唯一的物理资源后台清理任务。
            /// </summary>
            private Task? physicalCleanupTask;

            /// <summary>
            /// 指示会话取消源是否已经由自然结束、故障或主动关闭路径释放。
            /// </summary>
            private int cancellationDisposed;

            /// <summary>
            /// 本会话持有的进程内端口租约，仅在物理清理完成后释放。
            /// </summary>
            private SerialPortLease? portLease;

            /// <summary>
            /// 初始化一项使用等待背压策略的独立串口会话。
            /// </summary>
            /// <param name="serialPort">已经打开的串口对象。</param>
            /// <param name="baseStream">该串口对象当前会话的基础流。</param>
            /// <param name="generation">本会话唯一端口代次。</param>
            /// <param name="receiveCapacity">接收块通道容量。</param>
            /// <param name="portLease">必须保持至底层物理句柄完全释放的端口占用租约。</param>
            internal Session(
                SerialPort serialPort,
                Stream baseStream,
                int generation,
                int receiveCapacity,
                SerialPortLease portLease)
            {
                ArgumentNullException.ThrowIfNull(portLease);
                SerialPort = serialPort;
                BaseStream = baseStream;
                Generation = generation;
                this.portLease = portLease;
                Cancellation = new CancellationTokenSource();
                ReceiveChannel = Channel.CreateBounded<SerialReceiveChunk>(
                    new BoundedChannelOptions(receiveCapacity)
                    {
                        AllowSynchronousContinuations = false,
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = false,
                        SingleWriter = true,
                    });
            }

            /// <summary>
            /// 获取当前会话独占的串口对象。
            /// </summary>
            internal SerialPort SerialPort { get; }

            /// <summary>
            /// 获取唯一读取循环和异步写入共同使用的基础流。
            /// </summary>
            internal Stream BaseStream { get; }

            /// <summary>
            /// 获取当前会话唯一端口代次。
            /// </summary>
            internal int Generation { get; }

            /// <summary>
            /// 获取关闭会话时取消读取和背压等待的令牌源。
            /// </summary>
            internal CancellationTokenSource Cancellation { get; }

            /// <summary>
            /// 获取当前会话独享的有界接收通道。
            /// </summary>
            internal Channel<SerialReceiveChunk> ReceiveChannel { get; }

            /// <summary>
            /// 获取唯一后台读取循环及其最终清理任务。
            /// </summary>
            internal Task PumpCompletion { get; private set; } = Task.CompletedTask;

            /// <summary>
            /// 记录唯一后台读取循环及其最终清理任务。
            /// </summary>
            /// <param name="completion">不得为空的会话完成任务。</param>
            internal void SetPumpCompletion(Task completion)
            {
                ArgumentNullException.ThrowIfNull(completion);
                PumpCompletion = completion;
            }

            /// <summary>
            /// 请求终止后台读取；若自然结束路径已先释放取消源，则按幂等关闭处理。
            /// </summary>
            internal void RequestCancellation()
            {
                try
                {
                    Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 自然结束或故障路径已经完成会话清理，无需再次取消。
                }
            }

            /// <summary>
            /// 幂等启动后台物理清理，使可能阻塞的驱动 Close/Dispose 不占用界面线程。
            /// </summary>
            /// <returns>当前会话唯一的物理清理任务。</returns>
            internal Task StartPhysicalCleanup()
            {
                lock (physicalCleanupSyncRoot)
                {
                    physicalCleanupTask ??= Task.Run(DisposePhysicalResourcesCore);
                    return physicalCleanupTask;
                }
            }

            /// <summary>
            /// 关闭基础流和串口对象，并隔离失效驱动在清理阶段抛出的异常。
            /// </summary>
            private void DisposePhysicalResourcesCore()
            {
                try
                {
                    BaseStream.Dispose();
                }
                catch (IOException)
                {
                    // 会话失效后的底层流清理失败不覆盖已经传播到接收通道的首个通信故障。
                }
                catch (ObjectDisposedException)
                {
                    // 另一条关闭路径已经释放基础流，幂等收敛即可。
                }
                catch (Exception)
                {
                    // 驱动清理异常不得阻止串口对象的后续关闭与释放尝试。
                }

                try
                {
                    SerialPort.Close();
                }
                catch (IOException)
                {
                    // 驱动关闭异常不能阻止后续 Dispose 尝试释放句柄。
                }
                catch (ObjectDisposedException)
                {
                    // 基础流释放已经连带释放串口对象，继续按幂等关闭收敛。
                }
                catch (InvalidOperationException)
                {
                    // 串口已经关闭或未保持有效状态，继续执行释放。
                }
                catch (Exception)
                {
                    // 非预期驱动关闭异常不得阻止最终 Dispose 尝试。
                }

                try
                {
                    SerialPort.Dispose();
                }
                catch (IOException)
                {
                    // 释放阶段的驱动 I/O 错误不得覆盖接收通道已经保存的首个故障。
                }
                catch (ObjectDisposedException)
                {
                    // 另一条路径已经释放串口对象。
                }
                catch (InvalidOperationException)
                {
                    // 串口状态已经失效，资源释放路径仍视为完成。
                }
                catch (Exception)
                {
                    // 物理句柄已经从活动会话移出，释放异常仅作为清理失败被隔离。
                }
            }

            /// <summary>
            /// 幂等释放会话取消源，使自然结束、故障和主动关闭路径都不泄漏资源。
            /// </summary>
            internal void DisposeCancellation()
            {
                if (Interlocked.Exchange(ref cancellationDisposed, 1) == 0)
                {
                    Cancellation.Dispose();
                }
            }

            /// <summary>
            /// 在底层流和串口对象的物理清理完成后幂等释放进程内端口租约。
            /// </summary>
            internal void DisposePortLease()
            {
                SerialPortLease? lease = Interlocked.Exchange(ref portLease, null);
                lease?.Dispose();
            }
        }
    }
}
