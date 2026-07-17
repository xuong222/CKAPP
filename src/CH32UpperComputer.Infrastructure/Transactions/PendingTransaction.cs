using System.Collections.ObjectModel;

namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 记录一个未能赢得事务原子完成门的候选终态。
    /// </summary>
    public sealed class TransactionCompletionAttempt
    {
        /// <summary>
        /// 初始化一项失败候选诊断。
        /// </summary>
        /// <param name="attemptedState">本次事件尝试设置的终态。</param>
        /// <param name="winningState">原子门已经保存的胜出终态。</param>
        /// <param name="attemptedTimestamp">本次竞争发生时的单调时间戳。</param>
        internal TransactionCompletionAttempt(
            TransactionCompletionState attemptedState,
            TransactionCompletionState winningState,
            long attemptedTimestamp)
        {
            AttemptedState = attemptedState;
            WinningState = winningState;
            AttemptedTimestamp = attemptedTimestamp;
        }

        /// <summary>
        /// 获取竞争失败事件尝试设置的终态。
        /// </summary>
        public TransactionCompletionState AttemptedState { get; }

        /// <summary>
        /// 获取原子门中已经胜出的终态。
        /// </summary>
        public TransactionCompletionState WinningState { get; }

        /// <summary>
        /// 获取竞争失败事件到达时的单调时间戳。
        /// </summary>
        public long AttemptedTimestamp { get; }
    }

    /// <summary>
    /// 为单个事务提供唯一 Interlocked 完成入口和异步最终结果。
    /// </summary>
    public sealed class PendingTransaction
    {
        /// <summary>
        /// 单项事务最多保留的失败候选诊断数量，防止异常调用形成无界增长。
        /// </summary>
        private const int MaximumLosingAttemptCount = 64;

        /// <summary>
        /// 保护失败候选诊断列表。
        /// </summary>
        private readonly object attemptsSyncRoot = new();

        /// <summary>
        /// 提供失败候选到达时间的统一单调时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 使用异步延续选项交付唯一最终结果的完成源。
        /// </summary>
        private readonly TaskCompletionSource<TransactionOutcome> completionSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 按到达顺序保留的有界失败候选诊断。
        /// </summary>
        private readonly List<TransactionCompletionAttempt> losingAttempts = [];

        /// <summary>
        /// 由所有终止事件竞争的整数完成状态；初值必须为 Pending。
        /// </summary>
        private int completionState = (int)TransactionCompletionState.Pending;

        /// <summary>
        /// 原子门胜出后保存的最终结果。
        /// </summary>
        private TransactionOutcome? winningOutcome;

        /// <summary>
        /// 初始化一项尚未完成的软件事务。
        /// </summary>
        /// <param name="transactionId">协调器已经分配的正事务编号。</param>
        /// <param name="timeProvider">记录竞争事件到达时间的统一时间源。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="transactionId"/> 不是正数时抛出。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> 为空时抛出。</exception>
        public PendingTransaction(
            long transactionId,
            TimeProvider timeProvider)
        {
            if (transactionId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(transactionId));
            }

            ArgumentNullException.ThrowIfNull(timeProvider);
            TransactionId = transactionId;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 获取当前软件事务编号。
        /// </summary>
        public long TransactionId { get; }

        /// <summary>
        /// 获取通过原子读取观察到的当前完成状态。
        /// </summary>
        public TransactionCompletionState CompletionState =>
            (TransactionCompletionState)Volatile.Read(ref completionState);

        /// <summary>
        /// 获取异步交付唯一最终结果的任务。
        /// </summary>
        public Task<TransactionOutcome> Completion => completionSource.Task;

        /// <summary>
        /// 获取已经胜出的最终结果；仍为 Pending 时为空。
        /// </summary>
        public TransactionOutcome? WinningOutcome => Volatile.Read(ref winningOutcome);

        /// <summary>
        /// 获取失败候选诊断的独立只读快照。
        /// </summary>
        public IReadOnlyList<TransactionCompletionAttempt> LosingAttempts
        {
            get
            {
                lock (attemptsSyncRoot)
                {
                    return new ReadOnlyCollection<TransactionCompletionAttempt>(
                        losingAttempts.ToArray());
                }
            }
        }

        /// <summary>
        /// 让一项终止事件竞争唯一原子完成门。
        /// </summary>
        /// <param name="newState">本事件尝试设置的非 Pending 终态。</param>
        /// <param name="outcome">事务编号和状态均与本次尝试一致的最终结果。</param>
        /// <returns>本事件是第一个将 Pending 改为终态的胜者时返回 <see langword="true"/>。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="newState"/> 不是已定义终态时抛出。</exception>
        /// <exception cref="ArgumentException"><paramref name="outcome"/> 与当前事务或候选状态不一致时抛出。</exception>
        public bool TryComplete(
            TransactionCompletionState newState,
            TransactionOutcome outcome)
        {
            if (!Enum.IsDefined(newState) || newState == TransactionCompletionState.Pending)
            {
                throw new ArgumentOutOfRangeException(nameof(newState), newState, "完成候选必须是已定义终态。");
            }

            ArgumentNullException.ThrowIfNull(outcome);

            if (outcome.TransactionId != TransactionId || outcome.State != newState)
            {
                throw new ArgumentException("事务结果的编号和状态必须与本次完成候选一致。", nameof(outcome));
            }

            int previous = Interlocked.CompareExchange(
                ref completionState,
                (int)newState,
                (int)TransactionCompletionState.Pending);

            if (previous != (int)TransactionCompletionState.Pending)
            {
                RecordLosingCandidate(
                    newState,
                    (TransactionCompletionState)previous,
                    timeProvider.GetTimestamp());
                return false;
            }

            Volatile.Write(ref winningOutcome, outcome);
            completionSource.TrySetResult(outcome);
            return true;
        }

        /// <summary>
        /// 有界保存未赢得原子完成门的候选状态诊断。
        /// </summary>
        /// <param name="attemptedState">失败事件尝试设置的终态。</param>
        /// <param name="winningState">原子门已经保存的胜出终态。</param>
        /// <param name="attemptedTimestamp">失败事件竞争时的单调时间戳。</param>
        private void RecordLosingCandidate(
            TransactionCompletionState attemptedState,
            TransactionCompletionState winningState,
            long attemptedTimestamp)
        {
            lock (attemptsSyncRoot)
            {
                if (losingAttempts.Count == MaximumLosingAttemptCount)
                {
                    losingAttempts.RemoveAt(0);
                }

                losingAttempts.Add(new TransactionCompletionAttempt(
                    attemptedState,
                    winningState,
                    attemptedTimestamp));
            }
        }
    }
}
