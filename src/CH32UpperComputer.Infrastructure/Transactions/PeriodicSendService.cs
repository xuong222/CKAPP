namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 定义纯 fixed-delay 定时发送流程当前所处的阶段。
    /// </summary>
    public enum PeriodicSendPhase
    {
        /// <summary>
        /// 定时发送关闭，不存在活动调度代次。
        /// </summary>
        Off = 0,

        /// <summary>
        /// 从上次尝试完成时刻起等待一个完整发送间隔。
        /// </summary>
        WaitingInterval = 1,

        /// <summary>
        /// 间隔已经结束，正在复核代次并尝试占用无队列协调器。
        /// </summary>
        Submitting = 2,

        /// <summary>
        /// 当前定时事务已经提交，正在等待其唯一最终结果。
        /// </summary>
        WaitingTransaction = 3,
    }

    /// <summary>
    /// 提供默认关闭、无积压且不追赶的纯 fixed-delay 定时发送流程。
    /// </summary>
    public sealed class PeriodicSendService : IAsyncDisposable
    {
        /// <summary>
        /// 定时发送允许的最小完整间隔。
        /// </summary>
        public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// 定时发送允许的最大完整间隔。
        /// </summary>
        public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// 保护配置、活动调度代次、阶段和释放状态。
        /// </summary>
        private readonly object stateSyncRoot = new();

        /// <summary>
        /// 所有定时尝试共用的无队列事务协调器。
        /// </summary>
        private readonly ModbusTransactionCoordinator coordinator;

        /// <summary>
        /// 为完整发送间隔提供生产或测试时间。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 最近配置的不可变事务请求；尚未配置时为空。
        /// </summary>
        private TransactionRequest? configuredRequest;

        /// <summary>
        /// 最近配置的完整 fixed-delay 间隔。
        /// </summary>
        private TimeSpan configuredInterval;

        /// <summary>
        /// 当前活动调度会话；关闭时为空。
        /// </summary>
        private ScheduleSession? activeSession;

        /// <summary>
        /// 每次启动或停止均递增的调度代次，用于拒绝旧延续写入。
        /// </summary>
        private long scheduleGeneration;

        /// <summary>
        /// 当前对外可观察的调度阶段。
        /// </summary>
        private PeriodicSendPhase phase = PeriodicSendPhase.Off;

        /// <summary>
        /// 已经由协调器返回结果的定时尝试总数；Busy 也计为一次尝试。
        /// </summary>
        private long completedAttemptCount;

        /// <summary>
        /// 调度循环或观察者最近发生的、已经被隔离的异常。
        /// </summary>
        private Exception? lastFault;

        /// <summary>
        /// 指示服务已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 初始化一项默认关闭且尚未配置请求的定时发送服务。
        /// </summary>
        /// <param name="coordinator">所有手动和定时请求共用的无队列事务协调器。</param>
        /// <param name="timeProvider">完整间隔使用的统一时间源。</param>
        /// <exception cref="ArgumentNullException">任一依赖为空时抛出。</exception>
        public PeriodicSendService(
            ModbusTransactionCoordinator coordinator,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(coordinator);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.coordinator = coordinator;
            this.timeProvider = timeProvider;
            coordinator.StateChanged += HandleCoordinatorStateChanged;
        }

        /// <summary>
        /// 在定时发送开关实际变化后发布新值。
        /// </summary>
        public event Action<bool>? RunningChanged;

        /// <summary>
        /// 在调度阶段实际变化后发布新阶段。
        /// </summary>
        public event Action<PeriodicSendPhase>? PhaseChanged;

        /// <summary>
        /// 在一个完整间隔结束、提交前代次复核之前发布当前调度代次。
        /// </summary>
        public event Action<long>? IntervalElapsed;

        /// <summary>
        /// 在协调器返回一次定时尝试结果后发布该不可变结果。
        /// </summary>
        public event Action<TransactionExecutionResult>? AttemptCompleted;

        /// <summary>
        /// 获取定时发送是否由一个活动调度代次保持开启。
        /// </summary>
        public bool IsRunning
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return activeSession is not null;
                }
            }
        }

        /// <summary>
        /// 获取当前调度阶段。
        /// </summary>
        public PeriodicSendPhase Phase
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return phase;
                }
            }
        }

        /// <summary>
        /// 获取最近调度代次；每次启动和停止都会递增。
        /// </summary>
        public long ScheduleGeneration
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return scheduleGeneration;
                }
            }
        }

        /// <summary>
        /// 获取已经完成的定时尝试数量；Busy 计入尝试但不会形成线路写入。
        /// </summary>
        public long CompletedAttemptCount => Interlocked.Read(ref completedAttemptCount);

        /// <summary>
        /// 获取最近配置的请求；尚未配置时为空。
        /// </summary>
        public TransactionRequest? ConfiguredRequest
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return configuredRequest;
                }
            }
        }

        /// <summary>
        /// 获取最近配置的完整发送间隔；尚未配置时为零。
        /// </summary>
        public TimeSpan ConfiguredInterval
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return configuredInterval;
                }
            }
        }

        /// <summary>
        /// 获取最近被隔离的调度或观察者异常。
        /// </summary>
        public Exception? LastFault
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return lastFault;
                }
            }
        }

        /// <summary>
        /// 在定时发送关闭时保存下一次启动使用的请求和完整间隔，本方法不会发送数据。
        /// </summary>
        /// <param name="request">每次调度尝试提交的不可变事务请求。</param>
        /// <param name="interval">每次尝试完成后重新等待的完整间隔。</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> 超出 100 毫秒至 1 小时时抛出。</exception>
        /// <exception cref="InvalidOperationException">服务正在运行时抛出。</exception>
        /// <exception cref="ObjectDisposedException">服务已经永久释放时抛出。</exception>
        public void Configure(
            TransactionRequest request,
            TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateInterval(interval);

            lock (stateSyncRoot)
            {
                ThrowIfDisposedUnderLock();

                if (activeSession is not null)
                {
                    throw new InvalidOperationException("定时发送运行期间不能更改请求或间隔。");
                }

                configuredRequest = request;
                configuredInterval = interval;
            }
        }

        /// <summary>
        /// 启动一个新调度代次；首次发送前同样等待一个完整间隔。
        /// </summary>
        /// <exception cref="InvalidOperationException">尚未配置、已经运行或协调器已经断开时抛出。</exception>
        /// <exception cref="ObjectDisposedException">服务已经永久释放时抛出。</exception>
        public void Start()
        {
            ScheduleSession session;

            lock (stateSyncRoot)
            {
                ThrowIfDisposedUnderLock();

                if (configuredRequest is null)
                {
                    throw new InvalidOperationException("启动定时发送前必须先配置请求和间隔。");
                }

                if (activeSession is not null)
                {
                    throw new InvalidOperationException("定时发送已经启动。");
                }

                if (coordinator.State is TransactionCoordinatorState.Disconnected or
                    TransactionCoordinatorState.ApplicationStopping)
                {
                    throw new InvalidOperationException("串口未连接或应用正在退出，不能启动定时发送。");
                }

                long generation = checked(++scheduleGeneration);
                session = new ScheduleSession(
                    generation,
                    configuredRequest,
                    configuredInterval);
                activeSession = session;
                phase = PeriodicSendPhase.WaitingInterval;
            }

            PublishRunningChanged(true);
            PublishPhaseChanged(PeriodicSendPhase.WaitingInterval);
            _ = RunScheduleAsync(session);
        }

        /// <summary>
        /// 立即使当前调度代次失效并取消间隔等待；已经开始的单次事务允许完成。
        /// </summary>
        public void Stop()
        {
            StopCurrentSession();
        }

        /// <summary>
        /// 立即停止新调度，并等待已经开始的单次事务和调度循环完全退出。
        /// </summary>
        /// <param name="cancellationToken">只取消调用方等待，不恢复或重新启动调度。</param>
        /// <returns>当前调度循环已经退出后的任务。</returns>
        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Task completion = StopCurrentSession();
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 幂等停止调度、取消订阅协调器状态并永久释放服务。
        /// </summary>
        /// <returns>活动调度循环已经退出后的值任务。</returns>
        public async ValueTask DisposeAsync()
        {
            Task completion;
            bool runningChanged;

            lock (stateSyncRoot)
            {
                if (isDisposed)
                {
                    return;
                }

                isDisposed = true;
                completion = StopCurrentSessionUnderLock(out runningChanged);

                if (runningChanged)
                {
                    phase = PeriodicSendPhase.Off;
                }
            }

            coordinator.StateChanged -= HandleCoordinatorStateChanged;

            if (runningChanged)
            {
                PublishRunningChanged(false);
                PublishPhaseChanged(PeriodicSendPhase.Off);
            }

            await completion.ConfigureAwait(false);
        }

        /// <summary>
        /// 运行单一调度代次，保证每次尝试完成后才重新等待一个完整间隔。
        /// </summary>
        /// <param name="session">当前启动创建的不可变请求、间隔和代次会话。</param>
        /// <returns>代次停止并释放取消源后完成的任务。</returns>
        private async Task RunScheduleAsync(ScheduleSession session)
        {
            try
            {
                while (IsCurrentSession(session))
                {
                    ChangePhase(session, PeriodicSendPhase.WaitingInterval);

                    try
                    {
                        await Task.Delay(
                            session.Interval,
                            timeProvider,
                            session.Cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    PublishIntervalElapsed(session.Generation);

                    if (!IsCurrentSession(session))
                    {
                        break;
                    }

                    ChangePhase(session, PeriodicSendPhase.Submitting);

                    if (!IsCurrentSession(session))
                    {
                        break;
                    }

                    ValueTask<TransactionExecutionResult> execution =
                        coordinator.TryExecuteScheduledAsync(
                            session.Request,
                            beginWrite => TryStartWrite(session, beginWrite));
                    ChangePhase(session, PeriodicSendPhase.WaitingTransaction);
                    TransactionExecutionResult result = await execution.ConfigureAwait(false);
                    Interlocked.Increment(ref completedAttemptCount);
                    PublishAttemptCompleted(result);

                    if (!IsCurrentSession(session))
                    {
                        break;
                    }

                    if (!ShouldContinue(result))
                    {
                        StopSession(session);
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                RecordFault(exception);
                StopSession(session);
            }
            finally
            {
                StopSession(session);
                session.Cancellation.Dispose();
                session.CompletionSource.TrySetResult();
            }
        }

        /// <summary>
        /// 判断协调器结果是否允许从当前时刻重新等待一个完整间隔。
        /// </summary>
        /// <param name="result">协调器返回的接受或拒绝结果。</param>
        /// <returns>成功、原始捕获或 Busy 时为真；其他结果为假。</returns>
        private static bool ShouldContinue(TransactionExecutionResult result)
        {
            if (!result.IsAccepted)
            {
                return result.Rejection == TransactionRejected.Busy;
            }

            return result.Outcome!.State is
                TransactionCompletionState.Succeeded or
                TransactionCompletionState.RawCaptured;
        }

        /// <summary>
        /// 与 Stop 竞争同一服务锁，并在授权仍有效时于锁内同步启动物理写调用。
        /// </summary>
        /// <param name="session">正在提交请求的调度会话。</param>
        /// <param name="beginWrite">
        /// 由协调器提供的写启动回调；只允许同步取得值任务，不得在本锁内等待异步完成。
        /// </param>
        /// <returns>写调用已经在当前代次内启动时返回其值任务；Stop 先完成时返回空值。</returns>
        private ValueTask? TryStartWrite(
            ScheduleSession session,
            Func<ValueTask> beginWrite)
        {
            ArgumentNullException.ThrowIfNull(beginWrite);

            lock (stateSyncRoot)
            {
                if (!ReferenceEquals(activeSession, session) ||
                    scheduleGeneration != session.Generation ||
                    session.Cancellation.IsCancellationRequested)
                {
                    return null;
                }

                return beginWrite();
            }
        }

        /// <summary>
        /// 判断会话仍是当前活动代次。
        /// </summary>
        /// <param name="session">待核对调度会话。</param>
        /// <returns>会话仍活动且代次一致时返回真。</returns>
        private bool IsCurrentSession(ScheduleSession session)
        {
            lock (stateSyncRoot)
            {
                return ReferenceEquals(activeSession, session) &&
                    scheduleGeneration == session.Generation;
            }
        }

        /// <summary>
        /// 停止当前任意活动会话并返回其退出任务。
        /// </summary>
        /// <returns>没有活动会话时为已完成任务，否则为该会话退出任务。</returns>
        private Task StopCurrentSession()
        {
            Task completion;
            bool runningChanged;

            lock (stateSyncRoot)
            {
                completion = StopCurrentSessionUnderLock(out runningChanged);

                if (runningChanged)
                {
                    phase = PeriodicSendPhase.Off;
                }
            }

            if (runningChanged)
            {
                PublishRunningChanged(false);
                PublishPhaseChanged(PeriodicSendPhase.Off);
            }

            return completion;
        }

        /// <summary>
        /// 在已持有状态锁时使当前会话失效并递增代次。
        /// </summary>
        /// <param name="runningChanged">返回本次调用是否实际关闭一个活动会话。</param>
        /// <returns>被停止会话的退出任务。</returns>
        private Task StopCurrentSessionUnderLock(out bool runningChanged)
        {
            ScheduleSession? session = activeSession;
            scheduleGeneration = checked(scheduleGeneration + 1);
            activeSession = null;
            runningChanged = session is not null;
            session?.Cancellation.Cancel();
            return session?.CompletionSource.Task ?? Task.CompletedTask;
        }

        /// <summary>
        /// 只在指定会话仍活动时停止它。
        /// </summary>
        /// <param name="session">需要停止的调度会话。</param>
        private void StopSession(ScheduleSession session)
        {
            bool runningChanged = false;

            lock (stateSyncRoot)
            {
                if (ReferenceEquals(activeSession, session))
                {
                    scheduleGeneration = checked(scheduleGeneration + 1);
                    activeSession = null;
                    phase = PeriodicSendPhase.Off;
                    runningChanged = true;
                    session.Cancellation.Cancel();
                }
            }

            if (runningChanged)
            {
                PublishRunningChanged(false);
                PublishPhaseChanged(PeriodicSendPhase.Off);
            }
        }

        /// <summary>
        /// 只在指定会话仍活动时改变阶段并发布实际变化。
        /// </summary>
        /// <param name="session">阶段变化所属调度会话。</param>
        /// <param name="newPhase">目标阶段。</param>
        private void ChangePhase(
            ScheduleSession session,
            PeriodicSendPhase newPhase)
        {
            bool changed = false;

            lock (stateSyncRoot)
            {
                if (ReferenceEquals(activeSession, session) && phase != newPhase)
                {
                    phase = newPhase;
                    changed = true;
                }
            }

            if (changed)
            {
                PublishPhaseChanged(newPhase);
            }
        }

        /// <summary>
        /// 协调器断开或应用停止时立即使调度代次失效。
        /// </summary>
        /// <param name="state">协调器发布的新连接状态。</param>
        private void HandleCoordinatorStateChanged(TransactionCoordinatorState state)
        {
            if (state is TransactionCoordinatorState.Disconnected or
                TransactionCoordinatorState.ApplicationStopping)
            {
                Stop();
            }
        }

        /// <summary>
        /// 安全发布运行开关变化。
        /// </summary>
        /// <param name="isRunning">新的运行状态。</param>
        private void PublishRunningChanged(bool isRunning)
        {
            InvokeObservers(RunningChanged, isRunning);
        }

        /// <summary>
        /// 安全发布阶段变化。
        /// </summary>
        /// <param name="newPhase">新的调度阶段。</param>
        private void PublishPhaseChanged(PeriodicSendPhase newPhase)
        {
            InvokeObservers(PhaseChanged, newPhase);
        }

        /// <summary>
        /// 安全发布完整间隔结束事件。
        /// </summary>
        /// <param name="generation">发生事件的调度代次。</param>
        private void PublishIntervalElapsed(long generation)
        {
            InvokeObservers(IntervalElapsed, generation);
        }

        /// <summary>
        /// 安全发布定时尝试结果。
        /// </summary>
        /// <param name="result">协调器返回的不可变尝试结果。</param>
        private void PublishAttemptCompleted(TransactionExecutionResult result)
        {
            InvokeObservers(AttemptCompleted, result);
        }

        /// <summary>
        /// 逐个调用观察者并隔离观察者异常。
        /// </summary>
        /// <typeparam name="T">观察者接收的值类型。</typeparam>
        /// <param name="observers">可为空的多播观察者。</param>
        /// <param name="value">传递给观察者的值。</param>
        private void InvokeObservers<T>(
            Action<T>? observers,
            T value)
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
                    RecordFault(exception);
                }
            }
        }

        /// <summary>
        /// 保存最近被隔离的调度或观察者异常。
        /// </summary>
        /// <param name="exception">已经被观察且不会逃逸后台循环的异常。</param>
        private void RecordFault(Exception exception)
        {
            lock (stateSyncRoot)
            {
                lastFault = exception;
            }
        }

        /// <summary>
        /// 校验完整定时间隔处于规定闭区间。
        /// </summary>
        /// <param name="interval">待校验间隔。</param>
        private static void ValidateInterval(TimeSpan interval)
        {
            if (interval < MinimumInterval || interval > MaximumInterval)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(interval),
                    interval,
                    "定时发送间隔必须位于 100 至 3600000 毫秒。");
            }
        }

        /// <summary>
        /// 在已持有状态锁时拒绝永久释放后的操作。
        /// </summary>
        /// <exception cref="ObjectDisposedException">服务已经永久释放时抛出。</exception>
        private void ThrowIfDisposedUnderLock()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
        }

        /// <summary>
        /// 保存一个调度代次独占的请求、间隔、取消源和退出完成源。
        /// </summary>
        private sealed class ScheduleSession
        {
            /// <summary>
            /// 初始化一个尚未开始首个完整间隔的调度会话。
            /// </summary>
            /// <param name="generation">本调度会话唯一代次。</param>
            /// <param name="request">每轮提交的不可变请求。</param>
            /// <param name="interval">每轮开始前等待的完整间隔。</param>
            internal ScheduleSession(
                long generation,
                TransactionRequest request,
                TimeSpan interval)
            {
                Generation = generation;
                Request = request;
                Interval = interval;
                Cancellation = new CancellationTokenSource();
                CompletionSource = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            /// <summary>
            /// 获取本会话唯一调度代次。
            /// </summary>
            internal long Generation { get; }

            /// <summary>
            /// 获取每轮提交的不可变事务请求。
            /// </summary>
            internal TransactionRequest Request { get; }

            /// <summary>
            /// 获取每轮开始前等待的完整间隔。
            /// </summary>
            internal TimeSpan Interval { get; }

            /// <summary>
            /// 获取停止本调度代次间隔等待的取消源。
            /// </summary>
            internal CancellationTokenSource Cancellation { get; }

            /// <summary>
            /// 获取调度循环退出完成源。
            /// </summary>
            internal TaskCompletionSource CompletionSource { get; }
        }
    }
}
