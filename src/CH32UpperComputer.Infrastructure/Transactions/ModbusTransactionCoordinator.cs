using System.Collections.ObjectModel;
using System.IO;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Framing;
using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 定义无队列事务协调器的连接与迟到响应隔离状态。
    /// </summary>
    public enum TransactionCoordinatorState
    {
        /// <summary>
        /// 没有可用串口接收会话。
        /// </summary>
        Disconnected = 0,

        /// <summary>
        /// 已连接且可以接受一项新事务。
        /// </summary>
        ConnectedIdle = 1,

        /// <summary>
        /// 当前事务正在写入完整请求帧。
        /// </summary>
        Sending = 2,

        /// <summary>
        /// 当前事务已经发送并等待响应候选。
        /// </summary>
        WaitingResponse = 3,

        /// <summary>
        /// 上一事务可能存在迟到响应，正在等待完整静默保护窗口。
        /// </summary>
        Resynchronizing = 4,

        /// <summary>
        /// 应用退出流程已经开始，不再接受事务。
        /// </summary>
        ApplicationStopping = 5,
    }

    /// <summary>
    /// 指定事务协调器有界诊断记录的分类。
    /// </summary>
    public enum TransactionDiagnosticKind
    {
        /// <summary>
        /// 新请求因 Busy、未连接、重同步或应用退出而被立即拒绝。
        /// </summary>
        Rejected = 0,

        /// <summary>
        /// 响应结构完整，但没有通过当前标准请求签名校验。
        /// </summary>
        MismatchedResponse = 1,

        /// <summary>
        /// 接收记录在没有活动事务时到达，或位于同批首个完成记录之后。
        /// </summary>
        Unsolicited = 2,

        /// <summary>
        /// 数据在响应截止后或迟到响应隔离窗口中到达。
        /// </summary>
        LateOrUnsolicited = 3,

        /// <summary>
        /// 终止事件未能赢得事务唯一原子完成门。
        /// </summary>
        LosingCompletion = 4,

        /// <summary>
        /// 接收循环、串口生命周期或观察者回调发生故障。
        /// </summary>
        TransportOrObserverFault = 5,

        /// <summary>
        /// 相同请求在静默隔离后仍具有 Modbus RTU 无事务编号造成的残余歧义。
        /// </summary>
        ResidualAmbiguity = 6,
    }

    /// <summary>
    /// 表示一项有界保存的不可变事务协调诊断。
    /// </summary>
    public sealed class TransactionDiagnostic
    {
        /// <summary>
        /// 当前诊断独占持有的线路字节副本。
        /// </summary>
        private readonly byte[] data;

        /// <summary>
        /// 初始化一项带事务、端口与接收来源信息的诊断。
        /// </summary>
        /// <param name="kind">诊断分类。</param>
        /// <param name="transactionId">关联事务编号；拒绝或空闲数据可为空。</param>
        /// <param name="portGeneration">关联端口代次。</param>
        /// <param name="receiveSequence">关联传输块或接收记录的末来源序号。</param>
        /// <param name="timestamp">诊断发生时的单调时间戳。</param>
        /// <param name="message">明确诊断说明。</param>
        /// <param name="data">可选原始线路字节。</param>
        internal TransactionDiagnostic(
            TransactionDiagnosticKind kind,
            long? transactionId,
            int portGeneration,
            long receiveSequence,
            long timestamp,
            string message,
            ReadOnlySpan<byte> data)
        {
            Kind = kind;
            TransactionId = transactionId;
            PortGeneration = portGeneration;
            ReceiveSequence = receiveSequence;
            Timestamp = timestamp;
            Message = message;
            this.data = data.ToArray();
        }

        /// <summary>
        /// 获取诊断分类。
        /// </summary>
        public TransactionDiagnosticKind Kind { get; }

        /// <summary>
        /// 获取关联事务编号；没有分配编号时为空。
        /// </summary>
        public long? TransactionId { get; }

        /// <summary>
        /// 获取关联端口代次。
        /// </summary>
        public int PortGeneration { get; }

        /// <summary>
        /// 获取关联接收来源序号。
        /// </summary>
        public long ReceiveSequence { get; }

        /// <summary>
        /// 获取诊断发生时的单调时间戳。
        /// </summary>
        public long Timestamp { get; }

        /// <summary>
        /// 获取明确诊断说明。
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 获取原始线路字节的防御性副本。
        /// </summary>
        public ReadOnlyMemory<byte> Data => (byte[])data.Clone();
    }

    /// <summary>
    /// 协调单一 Modbus RTU 活动事务、响应签名、原子终态和迟到响应隔离。
    /// </summary>
    public sealed class ModbusTransactionCoordinator : IAsyncDisposable
    {
        /// <summary>
        /// 协调器最多保存的最近诊断数量。
        /// </summary>
        private const int MaximumDiagnosticCount = 512;

        /// <summary>
        /// 保护连接状态、活动上下文、重同步计时器和诊断快照。
        /// </summary>
        private readonly object stateSyncRoot = new();

        /// <summary>
        /// 串行化接收循环启动、用户断开、强制断开和应用停止。
        /// </summary>
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);

        /// <summary>
        /// 提供打开状态、异步发送和唯一接收块序列的抽象串口传输。
        /// </summary>
        private readonly ISerialTransport transport;

        /// <summary>
        /// 提供所有事务截止、静默窗口、诊断和计时器的统一时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 有界保存的最近协调器诊断。
        /// </summary>
        private readonly List<TransactionDiagnostic> diagnostics = [];

        /// <summary>
        /// 原子活动门；零为空闲，一表示一项事务已被接受且尚未完成 owner 清理。
        /// </summary>
        private int activeTransactionFlag;

        /// <summary>
        /// 最近分配的事务编号；拒绝项不得递增该值。
        /// </summary>
        private long lastTransactionId;

        /// <summary>
        /// 当前接收会话最近观察到的传输块序号。
        /// </summary>
        private long lastReceiveSequence;

        /// <summary>
        /// 当前接收会话最近完成事务路由与诊断处理的传输块序号。
        /// </summary>
        private long lastProcessedReceiveSequence;

        /// <summary>
        /// 当前被接受事务上下文；空闲、重同步或断开时为空。
        /// </summary>
        private ActiveTransactionContext? activeContext;

        /// <summary>
        /// 当前连接及事务生命周期状态。
        /// </summary>
        private TransactionCoordinatorState coordinatorState = TransactionCoordinatorState.Disconnected;

        /// <summary>
        /// 当前接收循环绑定的端口代次。
        /// </summary>
        private int receiveLoopGeneration;

        /// <summary>
        /// 当前接收循环的取消源。
        /// </summary>
        private CancellationTokenSource? receiveLoopCancellation;

        /// <summary>
        /// 当前或最近一次接收循环任务；重开前必须完全退出。
        /// </summary>
        private Task receiveLoopCompletion = Task.CompletedTask;

        /// <summary>
        /// 重同步连续静默保护窗口计时器。
        /// </summary>
        private ITimer? resynchronizationGuardTimer;

        /// <summary>
        /// 重同步绝对总时长上限计时器。
        /// </summary>
        private ITimer? resynchronizationMaximumTimer;

        /// <summary>
        /// 当前重同步开始时的单调时间戳。
        /// </summary>
        private long resynchronizationStartedTimestamp;

        /// <summary>
        /// 重同步期间最近观察到任意字节的单调时间戳。
        /// </summary>
        private long lastResynchronizationByteTimestamp;

        /// <summary>
        /// 当前重同步要求的完整连续静默时长。
        /// </summary>
        private TimeSpan resynchronizationGuardDuration;

        /// <summary>
        /// 每次开始新重同步时递增的代次，用于拒绝已释放计时器的排队旧回调。
        /// </summary>
        private long resynchronizationGeneration;

        /// <summary>
        /// 最近一次触发迟到隔离的请求，用于保留完整签名和残余歧义提示。
        /// </summary>
        private TransactionRequest? lastTimedOutRequest;

        /// <summary>
        /// 最近一次触发迟到隔离的事务编号。
        /// </summary>
        private long? lastTimedOutTransactionId;

        /// <summary>
        /// 指示上一响应仍存在无法由 RTU 线上编号彻底消除的迟到歧义。
        /// </summary>
        private bool hasLateResponseRisk;

        /// <summary>
        /// 强制断开等由同步计时器启动的后台生命周期任务。
        /// </summary>
        private Task backgroundLifecycleCompletion = Task.CompletedTask;

        /// <summary>
        /// 指示应用停止流程已经开始。
        /// </summary>
        private bool isApplicationStopping;

        /// <summary>
        /// 指示协调器已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 初始化只依赖抽象串口传输和统一时间源的无队列事务协调器。
        /// </summary>
        /// <param name="transport">可替换为 Fake 的抽象串口传输。</param>
        /// <param name="timeProvider">生产使用系统时间、测试使用手动时间的统一时间源。</param>
        /// <exception cref="ArgumentNullException">任一依赖为空时抛出。</exception>
        public ModbusTransactionCoordinator(
            ISerialTransport transport,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.transport = transport;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 在事务活动门占用或释放后发布一次忙状态变化。
        /// </summary>
        public event Action<bool>? BusyChanged;

        /// <summary>
        /// 在连接、发送、等待、重同步或断开状态实际变化后发布新状态。
        /// </summary>
        public event Action<TransactionCoordinatorState>? StateChanged;

        /// <summary>
        /// 在事务 owner 完成唯一清理并释放活动门后发布一次最终结果。
        /// </summary>
        public event Action<TransactionOutcome>? TransactionCompleted;

        /// <summary>
        /// 在接收循环完成当前端口代次数据块的事务路由与诊断处理后发布该块序号。
        /// </summary>
        public event Action<long>? ReceiveSequenceProcessed;

        /// <summary>
        /// 获取当前连接与事务状态。
        /// </summary>
        public TransactionCoordinatorState State
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return coordinatorState;
                }
            }
        }

        /// <summary>
        /// 获取是否有一项已接受事务尚未完成 owner 清理。
        /// </summary>
        public bool IsTransactionBusy => Volatile.Read(ref activeTransactionFlag) != 0;

        /// <summary>
        /// 获取当前活动事务编号；没有活动上下文时为空。
        /// </summary>
        public long? ActiveTransactionId
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return activeContext?.TransactionId;
                }
            }
        }

        /// <summary>
        /// 获取已分配的最近事务编号；Busy 等拒绝不会改变该值。
        /// </summary>
        public long LastTransactionId => Interlocked.Read(ref lastTransactionId);

        /// <summary>
        /// 获取当前接收会话已完成事务路由与诊断处理的最后传输块序号；尚未处理数据时为零。
        /// </summary>
        public long LastProcessedReceiveSequence => Interlocked.Read(ref lastProcessedReceiveSequence);

        /// <summary>
        /// 获取上一响应是否仍具有协议层无法完全消除的迟到歧义。
        /// </summary>
        public bool HasLateResponseRisk
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return hasLateResponseRisk;
                }
            }
        }

        /// <summary>
        /// 获取最近一次进入迟到隔离的完整不可变请求。
        /// </summary>
        public TransactionRequest? LastTimedOutRequest
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return lastTimedOutRequest;
                }
            }
        }

        /// <summary>
        /// 获取最近一次进入迟到隔离的事务编号。
        /// </summary>
        public long? LastTimedOutTransactionId
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return lastTimedOutTransactionId;
                }
            }
        }

        /// <summary>
        /// 获取最近协调诊断的独立只读快照。
        /// </summary>
        public IReadOnlyList<TransactionDiagnostic> Diagnostics
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return new ReadOnlyCollection<TransactionDiagnostic>(diagnostics.ToArray());
                }
            }
        }

        /// <summary>
        /// 启动当前已打开端口的唯一接收消费循环；本方法绝不发送任何请求。
        /// </summary>
        /// <param name="cancellationToken">取消尚未开始或等待上一循环退出的启动操作。</param>
        /// <returns>接收循环已经启动且状态进入 ConnectedIdle 后完成的值任务。</returns>
        /// <exception cref="InvalidOperationException">抽象传输尚未打开时抛出。</exception>
        /// <exception cref="ObjectDisposedException">协调器已经永久释放时抛出。</exception>
        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                Task previousCompletion;

                lock (stateSyncRoot)
                {
                    ThrowIfDisposedUnderLock();

                    if (coordinatorState != TransactionCoordinatorState.Disconnected)
                    {
                        return;
                    }

                    previousCompletion = receiveLoopCompletion;
                }

                await previousCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (!transport.IsOpen)
                {
                    throw new InvalidOperationException("串口传输尚未打开，不能启动事务接收循环。");
                }

                CancellationTokenSource receiveCancellation = new();
                int generation = transport.PortGeneration;
                TransactionCoordinatorState? changedState;

                lock (stateSyncRoot)
                {
                    ThrowIfDisposedUnderLock();
                    if (isApplicationStopping)
                    {
                        throw new InvalidOperationException("应用停止后不能重新启动事务协调器。");
                    }

                    receiveLoopCancellation?.Dispose();
                    receiveLoopCancellation = receiveCancellation;
                    receiveLoopGeneration = generation;
                    Interlocked.Exchange(ref lastReceiveSequence, 0);
                    Interlocked.Exchange(ref lastProcessedReceiveSequence, 0);
                    hasLateResponseRisk = false;
                    lastTimedOutRequest = null;
                    lastTimedOutTransactionId = null;
                    changedState = SetStateUnderLock(TransactionCoordinatorState.ConnectedIdle);
                }

                PublishStateChanged(changedState);
                Task loop = RunReceiveLoopAsync(generation, receiveCancellation.Token);

                lock (stateSyncRoot)
                {
                    receiveLoopCompletion = loop;
                }
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 尝试立即占用唯一活动门、发送一帧并等待原子终态；Busy 时不排队也不分配编号。
        /// </summary>
        /// <param name="request">显式携带标准或原始模式以及超时的不可变请求。</param>
        /// <param name="cancellationToken">取消当前被接受事务的令牌。</param>
        /// <returns>接受事务的最终结果，或未分配 TransactionId 的立即拒绝结果。</returns>
        public ValueTask<TransactionExecutionResult> TryExecuteAsync(
            TransactionRequest request,
            CancellationToken cancellationToken)
        {
            return TryExecuteCoreAsync(request, cancellationToken, null);
        }

        /// <summary>
        /// 提交一项由定时计划控制的请求，并在实际写入前通过同一代次门做最后授权。
        /// </summary>
        /// <param name="request">定时计划当前代次需要发送的不可变请求。</param>
        /// <param name="tryStartWrite">
        /// 在调度代次锁内复核授权并同步启动写调用的原子入口；返回空值时不得写串口。
        /// </param>
        /// <returns>定时事务的最终结果，或无队列协调器产生的立即拒绝结果。</returns>
        internal ValueTask<TransactionExecutionResult> TryExecuteScheduledAsync(
            TransactionRequest request,
            Func<Func<ValueTask>, ValueTask?> tryStartWrite)
        {
            ArgumentNullException.ThrowIfNull(tryStartWrite);
            return TryExecuteCoreAsync(request, CancellationToken.None, tryStartWrite);
        }

        /// <summary>
        /// 实现手动与定时请求共用的唯一无队列事务生命周期。
        /// </summary>
        /// <param name="request">显式携带模式、完整帧和超时的不可变请求。</param>
        /// <param name="cancellationToken">手动事务取消令牌；定时事务使用空令牌。</param>
        /// <param name="tryStartWrite">
        /// 定时计划可选的授权并启动写入口；手动事务为空并直接启动写调用。
        /// </param>
        /// <returns>接受事务的最终结果，或未分配 TransactionId 的立即拒绝结果。</returns>
        private async ValueTask<TransactionExecutionResult> TryExecuteCoreAsync(
            TransactionRequest request,
            CancellationToken cancellationToken,
            Func<Func<ValueTask>, ValueTask?>? tryStartWrite)
        {
            ArgumentNullException.ThrowIfNull(request);
            TransactionRejected initialRejection = GetCurrentRejection();

            if (initialRejection != TransactionRejected.None)
            {
                return Reject(initialRejection);
            }

            if (Interlocked.CompareExchange(ref activeTransactionFlag, 1, 0) != 0)
            {
                return Reject(TransactionRejected.Busy);
            }

            ActiveTransactionContext? context = null;
            TransactionOutcome? finalOutcome = null;
            bool publishedBusy = false;

            try
            {
                TransactionRejected guardedRejection;
                TransactionCoordinatorState? sendingState = null;

                lock (stateSyncRoot)
                {
                    guardedRejection = GetCurrentRejectionUnderLock();

                    if (guardedRejection == TransactionRejected.None)
                    {
                        long transactionId = checked(Interlocked.Increment(ref lastTransactionId));
                        int portGeneration = transport.PortGeneration;
                        context = new ActiveTransactionContext(
                            transactionId,
                            request,
                            portGeneration,
                            timeProvider);
                        activeContext = context;
                        hasLateResponseRisk = false;
                        sendingState = SetStateUnderLock(TransactionCoordinatorState.Sending);
                    }
                }

                if (guardedRejection != TransactionRejected.None)
                {
                    return Reject(guardedRejection);
                }

                ActiveTransactionContext acceptedContext = context ??
                    throw new InvalidOperationException("事务活动门已接受请求，但没有创建事务上下文。");
                PublishStateChanged(sendingState);
                PublishBusyChanged(true);
                publishedBusy = true;
                acceptedContext.SetCancellationRegistration(
                    cancellationToken.Register(
                        static state =>
                        {
                            CancellationCallbackState callbackState = (CancellationCallbackState)state!;
                            callbackState.Coordinator.HandleCancellation(callbackState.Context);
                        },
                        new CancellationCallbackState(this, acceptedContext)));

                if (acceptedContext.Pending.CompletionState == TransactionCompletionState.Pending)
                {
                    await WriteAndArmTransactionAsync(
                        acceptedContext,
                        cancellationToken,
                        tryStartWrite).ConfigureAwait(false);
                }

                finalOutcome = await acceptedContext.Pending.Completion.ConfigureAwait(false);
            }
            finally
            {
                if (context is not null)
                {
                    context.Dispose();
                    TransactionCoordinatorState? changedState = null;

                    lock (stateSyncRoot)
                    {
                        if (ReferenceEquals(activeContext, context))
                        {
                            activeContext = null;
                        }

                        if (coordinatorState is TransactionCoordinatorState.Sending or
                            TransactionCoordinatorState.WaitingResponse)
                        {
                            changedState = SetStateUnderLock(
                                transport.IsOpen
                                    ? TransactionCoordinatorState.ConnectedIdle
                                    : TransactionCoordinatorState.Disconnected);
                        }
                    }

                    PublishStateChanged(changedState);
                }

                Interlocked.Exchange(ref activeTransactionFlag, 0);

                if (publishedBusy)
                {
                    PublishBusyChanged(false);
                }
            }

            PublishTransactionCompleted(finalOutcome!);
            return TransactionExecutionResult.CreateAccepted(finalOutcome!);
        }

        /// <summary>
        /// 用户主动断开当前串口会话，并以 Disconnected 竞争尚未完成事务。
        /// </summary>
        /// <param name="cancellationToken">取消尚未取得生命周期门的断开请求。</param>
        /// <returns>端口关闭且接收循环退出后完成的值任务。</returns>
        public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await StopConnectionUnderLifecycleGateAsync(
                    TransactionCompletionState.Disconnected,
                    TransactionCoordinatorState.Disconnected,
                    null).ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 启动应用退出清理，以 ApplicationStopping 竞争事务并关闭接收与物理端口。
        /// </summary>
        /// <param name="cancellationToken">取消尚未取得生命周期门的停止请求。</param>
        /// <returns>端口关闭且接收循环退出后完成的值任务。</returns>
        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                lock (stateSyncRoot)
                {
                    isApplicationStopping = true;
                }

                await StopConnectionUnderLifecycleGateAsync(
                    TransactionCompletionState.ApplicationStopping,
                    TransactionCoordinatorState.ApplicationStopping,
                    null).ConfigureAwait(false);
                ChangeState(TransactionCoordinatorState.Disconnected);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 等待由重同步上限计时器启动的强制断开后台任务。
        /// </summary>
        /// <param name="cancellationToken">取消等待但不取消强制断开本身的令牌。</param>
        /// <returns>最近后台生命周期任务完成后结束的值任务。</returns>
        public async ValueTask WaitForBackgroundOperationsAsync(CancellationToken cancellationToken)
        {
            Task completion;

            lock (stateSyncRoot)
            {
                completion = backgroundLifecycleCompletion;
            }

            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 幂等执行应用停止并永久拒绝后续使用。
        /// </summary>
        /// <returns>接收循环和物理端口均已收敛后完成的值任务。</returns>
        public async ValueTask DisposeAsync()
        {
            lock (stateSyncRoot)
            {
                if (isDisposed)
                {
                    return;
                }

                isDisposed = true;
            }

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// 异步写入请求，并在写入成功后记录发送边界、启动组帧器和响应总计时器。
        /// </summary>
        /// <param name="context">当前唯一活动事务上下文。</param>
        /// <param name="cancellationToken">取消物理写入的调用方令牌。</param>
        /// <param name="tryStartWrite">
        /// 定时计划可选的授权并启动写入口；手动事务为空并直接启动写调用。
        /// </param>
        /// <returns>写入和等待状态设置完成后的任务。</returns>
        private async Task WriteAndArmTransactionAsync(
            ActiveTransactionContext context,
            CancellationToken cancellationToken,
            Func<Func<ValueTask>, ValueTask?>? tryStartWrite)
        {
            if (context.Pending.CompletionState != TransactionCompletionState.Pending)
            {
                return;
            }

            try
            {
                ValueTask writeOperation;

                if (tryStartWrite is null)
                {
                    context.MarkWriteAttempted();
                    writeOperation = transport.WriteAsync(
                        context.Request.Frame,
                        cancellationToken);
                }
                else
                {
                    ValueTask? authorizedWriteOperation = tryStartWrite(
                        () =>
                        {
                            context.MarkWriteAttempted();
                            return transport.WriteAsync(
                                context.Request.Frame,
                                cancellationToken);
                        });

                    if (!authorizedWriteOperation.HasValue)
                    {
                        TryComplete(
                            context,
                            TransactionCompletionState.Cancelled,
                            "定时计划已在物理写入前停止，本次请求未发送。",
                            null,
                            null,
                            null);
                        return;
                    }

                    writeOperation = authorizedWriteOperation.Value;
                }

                await writeOperation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                HandleCancellation(context);
                return;
            }
            catch (ObjectDisposedException exception)
            {
                TryComplete(
                    context,
                    TransactionCompletionState.Disconnected,
                    "发送期间串口资源已经释放。",
                    null,
                    null,
                    exception);
                return;
            }
            catch (IOException exception)
            {
                TransactionCompletionState state =
                    !transport.IsOpen || transport.PortGeneration != context.PortGeneration
                        ? TransactionCompletionState.Disconnected
                        : TransactionCompletionState.WriteFailed;
                bool won = TryComplete(
                    context,
                    state,
                    state == TransactionCompletionState.Disconnected
                        ? "发送期间串口已经断开。"
                        : "完整请求帧写入失败。",
                    null,
                    null,
                    exception);

                if (won && state == TransactionCompletionState.WriteFailed)
                {
                    BeginResynchronization(context, "请求可能被部分写入，保守隔离潜在响应。");
                }

                return;
            }
            catch (Exception exception)
            {
                bool won = TryComplete(
                    context,
                    TransactionCompletionState.WriteFailed,
                    "完整请求帧写入发生未预期故障。",
                    null,
                    null,
                    exception);

                if (won)
                {
                    BeginResynchronization(context, "请求写入状态不确定，保守隔离潜在响应。");
                }

                return;
            }

            if (context.Pending.CompletionState != TransactionCompletionState.Pending)
            {
                return;
            }

            if (!transport.IsOpen || transport.PortGeneration != context.PortGeneration)
            {
                TryComplete(
                    context,
                    TransactionCompletionState.Disconnected,
                    "请求写入后端口代次已经失效。",
                    null,
                    null,
                    null);
                return;
            }

            try
            {
                long sentTimestamp = timeProvider.GetTimestamp();
                long sentAfterReceiveSequence = Interlocked.Read(ref lastReceiveSequence);
                long deadlineTimestamp = AddDurationToTimestamp(
                    sentTimestamp,
                    context.Request.ResponseTimeout);
                context.Framer.StartCapture(
                    context.Request.Mode == TransactionMode.Standard
                        ? RtuFramingMode.Standard
                        : RtuFramingMode.RawDebug,
                    context.PortGeneration,
                    sentTimestamp);
                context.Arm(sentAfterReceiveSequence, sentTimestamp, deadlineTimestamp);
                TransactionCoordinatorState? waitingState = null;
                bool lifecycleInvalid;

                lock (stateSyncRoot)
                {
                    lifecycleInvalid = !ReferenceEquals(activeContext, context) ||
                        context.Pending.CompletionState != TransactionCompletionState.Pending ||
                        coordinatorState != TransactionCoordinatorState.Sending ||
                        !transport.IsOpen ||
                        transport.PortGeneration != context.PortGeneration;

                    if (!lifecycleInvalid)
                    {
                        waitingState = SetStateUnderLock(TransactionCoordinatorState.WaitingResponse);
                    }
                }

                if (lifecycleInvalid)
                {
                    TryComplete(
                        context,
                        TransactionCompletionState.Disconnected,
                        "响应等待状态建立前串口生命周期已经失效。",
                        null,
                        null,
                        null);
                    return;
                }

                PublishStateChanged(waitingState);

                if (context.Pending.CompletionState == TransactionCompletionState.Pending)
                {
                    ITimer responseTimer = timeProvider.CreateTimer(
                        static state =>
                        {
                            TimerCallbackState callbackState = (TimerCallbackState)state!;
                            callbackState.Coordinator.HandleResponseTimeout(callbackState.Context);
                        },
                        new TimerCallbackState(this, context),
                        context.Request.ResponseTimeout,
                        Timeout.InfiniteTimeSpan);
                    context.SetResponseTimer(responseTimer);
                }
            }
            catch (Exception exception)
            {
                bool won = TryComplete(
                    context,
                    TransactionCompletionState.WriteFailed,
                    "请求已经写入，但响应等待边界初始化失败。",
                    null,
                    null,
                    exception);

                if (won)
                {
                    BeginResynchronization(context, "请求已写入但等待状态不确定，隔离潜在响应。");
                }
            }
        }

        /// <summary>
        /// 持续消费当前端口会话的唯一接收块序列。
        /// </summary>
        /// <param name="generation">本循环绑定的端口代次。</param>
        /// <param name="cancellationToken">用户断开、强制断开或应用退出令牌。</param>
        /// <returns>序列结束或取消后完成的任务。</returns>
        private async Task RunReceiveLoopAsync(
            int generation,
            CancellationToken cancellationToken)
        {
            Exception? failure = null;

            try
            {
                await foreach (SerialReceiveChunk chunk in transport
                    .ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    HandleReceiveChunk(generation, chunk);

                    if (chunk.PortGeneration == generation)
                    {
                        Interlocked.Exchange(ref lastProcessedReceiveSequence, chunk.ReceiveSequence);
                        InvokeObservers(
                            ReceiveSequenceProcessed,
                            chunk.ReceiveSequence,
                            "ReceiveSequenceProcessed 观察者发生异常。");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                HandleUnexpectedDisconnect(generation, failure);
            }
        }

        /// <summary>
        /// 将一个完整传输块路由到重同步隔离、活动组帧器或空闲诊断。
        /// </summary>
        /// <param name="loopGeneration">调用本方法的接收循环绑定代次。</param>
        /// <param name="chunk">不可变串口接收块。</param>
        private void HandleReceiveChunk(
            int loopGeneration,
            SerialReceiveChunk chunk)
        {
            if (chunk.PortGeneration != loopGeneration)
            {
                RecordDiagnostic(
                    TransactionDiagnosticKind.LateOrUnsolicited,
                    null,
                    chunk.PortGeneration,
                    chunk.ReceiveSequence,
                    chunk.MonotonicTimestamp,
                    "旧端口代次接收块已隔离。",
                    chunk.Data.Span);
                return;
            }

            Interlocked.Exchange(ref lastReceiveSequence, chunk.ReceiveSequence);
            ActiveTransactionContext? context;
            TransactionCoordinatorState state;

            lock (stateSyncRoot)
            {
                state = coordinatorState;
                context = activeContext;

                if (state == TransactionCoordinatorState.Resynchronizing)
                {
                    lastResynchronizationByteTimestamp = chunk.MonotonicTimestamp;
                    resynchronizationGuardTimer?.Change(
                        resynchronizationGuardDuration,
                        Timeout.InfiniteTimeSpan);
                }
            }

            if (state == TransactionCoordinatorState.Resynchronizing)
            {
                RecordDiagnostic(
                    TransactionDiagnosticKind.LateOrUnsolicited,
                    lastTimedOutTransactionId,
                    chunk.PortGeneration,
                    chunk.ReceiveSequence,
                    chunk.MonotonicTimestamp,
                    "迟到响应隔离窗口中的字节不会完成任何事务。",
                    chunk.Data.Span);
                return;
            }

            if (context is null || state != TransactionCoordinatorState.WaitingResponse)
            {
                RecordDiagnostic(
                    TransactionDiagnosticKind.Unsolicited,
                    context?.TransactionId,
                    chunk.PortGeneration,
                    chunk.ReceiveSequence,
                    chunk.MonotonicTimestamp,
                    "没有处于等待响应状态的事务，接收块仅记录为非请求响应。",
                    chunk.Data.Span);
                return;
            }

            if (!context.IsArmed ||
                chunk.PortGeneration != context.PortGeneration ||
                chunk.ReceiveSequence <= context.SentAfterReceiveSequence)
            {
                RecordDiagnostic(
                    TransactionDiagnosticKind.LateOrUnsolicited,
                    context.TransactionId,
                    chunk.PortGeneration,
                    chunk.ReceiveSequence,
                    chunk.MonotonicTimestamp,
                    "候选响应不满足发送后的端口代次或接收序号边界。",
                    chunk.Data.Span);
                return;
            }

            if (chunk.MonotonicTimestamp >= context.DeadlineTimestamp)
            {
                RecordDiagnostic(
                    TransactionDiagnosticKind.LateOrUnsolicited,
                    context.TransactionId,
                    chunk.PortGeneration,
                    chunk.ReceiveSequence,
                    chunk.MonotonicTimestamp,
                    "候选响应在严格截止时间或之后到达，不能完成当前事务。",
                    chunk.Data.Span);
                HandleResponseTimeout(context, chunk.MonotonicTimestamp);
                return;
            }

            IReadOnlyList<ReceivedFrame> frames = context.Framer.Append(chunk);
            ProcessReceivedFrames(context, frames, enforceDeadline: true);

            if (context.Request.Mode == TransactionMode.RawDebug &&
                context.Pending.CompletionState == TransactionCompletionState.Pending)
            {
                ITimer rawTimer = context.GetOrCreateRawInterByteTimer(
                    timeProvider,
                    static state =>
                    {
                        TimerCallbackState callbackState = (TimerCallbackState)state!;
                        callbackState.Coordinator.HandleRawInterByteSilence(callbackState.Context);
                    },
                    new TimerCallbackState(this, context));
                rawTimer.Change(
                    context.Request.RawInterByteTimeout,
                    Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// 按事务模式处理同一追加操作形成的零至多项独立接收记录。
        /// </summary>
        /// <param name="context">当前活动事务上下文。</param>
        /// <param name="frames">按线路顺序形成的接收记录。</param>
        /// <param name="enforceDeadline">是否要求记录完成时间严格早于事务截止。</param>
        private void ProcessReceivedFrames(
            ActiveTransactionContext context,
            IReadOnlyList<ReceivedFrame> frames,
            bool enforceDeadline)
        {
            foreach (ReceivedFrame frame in frames)
            {
                if (context.Pending.CompletionState != TransactionCompletionState.Pending)
                {
                    RecordFrameDiagnostic(
                        TransactionDiagnosticKind.Unsolicited,
                        context,
                        frame,
                        "同批首个完成记录之后的接收记录仅作为非请求响应保存。");
                    continue;
                }

                if (enforceDeadline && frame.CompletedTimestamp >= context.DeadlineTimestamp)
                {
                    RecordFrameDiagnostic(
                        TransactionDiagnosticKind.LateOrUnsolicited,
                        context,
                        frame,
                        "接收记录在严格事务截止时间或之后完成。");
                    continue;
                }

                if (frame.PortGeneration != context.PortGeneration ||
                    frame.LastReceiveSequence <= context.SentAfterReceiveSequence)
                {
                    RecordFrameDiagnostic(
                        TransactionDiagnosticKind.LateOrUnsolicited,
                        context,
                        frame,
                        "接收记录不属于当前发送后的端口会话范围。");
                    continue;
                }

                if (context.Request.Mode == TransactionMode.RawDebug)
                {
                    if (frame.Data.IsEmpty)
                    {
                        RecordFrameDiagnostic(
                            TransactionDiagnosticKind.MismatchedResponse,
                            context,
                            frame,
                            "原始调试记录没有字节，不能形成 RawCaptured。");
                        continue;
                    }

                    bool won = TryComplete(
                        context,
                        TransactionCompletionState.RawCaptured,
                        frame.Kind == ReceivedFrameKind.ReceiveOverflow
                            ? "原始捕获达到有界上限，已保留截断证据。"
                            : "原始调试响应捕获完成。",
                        null,
                        frame,
                        null,
                        frame.CompletedTimestamp);

                    if (won && frame.Kind == ReceivedFrameKind.ReceiveOverflow)
                    {
                        BeginResynchronization(context, "原始捕获溢出后隔离迟到字节。");
                    }

                    continue;
                }

                if (frame.Kind == ReceivedFrameKind.ReceiveOverflow)
                {
                    bool won = TryComplete(
                        context,
                        TransactionCompletionState.ReceiveOverflow,
                        "标准接收缓存达到有界上限。",
                        null,
                        frame,
                        null,
                        frame.CompletedTimestamp);

                    if (won)
                    {
                        BeginResynchronization(context, "标准接收溢出后隔离迟到字节。");
                    }

                    continue;
                }

                if (frame.Kind != ReceivedFrameKind.StandardFrame)
                {
                    RecordFrameDiagnostic(
                        TransactionDiagnosticKind.MismatchedResponse,
                        context,
                        frame,
                        "标准事务只接受结构化标准帧候选。");
                    continue;
                }

                ModbusResponse response = ModbusResponseParser.Parse(
                    context.Request.StandardRequest!,
                    frame.Data.Span);

                if (response.Status == ModbusResponseStatus.ProtocolError)
                {
                    RecordFrameDiagnostic(
                        TransactionDiagnosticKind.MismatchedResponse,
                        context,
                        frame,
                        response.ErrorMessage ?? "响应未通过当前请求签名校验。");
                    continue;
                }

                TransactionCompletionState completionState = response.Status == ModbusResponseStatus.Succeeded
                    ? TransactionCompletionState.Succeeded
                    : TransactionCompletionState.ModbusException;
                TryComplete(
                    context,
                    completionState,
                    completionState == TransactionCompletionState.Succeeded
                        ? "标准响应完整匹配当前请求。"
                        : response.ExceptionMeaning ?? "设备返回标准 Modbus 异常。",
                    response,
                    frame,
                    null,
                    frame.CompletedTimestamp);
            }
        }

        /// <summary>
        /// 原始字节间静默计时器到期时观察组帧器，并完成非空原始捕获。
        /// </summary>
        /// <param name="context">计时器所属事务上下文。</param>
        private void HandleRawInterByteSilence(ActiveTransactionContext context)
        {
            if (!IsCurrentPendingContext(context))
            {
                return;
            }

            IReadOnlyList<ReceivedFrame> frames = context.Framer.ObserveTime(timeProvider.GetTimestamp());
            ProcessReceivedFrames(context, frames, enforceDeadline: true);
        }

        /// <summary>
        /// 响应总计时器或截止后候选触发时完成 RawCaptured 或 TimedOut，并立即进入重同步。
        /// </summary>
        /// <param name="context">截止事件所属事务上下文。</param>
        private void HandleResponseTimeout(ActiveTransactionContext context)
        {
            HandleResponseTimeout(context, timeProvider.GetTimestamp());
        }

        /// <summary>
        /// 使用明确事件时间处理响应截止。
        /// </summary>
        /// <param name="context">截止事件所属事务上下文。</param>
        /// <param name="eventTimestamp">不早于发送截止的单调事件时间戳。</param>
        private void HandleResponseTimeout(
            ActiveTransactionContext context,
            long eventTimestamp)
        {
            if (!IsCurrentPendingContext(context) || !context.IsArmed)
            {
                return;
            }

            long completionTimestamp = Math.Max(eventTimestamp, context.DeadlineTimestamp);
            IReadOnlyList<ReceivedFrame> frames = context.Framer.ObserveTime(completionTimestamp);
            bool completedFromRaw = false;

            if (context.Request.Mode == TransactionMode.RawDebug)
            {
                ReceivedFrame? rawFrame = frames.FirstOrDefault(frame => !frame.Data.IsEmpty);

                if (rawFrame is not null)
                {
                    completedFromRaw = TryComplete(
                        context,
                        TransactionCompletionState.RawCaptured,
                        "原始调试捕获在响应总截止时间结束。",
                        null,
                        rawFrame,
                        null,
                        completionTimestamp);

                    foreach (ReceivedFrame extraFrame in frames.Skip(1))
                    {
                        RecordFrameDiagnostic(
                            TransactionDiagnosticKind.Unsolicited,
                            context,
                            extraFrame,
                            "总截止时首个原始捕获之后的记录仅用于日志。");
                    }
                }
            }

            bool won = completedFromRaw || TryComplete(
                context,
                TransactionCompletionState.TimedOut,
                "响应总超时，未收到可完成事务的响应。",
                null,
                null,
                null,
                completionTimestamp);

            if (won)
            {
                BeginResynchronization(context, "响应总截止后隔离可能迟到的响应。");
            }
        }

        /// <summary>
        /// 调用方取消时以 Cancelled 竞争当前事务原子门。
        /// </summary>
        /// <param name="context">取消令牌注册所属事务上下文。</param>
        private void HandleCancellation(ActiveTransactionContext context)
        {
            bool won = TryComplete(
                context,
                TransactionCompletionState.Cancelled,
                "事务已由调用方取消。",
                null,
                null,
                null);

            if (won &&
                context.WasWriteAttempted &&
                transport.IsOpen &&
                transport.PortGeneration == context.PortGeneration)
            {
                BeginResynchronization(context, "已开始写入的事务被取消，隔离潜在迟到响应。");
            }
        }

        /// <summary>
        /// 接收循环意外结束时更新全局连接状态，并以 Disconnected 竞争活动事务。
        /// </summary>
        /// <param name="generation">发生结束的接收循环端口代次。</param>
        /// <param name="failure">非正常结束的底层异常；自然 EOF 时为空。</param>
        private void HandleUnexpectedDisconnect(
            int generation,
            Exception? failure)
        {
            ActiveTransactionContext? context;

            lock (stateSyncRoot)
            {
                if (generation != receiveLoopGeneration || isApplicationStopping)
                {
                    return;
                }

                context = activeContext;
                DisposeResynchronizationTimersUnderLock();
            }

            ChangeState(TransactionCoordinatorState.Disconnected);

            if (context is not null)
            {
                TryComplete(
                    context,
                    TransactionCompletionState.Disconnected,
                    failure is null ? "串口接收序列已经结束。" : "串口接收循环发生故障。",
                    null,
                    null,
                    failure);
            }

            if (failure is not null)
            {
                RecordDiagnostic(
                    TransactionDiagnosticKind.TransportOrObserverFault,
                    context?.TransactionId,
                    generation,
                    0,
                    timeProvider.GetTimestamp(),
                    failure.Message,
                    ReadOnlySpan<byte>.Empty);
            }
        }

        /// <summary>
        /// 在超时或溢出胜出后启动连续静默保护和绝对重同步上限计时器。
        /// </summary>
        /// <param name="context">触发隔离的已完成事务上下文。</param>
        /// <param name="reason">进入隔离的明确原因。</param>
        private void BeginResynchronization(
            ActiveTransactionContext context,
            string reason)
        {
            if (!context.TryMarkResynchronizationStarted())
            {
                return;
            }

            if (!transport.IsOpen || transport.PortGeneration != context.PortGeneration)
            {
                ChangeState(TransactionCoordinatorState.Disconnected);
                return;
            }

            TimeSpan guard = context.Request.ResponseTimeout > TimeSpan.FromMilliseconds(100)
                ? context.Request.ResponseTimeout
                : TimeSpan.FromMilliseconds(100);
            TimeSpan maximum = TimeSpan.FromTicks(checked(guard.Ticks * 2));
            long nowTimestamp = timeProvider.GetTimestamp();
            long resynchronizationEpoch;
            TransactionCoordinatorState? changedState;

            lock (stateSyncRoot)
            {
                if (coordinatorState is TransactionCoordinatorState.Disconnected or
                    TransactionCoordinatorState.ApplicationStopping)
                {
                    return;
                }

                DisposeResynchronizationTimersUnderLock();
                hasLateResponseRisk = true;
                lastTimedOutRequest = context.Request;
                lastTimedOutTransactionId = context.TransactionId;
                resynchronizationGuardDuration = guard;
                resynchronizationStartedTimestamp = nowTimestamp;
                lastResynchronizationByteTimestamp = nowTimestamp;
                resynchronizationEpoch = checked(++resynchronizationGeneration);
                changedState = SetStateUnderLock(TransactionCoordinatorState.Resynchronizing);
            }

            PublishStateChanged(changedState);
            RecordDiagnostic(
                TransactionDiagnosticKind.ResidualAmbiguity,
                context.TransactionId,
                context.PortGeneration,
                context.SentAfterReceiveSequence,
                nowTimestamp,
                $"{reason} 请求签名：{context.Request.CreateSignatureSummary()}。",
                ReadOnlySpan<byte>.Empty);

            ITimer guardTimer = timeProvider.CreateTimer(
                static state =>
                {
                    ResynchronizationTimerState callbackState = (ResynchronizationTimerState)state!;
                    callbackState.Coordinator.HandleResynchronizationGuard(callbackState.Generation);
                },
                new ResynchronizationTimerState(this, resynchronizationEpoch),
                guard,
                Timeout.InfiniteTimeSpan);
            ITimer maximumTimer = timeProvider.CreateTimer(
                static state =>
                {
                    ResynchronizationTimerState callbackState = (ResynchronizationTimerState)state!;
                    callbackState.Coordinator.HandleResynchronizationMaximum(callbackState.Generation);
                },
                new ResynchronizationTimerState(this, resynchronizationEpoch),
                maximum,
                Timeout.InfiniteTimeSpan);

            lock (stateSyncRoot)
            {
                if (coordinatorState == TransactionCoordinatorState.Resynchronizing &&
                    resynchronizationStartedTimestamp == nowTimestamp &&
                    resynchronizationGeneration == resynchronizationEpoch)
                {
                    resynchronizationGuardTimer = guardTimer;
                    resynchronizationMaximumTimer = maximumTimer;
                }
                else
                {
                    guardTimer.Dispose();
                    maximumTimer.Dispose();
                }
            }
        }

        /// <summary>
        /// 连续静默保护计时器到期时恢复 ConnectedIdle，或按剩余时长重新安排。
        /// </summary>
        /// <param name="generation">创建本回调的重同步代次。</param>
        private void HandleResynchronizationGuard(long generation)
        {
            TransactionCoordinatorState? changedState = null;
            TimeSpan? remaining = null;
            bool maximumReached = false;

            lock (stateSyncRoot)
            {
                if (coordinatorState != TransactionCoordinatorState.Resynchronizing ||
                    resynchronizationGeneration != generation)
                {
                    return;
                }

                long nowTimestamp = timeProvider.GetTimestamp();
                TimeSpan totalElapsed = timeProvider.GetElapsedTime(
                    resynchronizationStartedTimestamp,
                    nowTimestamp);
                TimeSpan maximumDuration = TimeSpan.FromTicks(
                    checked(resynchronizationGuardDuration.Ticks * 2));

                if (totalElapsed >= maximumDuration)
                {
                    maximumReached = true;
                }

                TimeSpan elapsed = timeProvider.GetElapsedTime(
                    lastResynchronizationByteTimestamp,
                    nowTimestamp);

                if (!maximumReached && elapsed >= resynchronizationGuardDuration)
                {
                    DisposeResynchronizationTimersUnderLock();
                    changedState = SetStateUnderLock(TransactionCoordinatorState.ConnectedIdle);
                }
                else if (!maximumReached)
                {
                    remaining = resynchronizationGuardDuration - elapsed;
                }
            }

            if (maximumReached)
            {
                HandleResynchronizationMaximum(generation);
                return;
            }

            if (remaining.HasValue)
            {
                lock (stateSyncRoot)
                {
                    if (coordinatorState == TransactionCoordinatorState.Resynchronizing &&
                        resynchronizationGeneration == generation)
                    {
                        resynchronizationGuardTimer?.Change(
                            remaining.Value,
                            Timeout.InfiniteTimeSpan);
                    }
                }
            }

            PublishStateChanged(changedState);
        }

        /// <summary>
        /// 重同步超过两倍保护窗口仍未静默时切换断开并异步关闭物理端口。
        /// </summary>
        /// <param name="generation">创建本回调的重同步代次。</param>
        private void HandleResynchronizationMaximum(long generation)
        {
            TransactionCoordinatorState? changedState;

            lock (stateSyncRoot)
            {
                if (coordinatorState != TransactionCoordinatorState.Resynchronizing ||
                    resynchronizationGeneration != generation)
                {
                    return;
                }

                DisposeResynchronizationTimersUnderLock();
                changedState = SetStateUnderLock(TransactionCoordinatorState.Disconnected);
            }

            PublishStateChanged(changedState);
            RecordDiagnostic(
                TransactionDiagnosticKind.TransportOrObserverFault,
                lastTimedOutTransactionId,
                transport.PortGeneration,
                0,
                timeProvider.GetTimestamp(),
                "重同步在两倍保护窗口内未获得连续静默，已强制断开串口。",
                ReadOnlySpan<byte>.Empty);
            Task forceDisconnect = ForceDisconnectAfterResynchronizationLimitAsync();

            lock (stateSyncRoot)
            {
                backgroundLifecycleCompletion = forceDisconnect;
            }
        }

        /// <summary>
        /// 完成由同步计时器触发的物理端口强制断开，并观察全部异常。
        /// </summary>
        /// <returns>接收循环与串口均关闭后完成的任务。</returns>
        private async Task ForceDisconnectAfterResynchronizationLimitAsync()
        {
            try
            {
                await lifecycleGate.WaitAsync().ConfigureAwait(false);

                try
                {
                    await StopConnectionUnderLifecycleGateAsync(
                        TransactionCompletionState.Disconnected,
                        TransactionCoordinatorState.Disconnected,
                        null).ConfigureAwait(false);
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }
            catch (Exception exception)
            {
                RecordDiagnostic(
                    TransactionDiagnosticKind.TransportOrObserverFault,
                    lastTimedOutTransactionId,
                    transport.PortGeneration,
                    0,
                    timeProvider.GetTimestamp(),
                    $"强制断开串口失败：{exception.Message}",
                    ReadOnlySpan<byte>.Empty);
            }
        }

        /// <summary>
        /// 在已持有生命周期门时竞争活动事务、取消接收循环并关闭物理端口。
        /// </summary>
        /// <param name="transactionState">连接关闭事件尝试设置的事务终态。</param>
        /// <param name="targetState">关闭开始时发布的协调器状态。</param>
        /// <param name="failure">触发关闭的可选原始异常。</param>
        /// <returns>接收循环完全退出后的任务。</returns>
        private async Task StopConnectionUnderLifecycleGateAsync(
            TransactionCompletionState transactionState,
            TransactionCoordinatorState targetState,
            Exception? failure)
        {
            ActiveTransactionContext? context;
            CancellationTokenSource? receiveCancellation;
            Task receiveCompletion;

            lock (stateSyncRoot)
            {
                context = activeContext;
                receiveCancellation = receiveLoopCancellation;
                receiveCompletion = receiveLoopCompletion;
                DisposeResynchronizationTimersUnderLock();
            }

            ChangeState(targetState);

            if (context is not null)
            {
                TryComplete(
                    context,
                    transactionState,
                    transactionState == TransactionCompletionState.ApplicationStopping
                        ? "应用退出正在终止事务。"
                        : "串口连接正在关闭。",
                    null,
                    null,
                    failure);
            }

            receiveCancellation?.Cancel();

            if (transport.IsOpen)
            {
                await transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                await receiveCompletion.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 关闭路径已经明确取消接收循环，等待任务只负责收敛。
            }

            receiveCancellation?.Dispose();

            lock (stateSyncRoot)
            {
                if (ReferenceEquals(receiveLoopCancellation, receiveCancellation))
                {
                    receiveLoopCancellation = null;
                }
            }
        }

        /// <summary>
        /// 让指定事件通过活动事务的唯一 PendingTransaction 原子门竞争终态。
        /// </summary>
        /// <param name="context">事件关联的事务上下文。</param>
        /// <param name="state">尝试设置的终态。</param>
        /// <param name="message">结果说明。</param>
        /// <param name="response">可选结构化响应。</param>
        /// <param name="receivedFrame">可选接收记录。</param>
        /// <param name="exception">可选底层异常。</param>
        /// <param name="completedTimestamp">可选明确完成时间；未提供时读取统一时间源。</param>
        /// <returns>本事件赢得原子门时返回 <see langword="true"/>。</returns>
        private bool TryComplete(
            ActiveTransactionContext context,
            TransactionCompletionState state,
            string message,
            ModbusResponse? response,
            ReceivedFrame? receivedFrame,
            Exception? exception,
            long? completedTimestamp = null)
        {
            TransactionOutcome outcome = new(
                context.TransactionId,
                state,
                context.Request.Mode,
                completedTimestamp ?? timeProvider.GetTimestamp(),
                message,
                response,
                receivedFrame,
                exception);
            bool won = context.Pending.TryComplete(state, outcome);

            if (!won)
            {
                ReadOnlyMemory<byte> evidence = receivedFrame?.Data ?? ReadOnlyMemory<byte>.Empty;
                RecordDiagnostic(
                    TransactionDiagnosticKind.LosingCompletion,
                    context.TransactionId,
                    context.PortGeneration,
                    receivedFrame?.LastReceiveSequence ?? 0,
                    completedTimestamp ?? timeProvider.GetTimestamp(),
                    $"候选终态 {state} 未赢得原子门，胜出状态为 {context.Pending.CompletionState}。",
                    evidence.Span);
            }

            return won;
        }

        /// <summary>
        /// 判断上下文仍是当前且尚未完成。
        /// </summary>
        /// <param name="context">待核对事务上下文。</param>
        /// <returns>上下文仍活动且原子状态为 Pending 时返回 <see langword="true"/>。</returns>
        private bool IsCurrentPendingContext(ActiveTransactionContext context)
        {
            lock (stateSyncRoot)
            {
                return ReferenceEquals(activeContext, context) &&
                    context.Pending.CompletionState == TransactionCompletionState.Pending;
            }
        }

        /// <summary>
        /// 获取当前状态对新请求的立即拒绝原因。
        /// </summary>
        /// <returns>None 表示可继续竞争活动门。</returns>
        private TransactionRejected GetCurrentRejection()
        {
            lock (stateSyncRoot)
            {
                return GetCurrentRejectionUnderLock();
            }
        }

        /// <summary>
        /// 在已持有状态门时获取新请求拒绝原因。
        /// </summary>
        /// <returns>None、Busy 以外的连接状态拒绝，或允许继续。</returns>
        private TransactionRejected GetCurrentRejectionUnderLock()
        {
            if (isApplicationStopping || coordinatorState == TransactionCoordinatorState.ApplicationStopping)
            {
                return TransactionRejected.ApplicationStopping;
            }

            if (coordinatorState == TransactionCoordinatorState.Resynchronizing)
            {
                return TransactionRejected.Resynchronizing;
            }

            if (coordinatorState == TransactionCoordinatorState.Disconnected || !transport.IsOpen)
            {
                return TransactionRejected.NotConnected;
            }

            return TransactionRejected.None;
        }

        /// <summary>
        /// 创建并记录一项不分配事务编号的立即拒绝结果。
        /// </summary>
        /// <param name="rejection">拒绝原因。</param>
        /// <returns>无 TransactionId 的拒绝提交结果。</returns>
        private TransactionExecutionResult Reject(TransactionRejected rejection)
        {
            string message = rejection switch
            {
                TransactionRejected.Busy => "当前已有请求等待处理，请完成或取消后再试。",
                TransactionRejected.NotConnected => "串口尚未连接或接收循环未启动。",
                TransactionRejected.Resynchronizing => "上一响应可能迟到，正在等待完整静默窗口。",
                TransactionRejected.ApplicationStopping => "应用正在退出，不能再发送请求。",
                _ => throw new ArgumentOutOfRangeException(nameof(rejection)),
            };
            RecordDiagnostic(
                TransactionDiagnosticKind.Rejected,
                null,
                transport.PortGeneration,
                0,
                timeProvider.GetTimestamp(),
                message,
                ReadOnlySpan<byte>.Empty);
            return TransactionExecutionResult.CreateRejected(rejection, message);
        }

        /// <summary>
        /// 按时间源频率把相对时长向上取整为绝对单调截止时间戳。
        /// </summary>
        /// <param name="startedTimestamp">非负起始时间戳。</param>
        /// <param name="duration">需要增加的正时长。</param>
        /// <returns>不会早于理论截止时刻的绝对时间戳。</returns>
        private long AddDurationToTimestamp(
            long startedTimestamp,
            TimeSpan duration)
        {
            decimal timestampUnits = decimal.Ceiling(
                duration.Ticks * (decimal)timeProvider.TimestampFrequency /
                TimeSpan.TicksPerSecond);
            return checked(startedTimestamp + decimal.ToInt64(timestampUnits));
        }

        /// <summary>
        /// 记录一项以接收记录为来源的诊断。
        /// </summary>
        /// <param name="kind">诊断分类。</param>
        /// <param name="context">关联事务上下文。</param>
        /// <param name="frame">关联接收记录。</param>
        /// <param name="message">明确诊断说明。</param>
        private void RecordFrameDiagnostic(
            TransactionDiagnosticKind kind,
            ActiveTransactionContext context,
            ReceivedFrame frame,
            string message)
        {
            RecordDiagnostic(
                kind,
                context.TransactionId,
                frame.PortGeneration,
                frame.LastReceiveSequence,
                frame.CompletedTimestamp,
                message,
                frame.Data.Span);
        }

        /// <summary>
        /// 有界保存一项事务协调诊断。
        /// </summary>
        /// <param name="kind">诊断分类。</param>
        /// <param name="transactionId">可选关联事务编号。</param>
        /// <param name="portGeneration">关联端口代次。</param>
        /// <param name="receiveSequence">关联接收序号。</param>
        /// <param name="timestamp">诊断单调时间戳。</param>
        /// <param name="message">明确诊断说明。</param>
        /// <param name="data">可选原始线路字节。</param>
        private void RecordDiagnostic(
            TransactionDiagnosticKind kind,
            long? transactionId,
            int portGeneration,
            long receiveSequence,
            long timestamp,
            string message,
            ReadOnlySpan<byte> data)
        {
            TransactionDiagnostic diagnostic = new(
                kind,
                transactionId,
                portGeneration,
                receiveSequence,
                timestamp,
                message,
                data);

            lock (stateSyncRoot)
            {
                if (diagnostics.Count == MaximumDiagnosticCount)
                {
                    diagnostics.RemoveAt(0);
                }

                diagnostics.Add(diagnostic);
            }
        }

        /// <summary>
        /// 线程安全切换协调器状态并发布实际变化。
        /// </summary>
        /// <param name="newState">目标状态。</param>
        private void ChangeState(TransactionCoordinatorState newState)
        {
            TransactionCoordinatorState? changedState;

            lock (stateSyncRoot)
            {
                changedState = SetStateUnderLock(newState);
            }

            PublishStateChanged(changedState);
        }

        /// <summary>
        /// 在已持有状态门时更新协调器状态。
        /// </summary>
        /// <param name="newState">目标状态。</param>
        /// <returns>状态实际变化时返回新状态，否则为空。</returns>
        private TransactionCoordinatorState? SetStateUnderLock(TransactionCoordinatorState newState)
        {
            if (coordinatorState == newState)
            {
                return null;
            }

            coordinatorState = newState;
            return newState;
        }

        /// <summary>
        /// 在已持有状态门时幂等释放重同步计时器。
        /// </summary>
        private void DisposeResynchronizationTimersUnderLock()
        {
            resynchronizationGuardTimer?.Dispose();
            resynchronizationMaximumTimer?.Dispose();
            resynchronizationGuardTimer = null;
            resynchronizationMaximumTimer = null;
        }

        /// <summary>
        /// 安全发布忙状态变化，观察者故障仅写入诊断而不破坏事务 owner。
        /// </summary>
        /// <param name="isBusy">新的忙状态。</param>
        private void PublishBusyChanged(bool isBusy)
        {
            InvokeObservers(
                BusyChanged,
                isBusy,
                "BusyChanged 观察者发生异常。");
        }

        /// <summary>
        /// 安全发布实际发生的协调器状态变化。
        /// </summary>
        /// <param name="changedState">实际新状态；为空时不发布。</param>
        private void PublishStateChanged(TransactionCoordinatorState? changedState)
        {
            if (changedState.HasValue)
            {
                InvokeObservers(
                    StateChanged,
                    changedState.Value,
                    "StateChanged 观察者发生异常。");
            }
        }

        /// <summary>
        /// 安全发布 owner 清理后的唯一最终事务结果。
        /// </summary>
        /// <param name="outcome">唯一最终事务结果。</param>
        private void PublishTransactionCompleted(TransactionOutcome outcome)
        {
            InvokeObservers(
                TransactionCompleted,
                outcome,
                "TransactionCompleted 观察者发生异常。");
        }

        /// <summary>
        /// 逐个调用观察者，隔离单个 UI 订阅者异常。
        /// </summary>
        /// <typeparam name="T">观察者接收的不可变值类型。</typeparam>
        /// <param name="observers">可为空的多播观察者。</param>
        /// <param name="value">传递给观察者的值。</param>
        /// <param name="failureMessage">观察者异常的诊断前缀。</param>
        private void InvokeObservers<T>(
            Action<T>? observers,
            T value,
            string failureMessage)
        {
            if (observers is null)
            {
                return;
            }

            foreach (Action<T> observer in observers.GetInvocationList().Cast<Action<T>>())
            {
                try
                {
                    observer(value);
                }
                catch (Exception exception)
                {
                    RecordDiagnostic(
                        TransactionDiagnosticKind.TransportOrObserverFault,
                        ActiveTransactionId,
                        transport.PortGeneration,
                        0,
                        timeProvider.GetTimestamp(),
                        $"{failureMessage} {exception.Message}",
                        ReadOnlySpan<byte>.Empty);
                }
            }
        }

        /// <summary>
        /// 在已持有状态门时拒绝永久释放后的进一步启动。
        /// </summary>
        /// <exception cref="ObjectDisposedException">协调器已永久释放时抛出。</exception>
        private void ThrowIfDisposedUnderLock()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
        }

        /// <summary>
        /// 保存一次已接受事务独享的完成门、组帧器、发送边界和计时器。
        /// </summary>
        private sealed class ActiveTransactionContext : IDisposable
        {
            /// <summary>
            /// 保护计时器和取消注册的创建、替换与释放。
            /// </summary>
            private readonly object resourceSyncRoot = new();

            /// <summary>
            /// 响应总截止计时器。
            /// </summary>
            private ITimer? responseTimer;

            /// <summary>
            /// 原始调试字节间静默计时器。
            /// </summary>
            private ITimer? rawInterByteTimer;

            /// <summary>
            /// 调用方取消令牌注册。
            /// </summary>
            private CancellationTokenRegistration cancellationRegistration;

            /// <summary>
            /// 指示取消注册是否已经保存。
            /// </summary>
            private bool hasCancellationRegistration;

            /// <summary>
            /// 指示发送边界和截止时间是否已经记录。
            /// </summary>
            private int isArmed;

            /// <summary>
            /// 指示至少一次物理写入调用已经开始，失败或取消时可能存在部分线路请求。
            /// </summary>
            private int writeAttempted;

            /// <summary>
            /// 指示重同步是否已经由一个胜出终态启动。
            /// </summary>
            private int resynchronizationStarted;

            /// <summary>
            /// 指示资源是否已经由事务 owner 释放。
            /// </summary>
            private int isDisposed;

            /// <summary>
            /// 初始化一项尚未写入、尚未启动计时器的活动事务上下文。
            /// </summary>
            /// <param name="transactionId">当前正数事务编号。</param>
            /// <param name="request">不可变事务请求。</param>
            /// <param name="portGeneration">写入前记录的端口代次。</param>
            /// <param name="timeProvider">完成门和组帧器使用的统一时间源。</param>
            internal ActiveTransactionContext(
                long transactionId,
                TransactionRequest request,
                int portGeneration,
                TimeProvider timeProvider)
            {
                TransactionId = transactionId;
                Request = request;
                PortGeneration = portGeneration;
                Pending = new PendingTransaction(transactionId, timeProvider);
                Framer = new RtuReceiveFramer(
                    new RtuFramerOptions(
                        RtuFramerOptions.DefaultMaximumFrameBytes,
                        RtuFramerOptions.DefaultReceiveBufferBytes,
                        RtuFramerOptions.DefaultRawCaptureMaxBytes,
                        request.RawInterByteTimeout,
                        request.ResponseTimeout,
                        timeProvider));
            }

            /// <summary>
            /// 获取当前事务编号。
            /// </summary>
            internal long TransactionId { get; }

            /// <summary>
            /// 获取不可变事务请求。
            /// </summary>
            internal TransactionRequest Request { get; }

            /// <summary>
            /// 获取写入前记录的端口代次。
            /// </summary>
            internal int PortGeneration { get; }

            /// <summary>
            /// 获取唯一原子完成门。
            /// </summary>
            internal PendingTransaction Pending { get; }

            /// <summary>
            /// 获取当前事务独享的 RTU 组帧器。
            /// </summary>
            internal RtuReceiveFramer Framer { get; }

            /// <summary>
            /// 获取发送完成后观察到的最后接收序号，候选必须严格大于该值。
            /// </summary>
            internal long SentAfterReceiveSequence { get; private set; }

            /// <summary>
            /// 获取发送完成时的单调时间戳。
            /// </summary>
            internal long SentTimestamp { get; private set; }

            /// <summary>
            /// 获取严格排他的响应总截止时间戳。
            /// </summary>
            internal long DeadlineTimestamp { get; private set; }

            /// <summary>
            /// 获取发送边界是否已经完整记录。
            /// </summary>
            internal bool IsArmed => Volatile.Read(ref isArmed) != 0;

            /// <summary>
            /// 获取物理写入调用是否已经开始。
            /// </summary>
            internal bool WasWriteAttempted => Volatile.Read(ref writeAttempted) != 0;

            /// <summary>
            /// 在调用抽象传输写入前原子记录请求可能已经部分到达线路。
            /// </summary>
            internal void MarkWriteAttempted()
            {
                Volatile.Write(ref writeAttempted, 1);
            }

            /// <summary>
            /// 记录发送后的接收序号与单调截止，并发布已武装状态。
            /// </summary>
            /// <param name="sentAfterReceiveSequence">发送后观察到的末接收序号。</param>
            /// <param name="sentTimestamp">发送完成单调时间戳。</param>
            /// <param name="deadlineTimestamp">严格排他的响应截止时间戳。</param>
            internal void Arm(
                long sentAfterReceiveSequence,
                long sentTimestamp,
                long deadlineTimestamp)
            {
                SentAfterReceiveSequence = sentAfterReceiveSequence;
                SentTimestamp = sentTimestamp;
                DeadlineTimestamp = deadlineTimestamp;
                Volatile.Write(ref isArmed, 1);
            }

            /// <summary>
            /// 保存响应总计时器；若 owner 已先释放上下文则立即释放传入计时器。
            /// </summary>
            /// <param name="timer">当前事务独享的一次性响应计时器。</param>
            internal void SetResponseTimer(ITimer timer)
            {
                ArgumentNullException.ThrowIfNull(timer);

                lock (resourceSyncRoot)
                {
                    if (isDisposed != 0)
                    {
                        timer.Dispose();
                        return;
                    }

                    responseTimer = timer;
                }
            }

            /// <summary>
            /// 保存调用方取消注册；若 owner 已先释放则立即释放传入注册。
            /// </summary>
            /// <param name="registration">当前事务的取消回调注册。</param>
            internal void SetCancellationRegistration(CancellationTokenRegistration registration)
            {
                lock (resourceSyncRoot)
                {
                    if (isDisposed != 0)
                    {
                        registration.Dispose();
                        return;
                    }

                    cancellationRegistration = registration;
                    hasCancellationRegistration = true;
                }
            }

            /// <summary>
            /// 获取或创建原始字节间静默计时器。
            /// </summary>
            /// <param name="timeProvider">创建计时器的统一时间源。</param>
            /// <param name="callback">静默到期回调。</param>
            /// <param name="state">原样传递给回调的状态。</param>
            /// <returns>当前事务独享的可重新安排计时器。</returns>
            internal ITimer GetOrCreateRawInterByteTimer(
                TimeProvider timeProvider,
                TimerCallback callback,
                object state)
            {
                lock (resourceSyncRoot)
                {
                    ObjectDisposedException.ThrowIf(isDisposed != 0, this);
                    rawInterByteTimer ??= timeProvider.CreateTimer(
                        callback,
                        state,
                        Timeout.InfiniteTimeSpan,
                        Timeout.InfiniteTimeSpan);
                    return rawInterByteTimer;
                }
            }

            /// <summary>
            /// 原子标记重同步已经由当前事务启动。
            /// </summary>
            /// <returns>本次调用首次设置标记时返回 <see langword="true"/>。</returns>
            internal bool TryMarkResynchronizationStarted()
            {
                return Interlocked.CompareExchange(ref resynchronizationStarted, 1, 0) == 0;
            }

            /// <summary>
            /// 幂等释放响应、静默计时器和调用方取消注册。
            /// </summary>
            public void Dispose()
            {
                lock (resourceSyncRoot)
                {
                    if (Interlocked.Exchange(ref isDisposed, 1) != 0)
                    {
                        return;
                    }

                    responseTimer?.Dispose();
                    rawInterByteTimer?.Dispose();
                    responseTimer = null;
                    rawInterByteTimer = null;

                    if (hasCancellationRegistration)
                    {
                        cancellationRegistration.Dispose();
                        hasCancellationRegistration = false;
                    }
                }
            }
        }

        /// <summary>
        /// 保存取消回调所需的协调器和事务上下文。
        /// </summary>
        /// <param name="Coordinator">拥有事务的协调器。</param>
        /// <param name="Context">取消注册所属活动事务。</param>
        private sealed record CancellationCallbackState(
            ModbusTransactionCoordinator Coordinator,
            ActiveTransactionContext Context);

        /// <summary>
        /// 保存计时器回调所需的协调器和事务上下文。
        /// </summary>
        /// <param name="Coordinator">拥有事务的协调器。</param>
        /// <param name="Context">计时器所属活动事务。</param>
        private sealed record TimerCallbackState(
            ModbusTransactionCoordinator Coordinator,
            ActiveTransactionContext Context);

        /// <summary>
        /// 保存重同步计时器所属协调器和单调代次，隔离已释放计时器的旧回调。
        /// </summary>
        /// <param name="Coordinator">拥有重同步状态的协调器。</param>
        /// <param name="Generation">创建计时器时的重同步代次。</param>
        private sealed record ResynchronizationTimerState(
            ModbusTransactionCoordinator Coordinator,
            long Generation);
    }
}
