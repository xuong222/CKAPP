using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Core.Registers;
using CH32UpperComputer.Infrastructure.Logging;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Transactions;

namespace CH32UpperComputer.App.Services
{
    /// <summary>
    /// 保存应用会话内的发送、成功、失败和最近响应耗时统计。
    /// </summary>
    public sealed class OperationMetrics
    {
        /// <summary>
        /// 初始化一份不可变通信统计快照。
        /// </summary>
        /// <param name="sendCount">实际被协调器接受的请求总数。</param>
        /// <param name="successCount">以成功标准响应或原始捕获结束的请求总数。</param>
        /// <param name="failureCount">以异常、超时、取消、断开或其他失败终态结束的请求总数。</param>
        /// <param name="lastSendTime">最近一个被接受请求的日历时间。</param>
        /// <param name="lastResponseDuration">最近一个被接受请求从提交到终态的耗时。</param>
        public OperationMetrics(
            long sendCount,
            long successCount,
            long failureCount,
            DateTimeOffset? lastSendTime,
            TimeSpan? lastResponseDuration)
        {
            SendCount = sendCount;
            SuccessCount = successCount;
            FailureCount = failureCount;
            LastSendTime = lastSendTime;
            LastResponseDuration = lastResponseDuration;
        }

        /// <summary>
        /// 获取实际被协调器接受的请求总数。
        /// </summary>
        public long SendCount { get; }

        /// <summary>
        /// 获取成功标准响应或原始捕获总数。
        /// </summary>
        public long SuccessCount { get; }

        /// <summary>
        /// 获取失败、超时或取消终态总数。
        /// </summary>
        public long FailureCount { get; }

        /// <summary>
        /// 获取最近一个被接受请求的发送时间。
        /// </summary>
        public DateTimeOffset? LastSendTime { get; }

        /// <summary>
        /// 获取最近一个被接受请求的完整事务耗时。
        /// </summary>
        public TimeSpan? LastResponseDuration { get; }
    }

    /// <summary>
    /// 统一执行应用层手动标准或原始请求，并集中完成日志、统计和数据快照更新。
    /// </summary>
    public sealed class ModbusOperationService : IDisposable
    {
        /// <summary>
        /// 保护会话统计快照的一致性。
        /// </summary>
        private readonly object metricsSyncRoot = new();

        /// <summary>
        /// 保护活动应用操作计数和退出排空任务代次的一致性。
        /// </summary>
        private readonly object operationSyncRoot = new();

        /// <summary>
        /// 所有请求共用的无队列 Modbus 事务协调器。
        /// </summary>
        private readonly ModbusTransactionCoordinator coordinator;

        /// <summary>
        /// 提供当前串口代次和连接状态的抽象传输。
        /// </summary>
        private readonly ISerialTransport transport;

        /// <summary>
        /// 保存最近一次有效寄存器数据的线程安全快照。
        /// </summary>
        private readonly DeviceSnapshot deviceSnapshot;

        /// <summary>
        /// 保存完整 TX、RX 和错误记录的有界日志服务。
        /// </summary>
        private readonly CommunicationLogService logService;

        /// <summary>
        /// 为耗时和日历日志提供统一时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 当前尚未完成日志、快照、统计和观察者发布的应用操作数量。
        /// </summary>
        private int activeOperationCount;

        /// <summary>
        /// 当前操作代次全部排空时完成的异步信号；初始状态已经完成。
        /// </summary>
        private TaskCompletionSource operationIdleSource = CreateCompletedSource();

        /// <summary>
        /// 当前已被接受请求数量。
        /// </summary>
        private long sendCount;

        /// <summary>
        /// 当前成功请求数量。
        /// </summary>
        private long successCount;

        /// <summary>
        /// 当前失败请求数量。
        /// </summary>
        private long failureCount;

        /// <summary>
        /// 最近一次被接受请求的发送时间。
        /// </summary>
        private DateTimeOffset? lastSendTime;

        /// <summary>
        /// 最近一次被接受请求的完整事务耗时。
        /// </summary>
        private TimeSpan? lastResponseDuration;

        /// <summary>
        /// 指示线路诊断事件订阅已经解除。
        /// </summary>
        private int disposedFlag;

        /// <summary>
        /// 初始化统一的应用层 Modbus 操作服务。
        /// </summary>
        /// <param name="coordinator">所有请求共用的无队列事务协调器。</param>
        /// <param name="transport">提供端口代次的抽象串口传输。</param>
        /// <param name="deviceSnapshot">仅由严格成功 0x03 响应更新的数据快照。</param>
        /// <param name="logService">保存完整 TX、RX 和错误记录的日志服务。</param>
        /// <param name="timeProvider">生产使用系统时间、测试可替换的统一时间源。</param>
        public ModbusOperationService(
            ModbusTransactionCoordinator coordinator,
            ISerialTransport transport,
            DeviceSnapshot deviceSnapshot,
            CommunicationLogService logService,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(coordinator);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(deviceSnapshot);
            ArgumentNullException.ThrowIfNull(logService);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.coordinator = coordinator;
            this.transport = transport;
            this.deviceSnapshot = deviceSnapshot;
            this.logService = logService;
            this.timeProvider = timeProvider;
            coordinator.DiagnosticRecorded += HandleDiagnosticRecorded;
        }

        /// <summary>
        /// 在有效 0x03 响应完成原子快照更新后发布通知。
        /// </summary>
        public event Action? SnapshotUpdated;

        /// <summary>
        /// 在一次提交被拒绝或接受事务进入终态后发布完整结果。
        /// </summary>
        public event Action<TransactionExecutionResult>? OperationCompleted;

        /// <summary>
        /// 在一次提交被拒绝或接受事务进入终态后，同时发布其原始请求和完整结果。
        /// </summary>
        public event Action<TransactionRequest, TransactionExecutionResult>? OperationRecorded;

        /// <summary>
        /// 在被接受事务更新发送统计后发布不可变统计快照。
        /// </summary>
        public event Action<OperationMetrics>? MetricsChanged;

        /// <summary>
        /// 获取当前会话通信统计的不可变副本。
        /// </summary>
        public OperationMetrics Metrics
        {
            get
            {
                lock (metricsSyncRoot)
                {
                    return CreateMetricsUnderLock();
                }
            }
        }

        /// <summary>
        /// 获取由成功标准响应维护的设备数据快照。
        /// </summary>
        public DeviceSnapshot DeviceSnapshot => deviceSnapshot;

        /// <summary>
        /// 解除协调器线路诊断订阅；通信服务的其余资源由应用组合根分别释放。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposedFlag, 1) != 0)
            {
                return;
            }

            coordinator.DiagnosticRecorded -= HandleDiagnosticRecorded;
        }

        /// <summary>
        /// 尝试立即执行一个标准或原始事务，不建立任何等待队列。
        /// </summary>
        /// <param name="request">已经完成帧、模式和超时校验的不可变事务请求。</param>
        /// <param name="cancellationToken">取消当前被接受请求的令牌。</param>
        /// <returns>Busy 等立即拒绝或唯一终态结果。</returns>
        public async ValueTask<TransactionExecutionResult> ExecuteAsync(
            TransactionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            EnterOperation();

            try
            {
                return await ExecuteCoreAsync(
                    request,
                    cancellationToken,
                    coordinator.TryExecuteAsync).ConfigureAwait(false);
            }
            finally
            {
                LeaveOperation();
            }
        }

        /// <summary>
        /// 尝试立即创建一个跨多项事务保持协调器唯一活动门的应用操作序列。
        /// </summary>
        /// <returns>成功时返回独占序列；Busy、断开、重同步或退出时返回空值。</returns>
        public ModbusOperationSequence? TryBeginSequence()
        {
            ModbusTransactionSequenceLease? transactionLease =
                coordinator.TryAcquireSequence();

            if (transactionLease is null)
            {
                return null;
            }

            EnterOperation();
            return new ModbusOperationSequence(this, transactionLease);
        }

        /// <summary>
        /// 在指定底层独占租约中执行事务，并记录日志、快照和统计。
        /// </summary>
        /// <param name="transactionLease">当前应用序列持有的协调器租约。</param>
        /// <param name="request">需要顺序执行的不可变事务请求。</param>
        /// <param name="cancellationToken">取消当前事务的令牌。</param>
        /// <returns>当前事务的唯一终态或立即拒绝结果。</returns>
        internal ValueTask<TransactionExecutionResult> ExecuteSequenceTransactionAsync(
            ModbusTransactionSequenceLease transactionLease,
            TransactionRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transactionLease);
            return ExecuteCoreAsync(
                request,
                cancellationToken,
                transactionLease.ExecuteAsync);
        }

        /// <summary>
        /// 释放底层独占租约，并保证应用活动操作计数只离开一次。
        /// </summary>
        /// <param name="transactionLease">需要释放的协调器独占租约。</param>
        /// <returns>底层租约释放完成后的值任务。</returns>
        internal async ValueTask EndSequenceAsync(
            ModbusTransactionSequenceLease transactionLease)
        {
            ArgumentNullException.ThrowIfNull(transactionLease);

            try
            {
                await transactionLease.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                LeaveOperation();
            }
        }

        /// <summary>
        /// 执行一项普通或序列内事务，并复用唯一日志、快照、统计和观察者路径。
        /// </summary>
        /// <param name="request">已经完成校验的不可变事务请求。</param>
        /// <param name="cancellationToken">取消当前事务的令牌。</param>
        /// <param name="executeTransaction">普通协调器或独占序列的实际执行入口。</param>
        /// <returns>事务唯一终态或立即拒绝结果。</returns>
        private async ValueTask<TransactionExecutionResult> ExecuteCoreAsync(
            TransactionRequest request,
            CancellationToken cancellationToken,
            Func<TransactionRequest, CancellationToken, ValueTask<TransactionExecutionResult>>
                executeTransaction)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(executeTransaction);
            long startedTimestamp = timeProvider.GetTimestamp();
            DateTimeOffset startedAt = timeProvider.GetUtcNow();
            TransactionExecutionResult result = await executeTransaction(
                request,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsAccepted)
            {
                InvokeObservers(OperationCompleted, result);
                InvokeObservers(OperationRecorded, request, result);
                return result;
            }

            TimeSpan elapsed = timeProvider.GetElapsedTime(startedTimestamp);
            RecordAcceptedResult(request, result, startedAt, elapsed);
            return result;
        }

        /// <summary>
        /// 等待所有已经进入应用操作服务的请求完成日志、快照、统计和观察者发布。
        /// </summary>
        /// <param name="cancellationToken">取消调用方等待，但不取消正在收敛的通信操作。</param>
        /// <returns>当前活动操作计数归零后的任务。</returns>
        public async ValueTask WaitForOperationsAsync(CancellationToken cancellationToken)
        {
            Task completion;

            lock (operationSyncRoot)
            {
                completion = operationIdleSource.Task;
            }

            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 将定时服务已经通过同一协调器完成的结果纳入统一日志、统计和设备快照路径。
        /// </summary>
        /// <param name="request">定时服务当前代次实际使用的不可变事务请求。</param>
        /// <param name="result">定时服务发布的 Busy 拒绝或唯一事务终态。</param>
        public void RecordScheduledResult(
            TransactionRequest request,
            TransactionExecutionResult result)
        {
            RecordExternalResult(request, result);
        }

        /// <summary>
        /// 将专用配置等已经通过同一协调器完成的外部结果纳入统一日志、统计和界面投影。
        /// </summary>
        /// <param name="request">外部流程实际提交的不可变事务请求。</param>
        /// <param name="result">外部流程收到的 Busy 拒绝或唯一事务终态。</param>
        public void RecordExternalResult(
            TransactionRequest request,
            TransactionExecutionResult result)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(result);

            if (!result.IsAccepted)
            {
                InvokeObservers(OperationCompleted, result);
                InvokeObservers(OperationRecorded, request, result);
                return;
            }

            RecordAcceptedResult(
                request,
                result,
                timeProvider.GetUtcNow(),
                TimeSpan.Zero);
        }

        /// <summary>
        /// 对一个已被协调器接受的结果执行唯一日志、快照、统计和观察者发布路径。
        /// </summary>
        /// <param name="request">实际发送的标准或原始事务请求。</param>
        /// <param name="result">必须包含唯一终态的接受结果。</param>
        /// <param name="startedAt">用于发送日志和最近发送时间的日历时间。</param>
        /// <param name="elapsed">从提交到终态的可用耗时；定时外部结果无法恢复时为零。</param>
        private void RecordAcceptedResult(
            TransactionRequest request,
            TransactionExecutionResult result,
            DateTimeOffset startedAt,
            TimeSpan elapsed)
        {
            TransactionOutcome outcome = result.Outcome ??
                throw new ArgumentException("接受结果必须携带唯一事务终态。", nameof(result));
            AppendTransactionLogs(request, outcome, startedAt);
            bool snapshotChanged = TryApplySnapshot(request, outcome);
            OperationMetrics metrics = UpdateMetrics(outcome, startedAt, elapsed);

            if (snapshotChanged)
            {
                InvokeObservers(SnapshotUpdated);
            }

            InvokeObservers(MetricsChanged, metrics);
            InvokeObservers(OperationCompleted, result);
            InvokeObservers(OperationRecorded, request, result);
        }

        /// <summary>
        /// 为被接受事务追加一条完整发送日志，以及一条接收或错误日志。
        /// </summary>
        /// <param name="request">包含实际发送线路帧的事务请求。</param>
        /// <param name="outcome">协调器产生的唯一最终事务结果。</param>
        /// <param name="startedAt">提交请求时的日历时间。</param>
        private void AppendTransactionLogs(
            TransactionRequest request,
            TransactionOutcome outcome,
            DateTimeOffset startedAt)
        {
            logService.Append(
                outcome.TransactionId,
                CommunicationDirection.Transmit,
                outcome.State,
                transport.PortGeneration,
                0,
                request.Frame.Span,
                $"TX #{outcome.TransactionId} · {request.CreateSignatureSummary()}",
                startedAt);

            if (outcome.ReceivedFrame is not null)
            {
                logService.Append(
                    outcome.TransactionId,
                    CommunicationDirection.Receive,
                    outcome.State,
                    outcome.ReceivedFrame.PortGeneration,
                    outcome.ReceivedFrame.ReceiveSequence,
                    outcome.RawData.Span,
                    $"RX #{outcome.TransactionId} · {outcome.Message}");
                return;
            }

            logService.Append(
                outcome.TransactionId,
                CommunicationDirection.Error,
                outcome.State,
                transport.PortGeneration,
                0,
                ReadOnlySpan<byte>.Empty,
                $"事务 #{outcome.TransactionId} · {outcome.Message}");
        }

        /// <summary>
        /// 将带原始字节的迟到、无归属和签名不匹配诊断接入统一通信日志。
        /// </summary>
        /// <param name="diagnostic">协调器已经完成分类和有界缓存的不可变诊断。</param>
        private void HandleDiagnosticRecorded(TransactionDiagnostic diagnostic)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);

            if (diagnostic.Data.IsEmpty ||
                diagnostic.Kind is not (
                    TransactionDiagnosticKind.MismatchedResponse or
                    TransactionDiagnosticKind.Unsolicited or
                    TransactionDiagnosticKind.LateOrUnsolicited))
            {
                return;
            }

            ReadOnlyMemory<byte> data = diagnostic.Data;
            logService.Append(
                diagnostic.TransactionId,
                CommunicationDirection.LateOrUnsolicited,
                null,
                diagnostic.PortGeneration,
                diagnostic.ReceiveSequence,
                data.Span,
                $"线路诊断 · {diagnostic.Message}");
        }

        /// <summary>
        /// 仅将严格成功且属于当前标准 0x03 请求的结构化响应应用到设备快照。
        /// </summary>
        /// <param name="request">本次事务实际发送的标准或原始请求。</param>
        /// <param name="outcome">协调器产生的唯一最终结果。</param>
        /// <returns>快照已经完成原子更新时返回 <see langword="true"/>。</returns>
        private bool TryApplySnapshot(
            TransactionRequest request,
            TransactionOutcome outcome)
        {
            if (outcome.State != TransactionCompletionState.Succeeded ||
                request.StandardRequest is null ||
                request.StandardRequest.FunctionCode != ModbusFunctionCode.ReadHoldingRegisters ||
                request.StandardRequest.IsUnknownAddressQuery ||
                outcome.Response is null)
            {
                return false;
            }

            SnapshotApplyResult applyResult = deviceSnapshot.Apply(
                request.StandardRequest,
                outcome.Response,
                timeProvider.GetUtcNow());

            if (!applyResult.IsSuccess)
            {
                logService.Append(
                    outcome.TransactionId,
                    CommunicationDirection.Error,
                    outcome.State,
                    transport.PortGeneration,
                    outcome.ReceivedFrame?.ReceiveSequence ?? 0,
                    outcome.RawData.Span,
                    $"快照拒绝更新：{applyResult.ErrorMessage}");
            }

            return applyResult.IsSuccess;
        }

        /// <summary>
        /// 在同步门内更新会话统计并返回独立快照。
        /// </summary>
        /// <param name="outcome">已经进入唯一终态的事务结果。</param>
        /// <param name="startedAt">事务提交时的日历时间。</param>
        /// <param name="elapsed">事务从提交到终态的耗时。</param>
        /// <returns>更新后的不可变统计快照。</returns>
        private OperationMetrics UpdateMetrics(
            TransactionOutcome outcome,
            DateTimeOffset startedAt,
            TimeSpan elapsed)
        {
            lock (metricsSyncRoot)
            {
                sendCount++;

                if (outcome.State is TransactionCompletionState.Succeeded or TransactionCompletionState.RawCaptured)
                {
                    successCount++;
                }
                else
                {
                    failureCount++;
                }

                lastSendTime = startedAt;
                lastResponseDuration = elapsed;
                return CreateMetricsUnderLock();
            }
        }

        /// <summary>
        /// 在已持有统计同步门时创建不可变统计快照。
        /// </summary>
        /// <returns>当前计数和最近时间的独立快照。</returns>
        private OperationMetrics CreateMetricsUnderLock()
        {
            return new OperationMetrics(
                sendCount,
                successCount,
                failureCount,
                lastSendTime,
                lastResponseDuration);
        }

        /// <summary>
        /// 逐个调用无参数观察者并隔离单个界面观察者异常。
        /// </summary>
        /// <param name="observers">需要调用的可空多播委托。</param>
        private static void InvokeObservers(Action? observers)
        {
            if (observers is null)
            {
                return;
            }

            foreach (Action observer in observers.GetInvocationList().Cast<Action>())
            {
                try
                {
                    observer();
                }
                catch (Exception)
                {
                    // 单个界面观察者错误不得破坏通信事务完成路径。
                }
            }
        }

        /// <summary>
        /// 逐个调用单参数观察者并隔离单个界面观察者异常。
        /// </summary>
        /// <typeparam name="TValue">发布的不可变值类型。</typeparam>
        /// <param name="observers">需要调用的可空多播委托。</param>
        /// <param name="value">发送给每个观察者的同一不可变值。</param>
        private static void InvokeObservers<TValue>(
            Action<TValue>? observers,
            TValue value)
        {
            if (observers is null)
            {
                return;
            }

            foreach (Action<TValue> observer in observers.GetInvocationList().Cast<Action<TValue>>())
            {
                try
                {
                    observer(value);
                }
                catch (Exception)
                {
                    // 单个界面观察者错误不得破坏通信事务完成路径。
                }
            }
        }

        /// <summary>
        /// 逐个调用双参数观察者并隔离单个界面观察者异常。
        /// </summary>
        /// <typeparam name="TFirst">第一个不可变发布值类型。</typeparam>
        /// <typeparam name="TSecond">第二个不可变发布值类型。</typeparam>
        /// <param name="observers">需要调用的可空多播委托。</param>
        /// <param name="first">发送给每个观察者的第一个值。</param>
        /// <param name="second">发送给每个观察者的第二个值。</param>
        private static void InvokeObservers<TFirst, TSecond>(
            Action<TFirst, TSecond>? observers,
            TFirst first,
            TSecond second)
        {
            if (observers is null)
            {
                return;
            }

            foreach (Action<TFirst, TSecond> observer in
                observers.GetInvocationList().Cast<Action<TFirst, TSecond>>())
            {
                try
                {
                    observer(first, second);
                }
                catch (Exception)
                {
                    // 单个界面观察者错误不得破坏通信事务完成路径。
                }
            }
        }

        /// <summary>
        /// 进入一次包含协调器执行及全部应用副作用的操作生命周期。
        /// </summary>
        private void EnterOperation()
        {
            lock (operationSyncRoot)
            {
                if (activeOperationCount == 0)
                {
                    operationIdleSource = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                activeOperationCount = checked(activeOperationCount + 1);
            }
        }

        /// <summary>
        /// 退出一次应用操作，并在最后一项退出时完成当前排空代次。
        /// </summary>
        private void LeaveOperation()
        {
            TaskCompletionSource? completedSource = null;

            lock (operationSyncRoot)
            {
                activeOperationCount--;

                if (activeOperationCount < 0)
                {
                    throw new InvalidOperationException("应用操作计数不能为负数。");
                }

                if (activeOperationCount == 0)
                {
                    completedSource = operationIdleSource;
                }
            }

            completedSource?.TrySetResult();
        }

        /// <summary>
        /// 创建一个已经完成的排空信号，表示服务初始化时没有活动操作。
        /// </summary>
        /// <returns>已经进入 RanToCompletion 状态的新完成源。</returns>
        private static TaskCompletionSource CreateCompletedSource()
        {
            TaskCompletionSource source = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
