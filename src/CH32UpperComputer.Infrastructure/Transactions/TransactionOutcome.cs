using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Framing;

namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 表示一项事务最终结果的不可变证据集合。
    /// </summary>
    public sealed class TransactionOutcome
    {
        /// <summary>
        /// 初始化一项已经进入终态的事务结果。
        /// </summary>
        /// <param name="transactionId">协调器分配的正软件事务编号。</param>
        /// <param name="state">不得为 Pending 的最终完成状态。</param>
        /// <param name="mode">本事务采用的标准或原始调试模式。</param>
        /// <param name="completedTimestamp">首个终止事件胜出时的非负单调时间戳。</param>
        /// <param name="message">面向日志和界面的明确结果说明。</param>
        /// <param name="response">标准成功或 Modbus 异常时的结构化响应。</param>
        /// <param name="receivedFrame">产生结果的接收记录；无线路响应的失败状态为空。</param>
        /// <param name="exception">写入、断开或应用生命周期故障的原始异常。</param>
        /// <exception cref="ArgumentOutOfRangeException">编号、状态、模式或时间戳无效时抛出。</exception>
        /// <exception cref="ArgumentException"><paramref name="message"/> 为空时抛出。</exception>
        public TransactionOutcome(
            long transactionId,
            TransactionCompletionState state,
            TransactionMode mode,
            long completedTimestamp,
            string message,
            ModbusResponse? response = null,
            ReceivedFrame? receivedFrame = null,
            Exception? exception = null)
        {
            if (transactionId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(transactionId));
            }

            if (!Enum.IsDefined(state) || state == TransactionCompletionState.Pending)
            {
                throw new ArgumentOutOfRangeException(nameof(state), state, "事务结果必须使用已定义的终态。");
            }

            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (completedTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(completedTimestamp));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("事务结果说明不能为空。", nameof(message));
            }

            ValidateEvidence(state, mode, response, receivedFrame, exception);

            TransactionId = transactionId;
            State = state;
            Mode = mode;
            CompletedTimestamp = completedTimestamp;
            Message = message;
            Response = response;
            ReceivedFrame = receivedFrame;
            Exception = exception;
        }

        /// <summary>
        /// 获取软件内部单调递增的事务编号。
        /// </summary>
        public long TransactionId { get; }

        /// <summary>
        /// 获取唯一原子完成门胜出的终态。
        /// </summary>
        public TransactionCompletionState State { get; }

        /// <summary>
        /// 获取事务采用的标准或原始调试模式。
        /// </summary>
        public TransactionMode Mode { get; }

        /// <summary>
        /// 获取终止事件胜出时的单调时间戳。
        /// </summary>
        public long CompletedTimestamp { get; }

        /// <summary>
        /// 获取面向日志和界面的结果说明。
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 获取标准成功或 Modbus 异常的结构化响应。
        /// </summary>
        public ModbusResponse? Response { get; }

        /// <summary>
        /// 获取产生结果的不可变接收记录。
        /// </summary>
        public ReceivedFrame? ReceivedFrame { get; }

        /// <summary>
        /// 获取写入、断开或生命周期故障的原始异常。
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// 获取原始捕获字节的防御性副本；没有接收记录时为空。
        /// </summary>
        public ReadOnlyMemory<byte> RawData =>
            ReceivedFrame?.Data ?? ReadOnlyMemory<byte>.Empty;

        /// <summary>
        /// 校验终态、事务模式和所携带协议证据之间不存在矛盾。
        /// </summary>
        /// <param name="state">待创建结果的终态。</param>
        /// <param name="mode">待创建结果的事务模式。</param>
        /// <param name="response">可选结构化 Modbus 响应。</param>
        /// <param name="receivedFrame">可选不可变接收记录。</param>
        /// <param name="exception">可选底层异常。</param>
        /// <exception cref="ArgumentException">状态、模式和证据组合不满足契约时抛出。</exception>
        private static void ValidateEvidence(
            TransactionCompletionState state,
            TransactionMode mode,
            ModbusResponse? response,
            ReceivedFrame? receivedFrame,
            Exception? exception)
        {
            switch (state)
            {
                case TransactionCompletionState.Succeeded:
                    if (mode != TransactionMode.Standard ||
                        response?.Status != ModbusResponseStatus.Succeeded ||
                        receivedFrame?.Kind != ReceivedFrameKind.StandardFrame ||
                        !response!.RawFrame.Span.SequenceEqual(receivedFrame!.Data.Span) ||
                        exception is not null)
                    {
                        throw new ArgumentException("Succeeded 必须携带标准模式的成功响应和接收帧。");
                    }

                    break;

                case TransactionCompletionState.RawCaptured:
                    if (mode != TransactionMode.RawDebug ||
                        receivedFrame is null ||
                        receivedFrame.Data.IsEmpty ||
                        response is not null ||
                        exception is not null)
                    {
                        throw new ArgumentException("RawCaptured 必须携带原始模式的非空接收记录且不得携带结构化响应。");
                    }

                    break;

                case TransactionCompletionState.ModbusException:
                    if (mode != TransactionMode.Standard ||
                        response?.Status != ModbusResponseStatus.ModbusException ||
                        receivedFrame?.Kind != ReceivedFrameKind.StandardFrame ||
                        !response!.RawFrame.Span.SequenceEqual(receivedFrame!.Data.Span) ||
                        exception is not null)
                    {
                        throw new ArgumentException("ModbusException 必须携带标准模式的异常响应和接收帧。");
                    }

                    break;

                case TransactionCompletionState.ReceiveOverflow:
                    if (mode != TransactionMode.Standard ||
                        receivedFrame?.Kind != ReceivedFrameKind.ReceiveOverflow ||
                        response is not null ||
                        exception is not null)
                    {
                        throw new ArgumentException("ReceiveOverflow 必须携带标准模式的接收溢出记录。");
                    }

                    break;

                case TransactionCompletionState.WriteFailed:
                    if (response is not null || receivedFrame is not null || exception is null)
                    {
                        throw new ArgumentException("WriteFailed 必须只携带底层写入异常。");
                    }

                    break;

                case TransactionCompletionState.TimedOut:
                case TransactionCompletionState.Cancelled:
                case TransactionCompletionState.ApplicationStopping:
                    if (response is not null || receivedFrame is not null || exception is not null)
                    {
                        throw new ArgumentException($"{state} 不得携带已接受的响应或接收帧。");
                    }

                    break;

                case TransactionCompletionState.Disconnected:
                    if (response is not null || receivedFrame is not null)
                    {
                        throw new ArgumentException("Disconnected 不得携带已接受的响应或接收帧。");
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }
    }

    /// <summary>
    /// 指定事务在进入协调器前被拒绝的原因。
    /// </summary>
    public enum TransactionRejected
    {
        /// <summary>
        /// 请求已被接受，不存在拒绝原因。
        /// </summary>
        None = 0,

        /// <summary>
        /// 当前已有一个事务占用唯一活动门。
        /// </summary>
        Busy = 1,

        /// <summary>
        /// 串口或协调器接收循环尚未连接。
        /// </summary>
        NotConnected = 2,

        /// <summary>
        /// 上一响应可能迟到，协调器正在等待完整静默窗口。
        /// </summary>
        Resynchronizing = 3,

        /// <summary>
        /// 应用正在退出，不再接受新事务。
        /// </summary>
        ApplicationStopping = 4,

        /// <summary>
        /// Ethernet IAP 正在独占应用通信操作。
        /// </summary>
        IapActive = 5,
    }

    /// <summary>
    /// 表示一次提交被接受并最终完成，或在分配 TransactionId 前被立即拒绝。
    /// </summary>
    public sealed class TransactionExecutionResult
    {
        /// <summary>
        /// 初始化一次事务提交结果。
        /// </summary>
        /// <param name="outcome">接受请求的最终事务结果；拒绝时为空。</param>
        /// <param name="rejection">拒绝原因；接受时为 None。</param>
        /// <param name="message">面向调用方的明确说明。</param>
        private TransactionExecutionResult(
            TransactionOutcome? outcome,
            TransactionRejected rejection,
            string message)
        {
            Outcome = outcome;
            Rejection = rejection;
            Message = message;
        }

        /// <summary>
        /// 获取请求是否通过无队列活动门并最终形成事务结果。
        /// </summary>
        public bool IsAccepted => Outcome is not null;

        /// <summary>
        /// 获取已接受请求的最终结果；拒绝时为空。
        /// </summary>
        public TransactionOutcome? Outcome { get; }

        /// <summary>
        /// 获取拒绝原因；接受时为 None。
        /// </summary>
        public TransactionRejected Rejection { get; }

        /// <summary>
        /// 获取面向调用方的提交或拒绝说明。
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 获取接受请求的事务编号；拒绝项始终为空且不会消耗编号。
        /// </summary>
        public long? TransactionId => Outcome?.TransactionId;

        /// <summary>
        /// 创建一项已经接受并完成的提交结果。
        /// </summary>
        /// <param name="outcome">唯一最终事务结果。</param>
        /// <returns>包含事务编号的接受结果。</returns>
        internal static TransactionExecutionResult CreateAccepted(TransactionOutcome outcome)
        {
            ArgumentNullException.ThrowIfNull(outcome);
            return new TransactionExecutionResult(outcome, TransactionRejected.None, outcome.Message);
        }

        /// <summary>
        /// 创建一项未分配事务编号、未写串口的立即拒绝结果。
        /// </summary>
        /// <param name="rejection">不得为 None 的拒绝原因。</param>
        /// <param name="message">面向调用方的拒绝说明。</param>
        /// <returns>不包含 TransactionId 的拒绝结果。</returns>
        internal static TransactionExecutionResult CreateRejected(
            TransactionRejected rejection,
            string message)
        {
            if (!Enum.IsDefined(rejection) || rejection == TransactionRejected.None)
            {
                throw new ArgumentOutOfRangeException(nameof(rejection));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("事务拒绝说明不能为空。", nameof(message));
            }

            return new TransactionExecutionResult(null, rejection, message);
        }
    }
}
