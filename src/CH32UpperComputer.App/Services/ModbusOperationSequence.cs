using CH32UpperComputer.Infrastructure.Transactions;

namespace CH32UpperComputer.App.Services
{
    /// <summary>
    /// 在应用日志、快照和统计路径内包装一个无队列独占 Modbus 事务序列。
    /// </summary>
    public sealed class ModbusOperationSequence : IAsyncDisposable
    {
        /// <summary>
        /// 负责统一记录序列内每项事务的应用操作服务。
        /// </summary>
        private readonly ModbusOperationService operationService;

        /// <summary>
        /// 实际持有协调器唯一活动门的底层租约。
        /// </summary>
        private readonly ModbusTransactionSequenceLease transactionLease;

        /// <summary>
        /// 指示应用操作序列是否已经释放。
        /// </summary>
        private int disposedFlag;

        /// <summary>
        /// 初始化一个只允许操作服务创建的应用事务序列。
        /// </summary>
        /// <param name="operationService">统一记录日志、快照和统计的操作服务。</param>
        /// <param name="transactionLease">持有协调器唯一活动门的底层租约。</param>
        internal ModbusOperationSequence(
            ModbusOperationService operationService,
            ModbusTransactionSequenceLease transactionLease)
        {
            ArgumentNullException.ThrowIfNull(operationService);
            ArgumentNullException.ThrowIfNull(transactionLease);
            this.operationService = operationService;
            this.transactionLease = transactionLease;
        }

        /// <summary>
        /// 在独占序列中执行一项事务，并沿用统一日志、快照和统计路径。
        /// </summary>
        /// <param name="request">需要顺序执行的不可变事务请求。</param>
        /// <param name="cancellationToken">取消当前事务的令牌。</param>
        /// <returns>当前事务的唯一终态或立即拒绝结果。</returns>
        public ValueTask<TransactionExecutionResult> ExecuteAsync(
            TransactionRequest request,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposedFlag) != 0,
                this);
            return operationService.ExecuteSequenceTransactionAsync(
                transactionLease,
                request,
                cancellationToken);
        }

        /// <summary>
        /// 释放协调器唯一活动门并结束应用层活动操作计数。
        /// </summary>
        /// <returns>底层租约释放完成后的值任务。</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
            {
                return;
            }

            await operationService
                .EndSequenceAsync(transactionLease)
                .ConfigureAwait(false);
        }
    }
}
