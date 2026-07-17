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
        /// 创建并配置串口对象的工厂；生产默认工厂使用完整 <see cref="SerialSettings"/>。
        /// </summary>
        private readonly Func<SerialSettings, SerialPort> serialPortFactory;

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
        /// 初始化使用系统时间源和真实串口工厂的生产传输。
        /// </summary>
        public SerialPortTransport()
            : this(TimeProvider.System, CreateConfiguredSerialPort)
        {
        }

        /// <summary>
        /// 初始化使用指定时间源和真实串口工厂的生产传输。
        /// </summary>
        /// <param name="timeProvider">为接收块提供 UTC 和单调时间戳的统一时间源。</param>
        public SerialPortTransport(TimeProvider timeProvider)
            : this(timeProvider, CreateConfiguredSerialPort)
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
            Func<SerialSettings, SerialPort> serialPortFactory)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentNullException.ThrowIfNull(serialPortFactory);

            this.timeProvider = timeProvider;
            this.serialPortFactory = serialPortFactory;
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
            SerialSettings settings,
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

                SerialPort serialPort = serialPortFactory(settings) ??
                    throw new InvalidOperationException("串口工厂返回了空对象。");
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
                        ReceiveChannelCapacity);

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
        /// <param name="cancellationToken">取消写门等待、物理写入或刷新。</param>
        /// <returns>全部字节已经交给基础流并刷新后完成的值任务。</returns>
        /// <exception cref="ArgumentException"><paramref name="frame"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">没有活动会话或会话在等待写门期间变化时抛出。</exception>
        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> frame,
            CancellationToken cancellationToken)
        {
            if (frame.IsEmpty)
            {
                throw new ArgumentException("发送帧不能为空。", nameof(frame));
            }

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

                session.DisposePhysicalResources();
                session.ReceiveChannel.Writer.TryComplete();
                session.DisposeCancellation();
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
                await completion.ConfigureAwait(false);
                return;
            }

            session.RequestCancellation();
            session.DisposePhysicalResources();
            await completion.ConfigureAwait(false);
            session.ReceiveChannel.Writer.TryComplete();
            session.DisposeCancellation();
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
        private static SerialPort CreateConfiguredSerialPort(SerialSettings settings)
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
            /// 指示物理资源是否已经由关闭方或后台循环释放。
            /// </summary>
            private int physicalResourcesDisposed;

            /// <summary>
            /// 指示会话取消源是否已经由自然结束、故障或主动关闭路径释放。
            /// </summary>
            private int cancellationDisposed;

            /// <summary>
            /// 初始化一项使用等待背压策略的独立串口会话。
            /// </summary>
            /// <param name="serialPort">已经打开的串口对象。</param>
            /// <param name="baseStream">该串口对象当前会话的基础流。</param>
            /// <param name="generation">本会话唯一端口代次。</param>
            /// <param name="receiveCapacity">接收块通道容量。</param>
            internal Session(
                SerialPort serialPort,
                Stream baseStream,
                int generation,
                int receiveCapacity)
            {
                SerialPort = serialPort;
                BaseStream = baseStream;
                Generation = generation;
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
            /// 幂等关闭并释放基础流与串口对象，以解除任何挂起读取。
            /// </summary>
            internal void DisposePhysicalResources()
            {
                if (Interlocked.Exchange(ref physicalResourcesDisposed, 1) != 0)
                {
                    return;
                }

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
        }
    }
}
