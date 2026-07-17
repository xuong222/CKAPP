using System.Collections.ObjectModel;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Framing
{
    /// <summary>
    /// 把有序串口接收块组装为有界、可诊断的 Modbus RTU 接收记录。
    /// </summary>
    /// <remarks>
    /// 本状态机不产生逐字节通知。调用方应串行调用开始、追加和观察时间方法，
    /// 并把每次返回的零至多项记录批量提交给日志或事务协调器。
    /// </remarks>
    public sealed class RtuReceiveFramer
    {
        /// <summary>
        /// 保护捕获生命周期和所有缓冲状态。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 保存尚未完成组帧的有界字节。
        /// </summary>
        private readonly byte[] receiveBuffer;

        /// <summary>
        /// 为每个缓冲字节保存其来源传输块序号。
        /// </summary>
        private readonly long[] receiveSequences;

        /// <summary>
        /// 为每个缓冲字节保存其来源块单调到达时间戳。
        /// </summary>
        private readonly long[] receiveTimestamps;

        /// <summary>
        /// 当前状态机使用的不可变容量与超时参数。
        /// </summary>
        private readonly RtuFramerOptions options;

        /// <summary>
        /// 当前缓冲区内有效字节数量。
        /// </summary>
        private int bufferedByteCount;

        /// <summary>
        /// 当前捕获采用的边界识别模式。
        /// </summary>
        private RtuFramingMode framingMode;

        /// <summary>
        /// 当前捕获绑定的串口会话代次。
        /// </summary>
        private int portGeneration;

        /// <summary>
        /// 当前响应窗口开始时的单调时间戳。
        /// </summary>
        private long responseStartedTimestamp;

        /// <summary>
        /// 最近一个已接受或正在丢弃的线路字节到达时的单调时间戳；时间轮询不得修改该基线。
        /// </summary>
        private long lastByteTimestamp;

        /// <summary>
        /// 最近一次被状态机接受或观察到的单调时间戳。
        /// </summary>
        private long lastObservedTimestamp;

        /// <summary>
        /// 最近一次被状态机接受的传输块序号。
        /// </summary>
        private long lastAcceptedReceiveSequence;

        /// <summary>
        /// 当前捕获内最近分配的输出记录序号。
        /// </summary>
        private long frameSequence;

        /// <summary>
        /// 溢出或结构错误后已经丢弃的总字节数，仅用于诊断状态。
        /// </summary>
        private int discardedByteCount;

        /// <summary>
        /// 指示捕获是否已经通过 <see cref="StartCapture"/> 启动。
        /// </summary>
        private bool isCaptureActive;

        /// <summary>
        /// 指示状态机是否正在丢弃输入，直至出现足够长的字节间静默。
        /// </summary>
        private bool isDiscardingUntilSilence;

        /// <summary>
        /// 指示固定响应总截止时间是否已经到达；截止后的数据只保留为诊断原始记录。
        /// </summary>
        private bool isResponseDeadlineExpired;

        /// <summary>
        /// 初始化使用指定有界参数的 RTU 接收状态机。
        /// </summary>
        /// <param name="options">经过完整校验的容量、超时和时间源。</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> 为空时抛出。</exception>
        public RtuReceiveFramer(RtuFramerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            this.options = options;
            receiveBuffer = new byte[options.ReceiveBufferBytes];
            receiveSequences = new long[options.ReceiveBufferBytes];
            receiveTimestamps = new long[options.ReceiveBufferBytes];
        }

        /// <summary>
        /// 开始一轮全新的标准或原始调试接收捕获，并清除上一轮全部状态。
        /// </summary>
        /// <param name="mode">本轮使用的组帧模式。</param>
        /// <param name="portGeneration">本轮绑定的非负串口会话代次。</param>
        /// <param name="startedTimestamp">由配置时间源取得的非负单调起始时间戳。</param>
        /// <exception cref="ArgumentOutOfRangeException">模式、代次或时间戳无效时抛出。</exception>
        public void StartCapture(
            RtuFramingMode mode,
            int portGeneration,
            long startedTimestamp)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "组帧模式必须是已定义值。");
            }

            if (portGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(portGeneration));
            }

            if (startedTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startedTimestamp));
            }

            lock (syncRoot)
            {
                ClearBufferUnderLock();
                framingMode = mode;
                this.portGeneration = portGeneration;
                responseStartedTimestamp = startedTimestamp;
                lastByteTimestamp = startedTimestamp;
                lastObservedTimestamp = startedTimestamp;
                lastAcceptedReceiveSequence = 0;
                frameSequence = 0;
                discardedByteCount = 0;
                isDiscardingUntilSilence = false;
                isResponseDeadlineExpired = false;
                isCaptureActive = true;
            }
        }

        /// <summary>
        /// 停止当前捕获并清除所有残片、溢出丢弃态和会话身份。
        /// </summary>
        public void Reset()
        {
            lock (syncRoot)
            {
                ClearBufferUnderLock();
                framingMode = default;
                portGeneration = 0;
                responseStartedTimestamp = 0;
                lastByteTimestamp = 0;
                lastObservedTimestamp = 0;
                lastAcceptedReceiveSequence = 0;
                frameSequence = 0;
                discardedByteCount = 0;
                isDiscardingUntilSilence = false;
                isResponseDeadlineExpired = false;
                isCaptureActive = false;
            }
        }

        /// <summary>
        /// 追加一个完整传输层接收块，并返回本次形成的全部独立记录。
        /// </summary>
        /// <param name="chunk">具有会话代次、递增序号和单调到达时间的非空接收块。</param>
        /// <returns>按形成顺序排列的零至多项不可变记录。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="chunk"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">尚未开始捕获时抛出。</exception>
        public IReadOnlyList<ReceivedFrame> Append(SerialReceiveChunk chunk)
        {
            ArgumentNullException.ThrowIfNull(chunk);

            lock (syncRoot)
            {
                EnsureCaptureActiveUnderLock();

                if (chunk.PortGeneration != portGeneration)
                {
                    return SingleResult(CreateRejectedChunkErrorUnderLock(
                        chunk,
                        $"接收块端口代次 {chunk.PortGeneration} 与当前捕获代次 {portGeneration} 不一致。"));
                }

                if (chunk.MonotonicTimestamp < lastObservedTimestamp)
                {
                    return SingleResult(CreateRejectedChunkErrorUnderLock(
                        chunk,
                        "接收块单调时间戳早于当前状态机时间，已拒绝且未污染捕获。"));
                }

                if (chunk.ReceiveSequence <= lastAcceptedReceiveSequence)
                {
                    return SingleResult(CreateRejectedChunkErrorUnderLock(
                        chunk,
                        "接收块序号没有严格递增，已拒绝且未污染捕获。"));
                }

                List<ReceivedFrame> results = [];
                ObserveTimeUnderLock(chunk.MonotonicTimestamp, results);

                lastObservedTimestamp = chunk.MonotonicTimestamp;
                lastAcceptedReceiveSequence = chunk.ReceiveSequence;
                lastByteTimestamp = chunk.MonotonicTimestamp;
                ReadOnlyMemory<byte> chunkData = chunk.Data;

                if (isDiscardingUntilSilence)
                {
                    discardedByteCount = SaturatingAdd(discardedByteCount, chunkData.Length);
                    return AsReadOnly(results);
                }

                int sourceOffset = 0;

                while (sourceOffset < chunkData.Length && !isDiscardingUntilSilence)
                {
                    receiveBuffer[bufferedByteCount] = chunkData.Span[sourceOffset];
                    receiveSequences[bufferedByteCount] = chunk.ReceiveSequence;
                    receiveTimestamps[bufferedByteCount] = chunk.MonotonicTimestamp;
                    bufferedByteCount++;
                    sourceOffset++;

                    DrainCompleteFramesUnderLock(results, chunk.MonotonicTimestamp);

                    int captureLimit = framingMode == RtuFramingMode.RawDebug
                        ? Math.Min(options.ReceiveBufferBytes, options.RawCaptureMaxBytes)
                        : options.ReceiveBufferBytes;

                    if (!isDiscardingUntilSilence && bufferedByteCount >= captureLimit)
                    {
                        int remainingInChunk = chunkData.Length - sourceOffset;
                        results.Add(CreateOverflowUnderLock(
                            chunk.MonotonicTimestamp,
                            remainingInChunk));
                        sourceOffset = chunkData.Length;
                    }
                }

                if (isResponseDeadlineExpired &&
                    !isDiscardingUntilSilence &&
                    bufferedByteCount > 0)
                {
                    results.Add(CreateBufferedFrameUnderLock(
                        ReceivedFrameKind.UnparsedRawCapture,
                        bufferedByteCount,
                        chunk.MonotonicTimestamp,
                        ModbusCrc16.IsValid(receiveBuffer.AsSpan(0, bufferedByteCount)),
                        "响应总截止时间后到达的字节，仅保留原始诊断且不参与事务匹配。"));
                    RemovePrefixUnderLock(bufferedByteCount);
                }

                return AsReadOnly(results);
            }
        }

        /// <summary>
        /// 通知状态机单调时间已经推进，以便处理静默、总超时或丢弃态复位。
        /// </summary>
        /// <param name="nowTimestamp">由配置时间源取得的当前非负单调时间戳。</param>
        /// <returns>本次时间推进形成的零至多项不可变记录。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nowTimestamp"/> 为负数时抛出。</exception>
        /// <exception cref="InvalidOperationException">尚未开始捕获时抛出。</exception>
        public IReadOnlyList<ReceivedFrame> ObserveTime(long nowTimestamp)
        {
            if (nowTimestamp < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowTimestamp));
            }

            lock (syncRoot)
            {
                EnsureCaptureActiveUnderLock();

                if (nowTimestamp < lastObservedTimestamp)
                {
                    return SingleResult(CreateProtocolErrorUnderLock(
                        ReadOnlySpan<byte>.Empty,
                        0,
                        0,
                        lastObservedTimestamp,
                        lastObservedTimestamp,
                        "观察时间发生倒退，状态机保持原状态。",
                        0));
                }

                List<ReceivedFrame> results = [];
                ObserveTimeUnderLock(nowTimestamp, results);
                lastObservedTimestamp = nowTimestamp;
                return AsReadOnly(results);
            }
        }

        /// <summary>
        /// 在已持有同步门时根据时间间隔完成残片或解除丢弃状态。
        /// </summary>
        /// <param name="nowTimestamp">当前单调时间戳。</param>
        /// <param name="results">接收本次形成记录的结果集合。</param>
        private void ObserveTimeUnderLock(
            long nowTimestamp,
            List<ReceivedFrame> results)
        {
            TimeSpan sinceLastByte = options.TimeProvider.GetElapsedTime(
                lastByteTimestamp,
                nowTimestamp);
            TimeSpan totalElapsed = options.TimeProvider.GetElapsedTime(
                responseStartedTimestamp,
                nowTimestamp);
            bool isResponseTimeout = totalElapsed >= options.ResponseTimeout;

            if (isResponseTimeout)
            {
                isResponseDeadlineExpired = true;
            }

            if (isDiscardingUntilSilence)
            {
                if (sinceLastByte >= options.RawInterByteTimeout)
                {
                    ClearBufferUnderLock();
                    discardedByteCount = 0;
                    isDiscardingUntilSilence = false;
                }

                return;
            }

            bool isRawSilence = framingMode == RtuFramingMode.RawDebug &&
                bufferedByteCount > 0 &&
                sinceLastByte >= options.RawInterByteTimeout;

            if (bufferedByteCount > 0 && (isRawSilence || isResponseTimeout))
            {
                string reason = isRawSilence
                    ? "原始捕获因字节间静默结束，未识别为标准帧或有效 CRC 帧。"
                    : "接收残片因响应总超时结束，不能作为成功响应。";
                results.Add(CreateBufferedFrameUnderLock(
                    ReceivedFrameKind.UnparsedRawCapture,
                    bufferedByteCount,
                    nowTimestamp,
                    false,
                    reason));
                RemovePrefixUnderLock(bufferedByteCount);
            }
        }

        /// <summary>
        /// 反复从缓冲头拆出所有已完整的标准帧、CRC 帧或前导噪声。
        /// </summary>
        /// <param name="results">接收拆帧结果的集合。</param>
        /// <param name="completedTimestamp">本批输入的单调完成时间戳。</param>
        private void DrainCompleteFramesUnderLock(
            List<ReceivedFrame> results,
            long completedTimestamp)
        {
            bool madeProgress;

            do
            {
                madeProgress = false;
                ExpectedLengthResult expected = GetExpectedLength(receiveBuffer, 0, bufferedByteCount);

                if (expected.Status == ExpectedLengthStatus.Invalid)
                {
                    results.Add(CreateProtocolErrorUnderLock(
                        receiveBuffer.AsSpan(0, bufferedByteCount),
                        bufferedByteCount == 0 ? 0 : receiveSequences[0],
                        bufferedByteCount == 0 ? 0 : receiveSequences[bufferedByteCount - 1],
                        bufferedByteCount == 0 ? responseStartedTimestamp : receiveTimestamps[0],
                        completedTimestamp,
                        $"结构推导长度 {expected.Length} 超过允许的 {options.MaximumFrameBytes} 字节。",
                        bufferedByteCount));
                    ClearBufferUnderLock();
                    isDiscardingUntilSilence = true;
                    discardedByteCount = 0;
                    return;
                }

                if (expected.Status == ExpectedLengthStatus.Known &&
                    bufferedByteCount >= expected.Length)
                {
                    bool isCrcValid = ModbusCrc16.IsValid(
                        receiveBuffer.AsSpan(0, expected.Length));
                    ReceivedFrameKind kind = isResponseDeadlineExpired
                        ? ReceivedFrameKind.UnparsedRawCapture
                        : ReceivedFrameKind.StandardFrame;
                    string diagnostic = isResponseDeadlineExpired
                        ? "结构完整但在响应总截止时间后到达，仅保留原始诊断。"
                        : isCrcValid
                            ? string.Empty
                            : "结构完整，但 Modbus CRC 校验失败。";
                    results.Add(CreateBufferedFrameUnderLock(
                        kind,
                        expected.Length,
                        completedTimestamp,
                        isCrcValid,
                        diagnostic));
                    RemovePrefixUnderLock(expected.Length);
                    madeProgress = true;
                    continue;
                }

                if (framingMode != RtuFramingMode.RawDebug)
                {
                    continue;
                }

                if (expected.Status == ExpectedLengthStatus.Unknown)
                {
                    int crcBoundary = FindEarliestCrcBoundaryUnderLock();

                    if (crcBoundary > 0)
                    {
                        results.Add(CreateBufferedFrameUnderLock(
                            isResponseDeadlineExpired
                                ? ReceivedFrameKind.UnparsedRawCapture
                                : ReceivedFrameKind.RawCrcFrame,
                            crcBoundary,
                            completedTimestamp,
                            true,
                            isResponseDeadlineExpired
                                ? "未知结构 CRC 正确但在响应总截止时间后到达，仅保留原始诊断。"
                                : "未知结构通过最早有效 CRC 边界完成。"));
                        RemovePrefixUnderLock(crcBoundary);
                        madeProgress = true;
                        continue;
                    }

                    int knownFrameOffset = FindLaterKnownValidFrameOffsetUnderLock();

                    if (knownFrameOffset > 0)
                    {
                        results.Add(CreateBufferedFrameUnderLock(
                            ReceivedFrameKind.UnparsedRawCapture,
                            knownFrameOffset,
                            completedTimestamp,
                            false,
                            "合法结构帧之前的原始噪声。"));
                        RemovePrefixUnderLock(knownFrameOffset);
                        madeProgress = true;
                    }
                }
            }
            while (madeProgress && !isDiscardingUntilSilence);
        }

        /// <summary>
        /// 检查未知结构缓冲头是否恰好在本次新增字节处形成有效 Modbus CRC 边界。
        /// 本方法由逐字节追加路径调用，较短候选已经在其自身末字节到达时检查过，
        /// 因此不得重复扫描全部历史长度，否则 4096 字节噪声会退化为高开销重复计算。
        /// </summary>
        /// <returns>当前完整缓冲长度是有效边界时返回该长度；否则返回零。</returns>
        private int FindEarliestCrcBoundaryUnderLock()
        {
            if (bufferedByteCount is < 4 || bufferedByteCount > options.MaximumFrameBytes)
            {
                return 0;
            }

            return ModbusCrc16.IsValid(receiveBuffer.AsSpan(0, bufferedByteCount))
                ? bufferedByteCount
                : 0;
        }

        /// <summary>
        /// 在前导未知噪声之后寻找一项恰好由本次新增字节补完整且 CRC 有效的已知响应帧。
        /// 已经在更早字节处完整但 CRC 无效的候选不会因追加尾部字节而改变，故只检查
        /// 以当前缓冲末尾为帧末尾、且位于最大帧长度窗口内的候选。
        /// </summary>
        /// <returns>已知帧的缓冲偏移；不存在完整合法帧时为零。</returns>
        private int FindLaterKnownValidFrameOffsetUnderLock()
        {
            int earliestPossibleOffset = Math.Max(
                1,
                bufferedByteCount - options.MaximumFrameBytes);

            for (int offset = earliestPossibleOffset; offset < bufferedByteCount; offset++)
            {
                int availableLength = bufferedByteCount - offset;
                ExpectedLengthResult expected = GetExpectedLength(
                    receiveBuffer,
                    offset,
                    availableLength);

                if (expected.Status == ExpectedLengthStatus.Known &&
                    availableLength == expected.Length &&
                    ModbusCrc16.IsValid(receiveBuffer.AsSpan(offset, expected.Length)))
                {
                    return offset;
                }
            }

            return 0;
        }

        /// <summary>
        /// 根据缓冲中指定位置的功能码推导标准响应帧长度。
        /// </summary>
        /// <param name="buffer">包含待识别线路字节的数组。</param>
        /// <param name="offset">候选帧首字节偏移。</param>
        /// <param name="availableLength">从偏移开始已经收到的字节数。</param>
        /// <returns>未知、等待更多字节、已知长度或长度非法的推导结果。</returns>
        private ExpectedLengthResult GetExpectedLength(
            byte[] buffer,
            int offset,
            int availableLength)
        {
            if (availableLength < 2)
            {
                return ExpectedLengthResult.NeedMore;
            }

            byte functionCode = buffer[offset + 1];

            if ((functionCode & 0x80) != 0)
            {
                return ExpectedLengthResult.Known(5);
            }

            if (functionCode == 0x03)
            {
                if (availableLength < 3)
                {
                    return ExpectedLengthResult.NeedMore;
                }

                int expectedLength = 5 + buffer[offset + 2];
                return expectedLength > options.MaximumFrameBytes
                    ? ExpectedLengthResult.Invalid(expectedLength)
                    : ExpectedLengthResult.Known(expectedLength);
            }

            if (functionCode is 0x06 or 0x10)
            {
                return ExpectedLengthResult.Known(8);
            }

            return ExpectedLengthResult.Unknown;
        }

        /// <summary>
        /// 使用缓冲头指定长度创建一项接收帧，并保留准确来源区间。
        /// </summary>
        /// <param name="kind">输出记录类别。</param>
        /// <param name="length">从缓冲头复制的字节数。</param>
        /// <param name="completedTimestamp">记录完成时的单调时间戳。</param>
        /// <param name="isCrcValid">记录的 CRC 是否有效。</param>
        /// <param name="diagnostic">面向日志的诊断文本。</param>
        /// <returns>新的不可变接收记录。</returns>
        private ReceivedFrame CreateBufferedFrameUnderLock(
            ReceivedFrameKind kind,
            int length,
            long completedTimestamp,
            bool isCrcValid,
            string diagnostic)
        {
            return new ReceivedFrame(
                kind,
                receiveBuffer.AsSpan(0, length),
                portGeneration,
                checked(++frameSequence),
                receiveSequences[0],
                receiveSequences[length - 1],
                receiveTimestamps[0],
                completedTimestamp,
                isCrcValid,
                diagnostic,
                length,
                0);
        }

        /// <summary>
        /// 创建一项带有截断摘要的容量溢出记录，并进入静默前丢弃状态。
        /// </summary>
        /// <param name="completedTimestamp">发现溢出时的单调时间戳。</param>
        /// <param name="remainingInChunk">当前传输块中尚未写入缓存且将被丢弃的字节数。</param>
        /// <returns>新的不可变溢出记录。</returns>
        private ReceivedFrame CreateOverflowUnderLock(
            long completedTimestamp,
            int remainingInChunk)
        {
            int summaryLength = Math.Min(bufferedByteCount, options.MaximumFrameBytes);
            int observedByteCount = SaturatingAdd(bufferedByteCount, remainingInChunk);
            ReceivedFrame result = new(
                ReceivedFrameKind.ReceiveOverflow,
                receiveBuffer.AsSpan(0, summaryLength),
                portGeneration,
                checked(++frameSequence),
                receiveSequences[0],
                receiveSequences[bufferedByteCount - 1],
                receiveTimestamps[0],
                completedTimestamp,
                false,
                $"接收达到 {bufferedByteCount} 字节有界上限，已保存前 {summaryLength} 字节摘要并丢弃至静默。",
                observedByteCount,
                observedByteCount - summaryLength);

            ClearBufferUnderLock();
            isDiscardingUntilSilence = true;
            discardedByteCount = remainingInChunk;
            return result;
        }

        /// <summary>
        /// 为未被接受的传输块创建协议错误，且不修改原捕获时间或缓冲内容。
        /// </summary>
        /// <param name="chunk">被拒绝的接收块。</param>
        /// <param name="diagnostic">拒绝原因。</param>
        /// <returns>新的不可变协议错误记录。</returns>
        private ReceivedFrame CreateRejectedChunkErrorUnderLock(
            SerialReceiveChunk chunk,
            string diagnostic)
        {
            long completedTimestamp = Math.Max(lastObservedTimestamp, responseStartedTimestamp);

            return CreateProtocolErrorUnderLock(
                ReadOnlySpan<byte>.Empty,
                chunk.ReceiveSequence,
                chunk.ReceiveSequence,
                completedTimestamp,
                completedTimestamp,
                diagnostic,
                chunk.Data.Length);
        }

        /// <summary>
        /// 创建一项不改变缓冲状态的协议错误记录。
        /// </summary>
        /// <param name="data">需要保留的错误上下文字节。</param>
        /// <param name="firstReceiveSequence">首来源接收序号。</param>
        /// <param name="lastReceiveSequence">末来源接收序号。</param>
        /// <param name="startedTimestamp">错误上下文开始时间戳。</param>
        /// <param name="completedTimestamp">错误确定时间戳。</param>
        /// <param name="diagnostic">错误诊断。</param>
        /// <param name="observedByteCount">错误事件实际观察到的字节数。</param>
        /// <returns>新的不可变协议错误记录。</returns>
        private ReceivedFrame CreateProtocolErrorUnderLock(
            ReadOnlySpan<byte> data,
            long firstReceiveSequence,
            long lastReceiveSequence,
            long startedTimestamp,
            long completedTimestamp,
            string diagnostic,
            int observedByteCount)
        {
            return new ReceivedFrame(
                ReceivedFrameKind.ProtocolError,
                data,
                portGeneration,
                checked(++frameSequence),
                firstReceiveSequence,
                lastReceiveSequence,
                startedTimestamp,
                completedTimestamp,
                false,
                diagnostic,
                Math.Max(observedByteCount, data.Length),
                0);
        }

        /// <summary>
        /// 从缓冲头移除指定字节数，并保持剩余字节及来源元数据对齐。
        /// </summary>
        /// <param name="length">需要移除的正字节数。</param>
        private void RemovePrefixUnderLock(int length)
        {
            int remainingLength = bufferedByteCount - length;

            if (remainingLength > 0)
            {
                Array.Copy(receiveBuffer, length, receiveBuffer, 0, remainingLength);
                Array.Copy(receiveSequences, length, receiveSequences, 0, remainingLength);
                Array.Copy(receiveTimestamps, length, receiveTimestamps, 0, remainingLength);
            }

            Array.Clear(receiveBuffer, remainingLength, length);
            Array.Clear(receiveSequences, remainingLength, length);
            Array.Clear(receiveTimestamps, remainingLength, length);
            bufferedByteCount = remainingLength;
        }

        /// <summary>
        /// 清除缓冲字节及其来源元数据。
        /// </summary>
        private void ClearBufferUnderLock()
        {
            if (bufferedByteCount > 0)
            {
                Array.Clear(receiveBuffer, 0, bufferedByteCount);
                Array.Clear(receiveSequences, 0, bufferedByteCount);
                Array.Clear(receiveTimestamps, 0, bufferedByteCount);
            }

            bufferedByteCount = 0;
        }

        /// <summary>
        /// 在已持有同步门时确认捕获已经开始。
        /// </summary>
        /// <exception cref="InvalidOperationException">捕获未开始时抛出。</exception>
        private void EnsureCaptureActiveUnderLock()
        {
            if (!isCaptureActive)
            {
                throw new InvalidOperationException("必须先开始 RTU 接收捕获。");
            }
        }

        /// <summary>
        /// 把可变结果集合转换为调用方无法修改的独立只读快照。
        /// </summary>
        /// <param name="results">当前调用形成的结果集合。</param>
        /// <returns>空数组或新的只读结果集合。</returns>
        private static IReadOnlyList<ReceivedFrame> AsReadOnly(List<ReceivedFrame> results)
        {
            return results.Count == 0
                ? Array.Empty<ReceivedFrame>()
                : new ReadOnlyCollection<ReceivedFrame>(results.ToArray());
        }

        /// <summary>
        /// 创建只包含一项记录的只读结果集合。
        /// </summary>
        /// <param name="result">唯一结果记录。</param>
        /// <returns>只包含指定记录的只读数组。</returns>
        private static IReadOnlyList<ReceivedFrame> SingleResult(ReceivedFrame result)
        {
            return new ReadOnlyCollection<ReceivedFrame>(new[] { result });
        }

        /// <summary>
        /// 对非负计数执行饱和加法，避免极端丢弃流量令诊断计数溢出。
        /// </summary>
        /// <param name="left">左侧非负计数。</param>
        /// <param name="right">右侧非负计数。</param>
        /// <returns>精确和，或达到 <see cref="int.MaxValue"/> 的饱和值。</returns>
        private static int SaturatingAdd(int left, int right)
        {
            long sum = (long)left + right;
            return sum >= int.MaxValue ? int.MaxValue : (int)sum;
        }

        /// <summary>
        /// 指定结构长度推导所处的状态。
        /// </summary>
        private enum ExpectedLengthStatus
        {
            /// <summary>
            /// 尚未收到足以读取结构头的字节。
            /// </summary>
            NeedMore = 0,

            /// <summary>
            /// 功能码不属于当前标准事务结构集合。
            /// </summary>
            Unknown = 1,

            /// <summary>
            /// 已得到合法的期望总长度。
            /// </summary>
            Known = 2,

            /// <summary>
            /// 结构声称的长度超过配置上限。
            /// </summary>
            Invalid = 3,
        }

        /// <summary>
        /// 保存一次结构长度推导的状态和长度。
        /// </summary>
        /// <param name="Status">推导状态。</param>
        /// <param name="Length">已知或非法时的结构总长度，其他状态为零。</param>
        private readonly record struct ExpectedLengthResult(
            ExpectedLengthStatus Status,
            int Length)
        {
            /// <summary>
            /// 获取等待更多结构头字节的结果。
            /// </summary>
            internal static ExpectedLengthResult NeedMore =>
                new(ExpectedLengthStatus.NeedMore, 0);

            /// <summary>
            /// 获取未知功能码结果。
            /// </summary>
            internal static ExpectedLengthResult Unknown =>
                new(ExpectedLengthStatus.Unknown, 0);

            /// <summary>
            /// 创建具有合法总长度的推导结果。
            /// </summary>
            /// <param name="length">合法结构总长度。</param>
            /// <returns>新的已知长度结果。</returns>
            internal static ExpectedLengthResult Known(int length)
            {
                return new ExpectedLengthResult(ExpectedLengthStatus.Known, length);
            }

            /// <summary>
            /// 创建超过配置上限的长度推导结果。
            /// </summary>
            /// <param name="length">结构头声称的非法总长度。</param>
            /// <returns>新的非法长度结果。</returns>
            internal static ExpectedLengthResult Invalid(int length)
            {
                return new ExpectedLengthResult(ExpectedLengthStatus.Invalid, length);
            }
        }
    }
}
