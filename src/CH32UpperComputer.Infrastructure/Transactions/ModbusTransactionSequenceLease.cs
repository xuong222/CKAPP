namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 表示协调器唯一活动门上的无队列独占事务序列租约。
    /// </summary>
    public sealed class ModbusTransactionSequenceLease : IAsyncDisposable
    {
        /// <summary>
        /// 拥有唯一活动门的事务协调器。
        /// </summary>
        private readonly ModbusTransactionCoordinator coordinator;

        /// <summary>
        /// 指示租约内部当前是否正在执行一个事务。
        /// </summary>
        private int executionFlag;

        /// <summary>
        /// 指示租约是否已经释放。
        /// </summary>
        private int disposedFlag;

        /// <summary>
        /// 初始化一个只允许协调器创建的独占序列租约。
        /// </summary>
        /// <param name="coordinator">拥有唯一活动门的事务协调器。</param>
        internal ModbusTransactionSequenceLease(ModbusTransactionCoordinator coordinator)
        {
            ArgumentNullException.ThrowIfNull(coordinator);
            this.coordinator = coordinator;
        }

        /// <summary>
        /// 获取租约是否已经释放。
        /// </summary>
        internal bool IsDisposed => Volatile.Read(ref disposedFlag) != 0;

        /// <summary>
        /// 在租约保持唯一活动门期间执行一项标准事务。
        /// </summary>
        /// <param name="request">需要顺序执行的不可变事务请求。</param>
        /// <param name="cancellationToken">取消当前事务但不建立等待队列的令牌。</param>
        /// <returns>当前事务的唯一终态或立即拒绝结果。</returns>
        public async ValueTask<TransactionExecutionResult> ExecuteAsync(
            TransactionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (Interlocked.CompareExchange(ref executionFlag, 1, 0) != 0)
            {
                throw new InvalidOperationException("同一个独占事务序列不能并发执行多个请求。");
            }

            try
            {
                return await coordinator.TryExecuteWithinSequenceAsync(
                    this,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref executionFlag, 0);
            }
        }

        /// <summary>
        /// 释放唯一活动门，使后续手动或定时请求可以重新竞争协调器。
        /// </summary>
        /// <returns>同步完成的值任务。</returns>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            if (Volatile.Read(ref executionFlag) != 0)
            {
                Interlocked.Exchange(ref disposedFlag, 0);
                throw new InvalidOperationException("独占事务仍在执行，不能提前释放序列租约。");
            }

            coordinator.ReleaseSequenceLease(this);
            return ValueTask.CompletedTask;
        }
    }
}
