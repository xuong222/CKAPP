using System.Diagnostics;
using System.Net;

using CH32UpperComputer.Core.Iap;

namespace CH32UpperComputer.Infrastructure.Iap
{
    /// <summary>
    /// 保存一次完整请求和匹配响应的结果。
    /// </summary>
    public sealed class IapExchangeResult
    {
        /// <summary>
        /// 初始化一次完整交换结果。
        /// </summary>
        /// <param name="request">已经发送的请求头。</param>
        /// <param name="response">已经匹配的响应头。</param>
        /// <param name="helloInfo">仅 HELLO ACK 携带的设备信息。</param>
        /// <param name="duration">从写入开始到完整响应的耗时。</param>
        public IapExchangeResult(
            IapRequestHeader request,
            IapResponseHeader response,
            IapHelloInfo? helloInfo,
            TimeSpan duration)
        {
            Request = request;
            Response = response;
            HelloInfo = helloInfo;
            Duration = duration;
        }

        /// <summary>
        /// 获取已发送请求。
        /// </summary>
        public IapRequestHeader Request { get; }

        /// <summary>
        /// 获取匹配响应。
        /// </summary>
        public IapResponseHeader Response { get; }

        /// <summary>
        /// 获取可选 HELLO 设备信息。
        /// </summary>
        public IapHelloInfo? HelloInfo { get; }

        /// <summary>
        /// 获取完整交换耗时。
        /// </summary>
        public TimeSpan Duration { get; }
    }

    /// <summary>
    /// 在一条 TCP 连接中顺序编码、发送、精确读取并匹配 IAP 响应。
    /// </summary>
    public sealed class IapProtocolClient : IAsyncDisposable
    {
        /// <summary>
        /// 序列化同一连接上的请求，禁止交叉读写。
        /// </summary>
        private readonly SemaphoreSlim exchangeGate = new(1, 1);

        /// <summary>
        /// 底层可替换 TCP 字节流。
        /// </summary>
        private readonly IIapTransport transport;

        /// <summary>
        /// 独立 IAP 日志服务。
        /// </summary>
        private readonly IapCommunicationLogService logService;

        /// <summary>
        /// 提供事务耗时。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 当前连接下一个 sequence。
        /// </summary>
        private uint nextSequence = 1U;

        /// <summary>
        /// 当前应用内连接编号。
        /// </summary>
        private long connectionId;

        /// <summary>
        /// 防止重复释放。
        /// </summary>
        private int isDisposed;

        /// <summary>
        /// 初始化一个顺序 IAP 协议客户端。
        /// </summary>
        /// <param name="transport">负责连接和精确 TCP 读写的传输。</param>
        /// <param name="logService">记录原始帧和状态的独立日志服务。</param>
        /// <param name="timeProvider">计算事务耗时的时间源。</param>
        public IapProtocolClient(
            IIapTransport transport,
            IapCommunicationLogService logService,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(logService);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.transport = transport;
            this.logService = logService;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 获取底层传输当前是否连接。
        /// </summary>
        public bool IsConnected => transport.IsConnected;

        /// <summary>
        /// 获取当前应用内连接编号。
        /// </summary>
        public long ConnectionId => Interlocked.Read(ref connectionId);

        /// <summary>
        /// 建立新连接、增加连接编号并把 sequence 重置为一。
        /// </summary>
        /// <param name="address">目标 IPv4 地址。</param>
        /// <param name="port">目标 TCP 端口。</param>
        /// <param name="timeout">连接总超时。</param>
        /// <param name="cancellationToken">取消连接。</param>
        /// <returns>新连接可用后的任务。</returns>
        public async ValueTask ConnectAsync(
            IPAddress address,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await transport.ConnectAsync(address, port, timeout, cancellationToken)
                .ConfigureAwait(false);
            nextSequence = 1U;
            long newConnectionId = Interlocked.Increment(ref connectionId);
            RecordSystem($"TCP 连接 #{newConnectionId} 已建立，sequence 从 1 开始。");
        }

        /// <summary>
        /// 发送 HELLO；只有匹配 ACK 才读取后续 24 字节设备信息。
        /// </summary>
        /// <param name="timeout">响应总超时。</param>
        /// <param name="cancellationToken">取消本次事务。</param>
        /// <returns>响应头以及 ACK 时的 HELLO 信息。</returns>
        public ValueTask<IapExchangeResult> SendHelloAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return ExchangeAsync(
                IapCommand.Hello,
                0U,
                0U,
                0U,
                0U,
                ReadOnlyMemory<byte>.Empty,
                timeout,
                cancellationToken);
        }

        /// <summary>
        /// 发送 BEGIN 并等待 Bootloader 擦除确认。
        /// </summary>
        /// <param name="imageLength">完整镜像字节数。</param>
        /// <param name="imageCrc32">完整镜像 CRC32。</param>
        /// <param name="timeout">BEGIN 总超时。</param>
        /// <param name="cancellationToken">取消事务。</param>
        /// <returns>匹配 BEGIN 响应。</returns>
        public ValueTask<IapExchangeResult> SendBeginAsync(
            uint imageLength,
            uint imageCrc32,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return ExchangeAsync(
                IapCommand.Begin,
                0U,
                imageLength,
                0U,
                imageCrc32,
                ReadOnlyMemory<byte>.Empty,
                timeout,
                cancellationToken);
        }

        /// <summary>
        /// 发送一个顺序 DATA 分片。
        /// </summary>
        /// <param name="offset">分片相对 App 基址的偏移。</param>
        /// <param name="payload">一至 1024 字节分片。</param>
        /// <param name="timeout">DATA 响应总超时。</param>
        /// <param name="cancellationToken">取消事务。</param>
        /// <returns>匹配 DATA 响应。</returns>
        public ValueTask<IapExchangeResult> SendDataAsync(
            uint offset,
            ReadOnlyMemory<byte> payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return ExchangeAsync(
                IapCommand.Data,
                offset,
                checked((uint)payload.Length),
                IapCrc32.Compute(payload.Span),
                0U,
                payload,
                timeout,
                cancellationToken);
        }

        /// <summary>
        /// 发送 END 并等待镜像校验结果。
        /// </summary>
        /// <param name="timeout">END 响应总超时。</param>
        /// <param name="cancellationToken">取消事务。</param>
        /// <returns>匹配 END 响应。</returns>
        public ValueTask<IapExchangeResult> SendEndAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return ExchangeAsync(
                IapCommand.End,
                0U,
                0U,
                0U,
                0U,
                ReadOnlyMemory<byte>.Empty,
                timeout,
                cancellationToken);
        }

        /// <summary>
        /// 发送 RUN 并等待确认。
        /// </summary>
        /// <param name="timeout">RUN 响应总超时。</param>
        /// <param name="cancellationToken">取消事务。</param>
        /// <returns>匹配 RUN 响应。</returns>
        public ValueTask<IapExchangeResult> SendRunAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return ExchangeAsync(
                IapCommand.Run,
                0U,
                0U,
                0U,
                0U,
                ReadOnlyMemory<byte>.Empty,
                timeout,
                cancellationToken);
        }

        /// <summary>
        /// 发送 ABORT 并等待短确认。
        /// </summary>
        /// <param name="timeout">ABORT 短超时。</param>
        /// <param name="cancellationToken">取消事务。</param>
        /// <returns>匹配 ABORT 响应。</returns>
        public ValueTask<IapExchangeResult> SendAbortAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return ExchangeAsync(
                IapCommand.Abort,
                0U,
                0U,
                0U,
                0U,
                ReadOnlyMemory<byte>.Empty,
                timeout,
                cancellationToken);
        }

        /// <summary>
        /// 立即断开当前连接；下一次连接会重置 sequence。
        /// </summary>
        /// <returns>底层连接已清除后的任务。</returns>
        public async ValueTask DisconnectAsync()
        {
            await transport.DisconnectAsync().ConfigureAwait(false);
            RecordSystem($"TCP 连接 #{ConnectionId} 已断开。");
        }

        /// <summary>
        /// 释放协议门和底层传输。
        /// </summary>
        /// <returns>所有网络资源释放后的任务。</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref isDisposed, 1) != 0)
            {
                return;
            }

            await transport.DisposeAsync().ConfigureAwait(false);
            exchangeGate.Dispose();
        }

        /// <summary>
        /// 完成一次固定请求、响应头和可选 HELLO 信息交换。
        /// </summary>
        /// <param name="command">命令码。</param>
        /// <param name="offset">DATA 偏移。</param>
        /// <param name="length">BEGIN 镜像长度或 DATA 长度。</param>
        /// <param name="payloadCrc32">DATA 分片 CRC32。</param>
        /// <param name="imageCrc32">BEGIN 整体 CRC32。</param>
        /// <param name="payload">DATA 载荷。</param>
        /// <param name="timeout">响应总超时。</param>
        /// <param name="cancellationToken">取消事务。</param>
        /// <returns>完整匹配的交换结果。</returns>
        private async ValueTask<IapExchangeResult> ExchangeAsync(
            IapCommand command,
            uint offset,
            uint length,
            uint payloadCrc32,
            uint imageCrc32,
            ReadOnlyMemory<byte> payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            if (timeout <= TimeSpan.Zero ||
                timeout == Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    "IAP 事务总超时必须为有限正数。");
            }

            await exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource transactionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using ITimer timeoutTimer = timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                transactionCancellation,
                timeout,
                Timeout.InfiniteTimeSpan);

            try
            {
                if (!transport.IsConnected)
                {
                    throw new InvalidOperationException("IAP TCP 尚未连接。");
                }

                uint sequence = TakeNextSequence();
                IapRequestHeader request = new(
                    command,
                    sequence,
                    offset,
                    length,
                    payloadCrc32,
                    imageCrc32);
                byte[] requestBytes = IapFrameCodec.EncodeRequest(request, payload.Span);
                RecordTransmit(request, requestBytes);
                long startTimestamp = timeProvider.GetTimestamp();
                await transport
                    .WriteAsync(requestBytes, transactionCancellation.Token)
                    .ConfigureAwait(false);
                byte[] responseBytes = new byte[IapFrameCodec.ResponseHeaderSize];
                await transport
                    .ReadExactAsync(
                        responseBytes,
                        GetRemainingTimeout(startTimestamp, timeout),
                        transactionCancellation.Token)
                    .ConfigureAwait(false);
                IapResponseHeader response = IapFrameCodec.DecodeResponseHeader(responseBytes);

                if (response.Command != command || response.Sequence != sequence)
                {
                    RecordReceive(request, response, responseBytes, startTimestamp);
                    await transport.DisconnectAsync().ConfigureAwait(false);
                    throw new InvalidDataException(
                        $"IAP 响应不匹配：期望 {command}/sequence={sequence}，实际 {response.Command}/sequence={response.Sequence}。");
                }

                IapHelloInfo? helloInfo = null;
                byte[] completeResponse = responseBytes;

                if (command == IapCommand.Hello &&
                    response.Status == IapResponseStatus.Ack)
                {
                    byte[] helloBytes = new byte[IapFrameCodec.HelloInfoSize];
                    await transport
                        .ReadExactAsync(
                            helloBytes,
                            GetRemainingTimeout(startTimestamp, timeout),
                            transactionCancellation.Token)
                        .ConfigureAwait(false);
                    helloInfo = IapFrameCodec.DecodeHelloInfo(helloBytes);
                    completeResponse = new byte[
                        IapFrameCodec.ResponseHeaderSize + IapFrameCodec.HelloInfoSize];
                    responseBytes.CopyTo(completeResponse, 0);
                    helloBytes.CopyTo(completeResponse, responseBytes.Length);
                }

                TimeSpan duration = timeProvider.GetElapsedTime(startTimestamp);
                RecordReceive(request, response, completeResponse, startTimestamp);
                return new IapExchangeResult(request, response, helloInfo, duration);
            }
            catch (InvalidDataException)
            {
                await transport.DisconnectAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException exception)
            {
                await transport.DisconnectAsync().ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException(
                    $"IAP 事务在 {timeout.TotalMilliseconds:0} ms 总超时内未完成。",
                    exception);
            }
            finally
            {
                exchangeGate.Release();
            }
        }

        /// <summary>
        /// 从事务固定开始时刻计算剩余总超时，保证 HELLO 两段读取共用一个预算。
        /// </summary>
        /// <param name="startTimestamp">请求写入前记录的单调时间戳。</param>
        /// <param name="totalTimeout">整项事务允许的总时长。</param>
        /// <returns>仍可用于下一次精确读取的正时间跨度。</returns>
        private TimeSpan GetRemainingTimeout(
            long startTimestamp,
            TimeSpan totalTimeout)
        {
            TimeSpan remaining = totalTimeout -
                timeProvider.GetElapsedTime(startTimestamp);

            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"IAP 事务在 {totalTimeout.TotalMilliseconds:0} ms 总超时内未完成。");
            }

            return remaining;
        }

        /// <summary>
        /// 获取并递增当前连接 sequence，禁止回绕到零。
        /// </summary>
        /// <returns>本次请求使用的非零 sequence。</returns>
        private uint TakeNextSequence()
        {
            uint sequence = nextSequence;

            if (sequence == uint.MaxValue)
            {
                throw new InvalidOperationException("IAP sequence 已耗尽，必须重新连接。");
            }

            nextSequence = sequence + 1U;
            return sequence;
        }

        /// <summary>
        /// 记录发送帧。
        /// </summary>
        /// <param name="request">已发送请求头。</param>
        /// <param name="rawBytes">完整请求字节。</param>
        private void RecordTransmit(
            IapRequestHeader request,
            ReadOnlySpan<byte> rawBytes)
        {
            logService.Record(
                new IapCommunicationLogEntry(
                    logService.UtcNow,
                    ConnectionId,
                    IapCommunicationDirection.Transmit,
                    request.Command,
                    request.Sequence,
                    request.Offset,
                    request.Length,
                    request.Command == IapCommand.Begin
                        ? request.ImageCrc32
                        : request.PayloadCrc32,
                    null,
                    null,
                    null,
                    "发送请求",
                    rawBytes));
        }

        /// <summary>
        /// 记录接收帧。
        /// </summary>
        /// <param name="request">对应请求头。</param>
        /// <param name="response">已解析响应头。</param>
        /// <param name="rawBytes">响应头和可选 HELLO 信息。</param>
        /// <param name="startTimestamp">请求写入前的时间戳。</param>
        private void RecordReceive(
            IapRequestHeader request,
            IapResponseHeader response,
            ReadOnlySpan<byte> rawBytes,
            long startTimestamp)
        {
            logService.Record(
                new IapCommunicationLogEntry(
                    logService.UtcNow,
                    ConnectionId,
                    IapCommunicationDirection.Receive,
                    response.Command,
                    response.Sequence,
                    request.Offset,
                    request.Length,
                    request.Command == IapCommand.Begin
                        ? request.ImageCrc32
                        : request.PayloadCrc32,
                    response.Status,
                    response.Detail,
                    timeProvider.GetElapsedTime(startTimestamp),
                    response.Status == IapResponseStatus.Ack ? "收到 ACK" : "收到 NACK",
                    rawBytes));
        }

        /// <summary>
        /// 记录连接和状态诊断。
        /// </summary>
        /// <param name="message">中文诊断信息。</param>
        private void RecordSystem(string message)
        {
            logService.Record(
                new IapCommunicationLogEntry(
                    logService.UtcNow,
                    ConnectionId,
                    IapCommunicationDirection.System,
                    null,
                    null,
                    0U,
                    0U,
                    0U,
                    null,
                    null,
                    null,
                    message,
                    ReadOnlySpan<byte>.Empty));
        }

        /// <summary>
        /// 释放后禁止继续使用。
        /// </summary>
        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref isDisposed) != 0,
                this);
        }
    }
}
