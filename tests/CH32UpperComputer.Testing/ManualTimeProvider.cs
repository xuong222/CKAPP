namespace CH32UpperComputer.Testing
{
    /// <summary>
    /// 提供无需真实等待的同步确定性时间源，供超时、周期任务和竞态边界测试使用。
    /// </summary>
    public sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>
        /// 保护当前时间、计时器集合及创建顺序的同步门。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 仍归属于当前时间源的全部计时器；已释放项会在安全时机清理。
        /// </summary>
        private readonly List<ManualTimer> timers = [];

        /// <summary>
        /// 当前 UTC 日历时间。
        /// </summary>
        private DateTimeOffset utcNow;

        /// <summary>
        /// 当前单调时间戳，以 <see cref="TimeSpan.TicksPerSecond"/> 为每秒频率。
        /// </summary>
        private long timestamp;

        /// <summary>
        /// 下一个计时器的稳定创建顺序，用于同一到期时刻的确定性排序。
        /// </summary>
        private long nextCreationOrder;

        /// <summary>
        /// 使用 Unix 纪元和零单调时间戳初始化确定性时间源。
        /// </summary>
        public ManualTimeProvider()
            : this(DateTimeOffset.UnixEpoch, 0)
        {
        }

        /// <summary>
        /// 使用调用方指定的日历时间和单调时间戳初始化确定性时间源。
        /// </summary>
        /// <param name="initialUtcNow">初始 UTC 日历时间；非零偏移会先规范化为 UTC。</param>
        /// <param name="initialTimestamp">初始非负单调时间戳。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialTimestamp"/> 为负数时抛出。</exception>
        public ManualTimeProvider(
            DateTimeOffset initialUtcNow,
            long initialTimestamp = 0)
        {
            if (initialTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialTimestamp),
                    initialTimestamp,
                    "初始单调时间戳不能为负数。");
            }

            utcNow = initialUtcNow.ToUniversalTime();
            timestamp = initialTimestamp;
        }

        /// <summary>
        /// 获取该时间源的单调时间戳频率；一个时间戳单位等于一个 <see cref="TimeSpan"/> 计时周期。
        /// </summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>
        /// 获取当前受控 UTC 日历时间。
        /// </summary>
        /// <returns>最近一次推进后的 UTC 时间。</returns>
        public override DateTimeOffset GetUtcNow()
        {
            lock (syncRoot)
            {
                return utcNow;
            }
        }

        /// <summary>
        /// 获取当前受控单调时间戳。
        /// </summary>
        /// <returns>最近一次推进后的非负时间戳。</returns>
        public override long GetTimestamp()
        {
            lock (syncRoot)
            {
                return timestamp;
            }
        }

        /// <summary>
        /// 创建一个由本时间源驱动的确定性计时器。
        /// </summary>
        /// <param name="callback">到期时在时间源锁外同步执行的回调。</param>
        /// <param name="state">原样传递给 <paramref name="callback"/> 的状态。</param>
        /// <param name="dueTime">首次到期延迟；零立即触发，<see cref="Timeout.InfiniteTimeSpan"/> 表示暂不启用。</param>
        /// <param name="period">后续周期；正值表示周期执行，零或 <see cref="Timeout.InfiniteTimeSpan"/> 表示仅执行一次。</param>
        /// <returns>支持更改计划和同步或异步释放的受控计时器。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="callback"/> 为 <see langword="null"/> 时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">到期延迟或周期为非无限的负值时抛出。</exception>
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            ValidateTimerInterval(dueTime, nameof(dueTime));
            ValidateTimerInterval(period, nameof(period));

            ManualTimer timer;

            lock (syncRoot)
            {
                timer = new ManualTimer(
                    this,
                    callback,
                    state,
                    checked(++nextCreationOrder));
                timers.Add(timer);
                SetTimerScheduleUnderLock(timer, dueTime, period);
            }

            FireDueTimersThrough(GetTimestamp());
            return timer;
        }

        /// <summary>
        /// 将日历时间和单调时间共同向前推进，并按到期时间及创建顺序触发全部到期计时器。
        /// </summary>
        /// <param name="amount">非负推进量；零也会处理当前时刻刚被启用的计时器。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> 为负数时抛出。</exception>
        /// <exception cref="OverflowException">推进后的时间超出 <see cref="DateTimeOffset"/> 或 64 位时间戳范围时抛出。</exception>
        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount),
                    amount,
                    "手动时间只能向前推进。");
            }

            long targetTimestamp;

            lock (syncRoot)
            {
                targetTimestamp = checked(timestamp + amount.Ticks);
            }

            FireDueTimersThrough(targetTimestamp);
        }

        /// <summary>
        /// 更新指定计时器的计划，并在零延迟时于锁外立即执行到期回调。
        /// </summary>
        /// <param name="timer">归属于当前时间源的计时器。</param>
        /// <param name="dueTime">新的首次到期延迟。</param>
        /// <param name="period">新的后续周期。</param>
        /// <returns>计时器尚未释放且计划已更新时返回 <see langword="true"/>。</returns>
        internal bool ChangeTimer(
            ManualTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ValidateTimerInterval(dueTime, nameof(dueTime));
            ValidateTimerInterval(period, nameof(period));

            long currentTimestamp;

            lock (syncRoot)
            {
                if (timer.IsDisposed)
                {
                    return false;
                }

                SetTimerScheduleUnderLock(timer, dueTime, period);
                currentTimestamp = timestamp;
            }

            FireDueTimersThrough(currentTimestamp);
            return true;
        }

        /// <summary>
        /// 将指定计时器标记为已释放并从后续调度中移除。
        /// </summary>
        /// <param name="timer">归属于当前时间源的计时器。</param>
        internal void DisposeTimer(ManualTimer timer)
        {
            lock (syncRoot)
            {
                if (timer.IsDisposed)
                {
                    return;
                }

                timer.IsDisposed = true;
                timer.NextDueTimestamp = null;
                timers.Remove(timer);
            }
        }

        /// <summary>
        /// 在已持有同步门时把相对延迟转换成绝对时间戳并保存周期。
        /// </summary>
        /// <param name="timer">需要更新的内部计时器。</param>
        /// <param name="dueTime">首次到期的相对延迟或无限值。</param>
        /// <param name="period">正周期、零或无限值。</param>
        private void SetTimerScheduleUnderLock(
            ManualTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {
            timer.Period = period > TimeSpan.Zero
                ? period
                : Timeout.InfiniteTimeSpan;
            timer.NextDueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : checked(timestamp + dueTime.Ticks);
        }

        /// <summary>
        /// 将时间推进到目标时间戳，并逐个在同步门外执行到期回调。
        /// </summary>
        /// <param name="targetTimestamp">本轮允许推进到的绝对单调时间戳。</param>
        private void FireDueTimersThrough(long targetTimestamp)
        {
            while (true)
            {
                ManualTimer? timerToFire;

                lock (syncRoot)
                {
                    if (timestamp > targetTimestamp)
                    {
                        return;
                    }

                    timerToFire = timers
                        .Where(timer =>
                            !timer.IsDisposed &&
                            timer.NextDueTimestamp.HasValue &&
                            timer.NextDueTimestamp.Value <= targetTimestamp)
                        .OrderBy(timer => timer.NextDueTimestamp)
                        .ThenBy(timer => timer.CreationOrder)
                        .FirstOrDefault();

                    if (timerToFire is null)
                    {
                        MoveClockUnderLock(targetTimestamp);
                        return;
                    }

                    long dueTimestamp = timerToFire.NextDueTimestamp!.Value;

                    if (dueTimestamp > timestamp)
                    {
                        MoveClockUnderLock(dueTimestamp);
                    }

                    if (timerToFire.Period == Timeout.InfiniteTimeSpan)
                    {
                        timerToFire.NextDueTimestamp = null;
                    }
                    else
                    {
                        timerToFire.NextDueTimestamp = checked(
                            dueTimestamp + timerToFire.Period.Ticks);
                    }
                }

                timerToFire.InvokeCallback();
            }
        }

        /// <summary>
        /// 在已持有同步门时同步推进单调时间和 UTC 日历时间。
        /// </summary>
        /// <param name="targetTimestamp">不得早于当前时间的目标时间戳。</param>
        private void MoveClockUnderLock(long targetTimestamp)
        {
            if (targetTimestamp < timestamp)
            {
                return;
            }

            long elapsedTicks = targetTimestamp - timestamp;
            utcNow = utcNow.AddTicks(elapsedTicks);
            timestamp = targetTimestamp;
        }

        /// <summary>
        /// 校验计时器间隔是否为非负值或框架规定的无限值。
        /// </summary>
        /// <param name="value">待校验的到期延迟或周期。</param>
        /// <param name="parameterName">异常中使用的参数名称。</param>
        private static void ValidateTimerInterval(
            TimeSpan value,
            string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "计时器间隔不能为负数；只有 Timeout.InfiniteTimeSpan 表示无限。");
            }
        }

        /// <summary>
        /// 实现 <see cref="ITimer"/> 的受控计时器；所有状态变更均委托所属时间源串行化。
        /// </summary>
        internal sealed class ManualTimer : ITimer
        {
            /// <summary>
            /// 管理本计时器的确定性时间源。
            /// </summary>
            private readonly ManualTimeProvider owner;

            /// <summary>
            /// 到期时执行的回调。
            /// </summary>
            private readonly TimerCallback callback;

            /// <summary>
            /// 原样传递给回调的状态。
            /// </summary>
            private readonly object? state;

            /// <summary>
            /// 初始化一个尚未安排到期时刻的内部计时器。
            /// </summary>
            /// <param name="owner">管理计时器状态和时间推进的时间源。</param>
            /// <param name="callback">到期回调。</param>
            /// <param name="state">传递给回调的状态。</param>
            /// <param name="creationOrder">同一时间源内唯一且递增的创建顺序。</param>
            internal ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                long creationOrder)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
                CreationOrder = creationOrder;
            }

            /// <summary>
            /// 获取稳定创建顺序，用于同一时刻的确定性回调排序。
            /// </summary>
            internal long CreationOrder { get; }

            /// <summary>
            /// 获取或设置下一绝对到期时间戳；<see langword="null"/> 表示未启用。
            /// </summary>
            internal long? NextDueTimestamp { get; set; }

            /// <summary>
            /// 获取或设置后续正周期；无限值表示一次性计时器。
            /// </summary>
            internal TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

            /// <summary>
            /// 获取或设置计时器是否已经释放。
            /// </summary>
            internal bool IsDisposed { get; set; }

            /// <summary>
            /// 更改首次到期延迟和周期。
            /// </summary>
            /// <param name="dueTime">新的首次到期延迟；零立即到期，无限值禁用。</param>
            /// <param name="period">新的后续周期；零或无限值表示仅执行一次。</param>
            /// <returns>计时器尚未释放且更改成功时返回 <see langword="true"/>。</returns>
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                return owner.ChangeTimer(this, dueTime, period);
            }

            /// <summary>
            /// 幂等释放计时器并阻止尚未开始的后续回调。
            /// </summary>
            public void Dispose()
            {
                owner.DisposeTimer(this);
            }

            /// <summary>
            /// 同步完成释放，并以已完成值任务满足异步释放契约。
            /// </summary>
            /// <returns>已经完成的值任务。</returns>
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            /// <summary>
            /// 在所属时间源的同步门外调用用户回调，使回调能够安全地更改计时器或推进时间。
            /// </summary>
            internal void InvokeCallback()
            {
                callback(state);
            }
        }
    }
}
