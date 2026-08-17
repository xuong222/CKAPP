using System.Net;
using System.Net.Sockets;

using CH32UpperComputer.Core.Iap;

namespace CH32UpperComputer.Infrastructure.Iap
{
    /// <summary>
    /// 使用唯一 NetworkStream 顺序完成 IAP TCP 读写。
    /// </summary>
    public sealed class TcpIapTransport : IIapTransport
    {
        /// <summary>
        /// 保护连接引用替换，允许取消路径并发断开。
        /// </summary>
        private readonly object connectionSync = new();

        /// <summary>
        /// 为连接和精确读取提供可测试的超时计时器。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 当前 TCP 客户端。
        /// </summary>
        private TcpClient? client;

        /// <summary>
        /// 当前客户端对应的网络流。
        /// </summary>
        private NetworkStream? stream;

        /// <summary>
        /// 防止释放后再次连接。
        /// </summary>
        private int isDisposed;

        /// <summary>
        /// 使用生产系统时间创建 TCP IAP 传输。
        /// </summary>
        public TcpIapTransport()
            : this(TimeProvider.System)
        {
        }

        /// <summary>
        /// 使用指定时间源创建 TCP IAP 传输。
        /// </summary>
        /// <param name="timeProvider">控制连接和读取总超时的时间源。</param>
        public TcpIapTransport(TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 获取当前是否持有已连接 Socket 和网络流。
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (connectionSync)
                {
                    return client?.Connected == true && stream is not null;
                }
            }
        }

        /// <summary>
        /// 在指定总超时内建立 IPv4 TCP 连接。
        /// </summary>
        /// <param name="address">目标 IPv4 地址。</param>
        /// <param name="port">目标 TCP 端口。</param>
        /// <param name="timeout">连接总超时。</param>
        /// <param name="cancellationToken">取消连接。</param>
        /// <returns>连接可读写后的任务。</returns>
        public async ValueTask ConnectAsync(
            IPAddress address,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref isDisposed) != 0,
                this);
            ArgumentNullException.ThrowIfNull(address);
            ValidateEndpoint(address, port, timeout);
            await DisconnectAsync().ConfigureAwait(false);
            TcpClient newClient = new(AddressFamily.InterNetwork)
            {
                NoDelay = true,
            };

            try
            {
                await newClient
                    .ConnectAsync(address, port, cancellationToken)
                    .AsTask()
                    .WaitAsync(timeout, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                NetworkStream newStream = newClient.GetStream();

                lock (connectionSync)
                {
                    client = newClient;
                    stream = newStream;
                }
            }
            catch
            {
                newClient.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 完整写入一条请求帧。
        /// </summary>
        /// <param name="bytes">需要写出的完整请求帧。</param>
        /// <param name="cancellationToken">取消写入。</param>
        /// <returns>全部字节写入网络流后的任务。</returns>
        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            if (bytes.IsEmpty)
            {
                throw new ArgumentException("IAP 写入帧不能为空。", nameof(bytes));
            }

            if (bytes.Length >
                IapFrameCodec.RequestHeaderSize + IapFrameCodec.MaximumDataLength)
            {
                throw new InvalidDataException(
                    $"IAP 请求帧不能超过 " +
                    $"{IapFrameCodec.RequestHeaderSize + IapFrameCodec.MaximumDataLength} 字节。");
            }

            NetworkStream activeStream = GetRequiredStream();
            await activeStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await activeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 循环读取直到填满目标缓冲区或触发总超时。
        /// </summary>
        /// <param name="destination">需要完整填充的目标内存。</param>
        /// <param name="timeout">整次读取的总超时。</param>
        /// <param name="cancellationToken">取消读取。</param>
        /// <returns>目标内存已填满后的任务。</returns>
        public async ValueTask ReadExactAsync(
            Memory<byte> destination,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (destination.IsEmpty)
            {
                throw new ArgumentException("IAP 精确读取目标不能为空。", nameof(destination));
            }

            ValidateTimeout(timeout);
            NetworkStream activeStream = GetRequiredStream();
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using ITimer timeoutTimer = timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                timeoutSource,
                timeout,
                Timeout.InfiniteTimeSpan);
            int totalRead = 0;

            try
            {
                while (totalRead < destination.Length)
                {
                    int count = await activeStream
                        .ReadAsync(destination[totalRead..], timeoutSource.Token)
                        .ConfigureAwait(false);

                    if (count == 0)
                    {
                        throw new EndOfStreamException(
                            $"IAP TCP 在读取 {destination.Length} 字节时于 {totalRead} 字节处断开。");
                    }

                    totalRead = checked(totalRead + count);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"IAP TCP 在 {timeout.TotalMilliseconds:0} ms 内未读满 {destination.Length} 字节。");
            }
        }

        /// <summary>
        /// 原子移除当前连接引用并释放 Socket。
        /// </summary>
        /// <returns>连接引用已经清除后的任务。</returns>
        public ValueTask DisconnectAsync()
        {
            TcpClient? oldClient;
            NetworkStream? oldStream;

            lock (connectionSync)
            {
                oldClient = client;
                oldStream = stream;
                client = null;
                stream = null;
            }

            try
            {
                oldStream?.Dispose();
            }
            finally
            {
                oldClient?.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 永久释放传输并断开当前连接。
        /// </summary>
        /// <returns>连接已经释放后的任务。</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref isDisposed, 1) != 0)
            {
                return;
            }

            await DisconnectAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 获取当前网络流；未连接时抛出明确异常。
        /// </summary>
        /// <returns>当前唯一网络流。</returns>
        private NetworkStream GetRequiredStream()
        {
            lock (connectionSync)
            {
                return stream ?? throw new InvalidOperationException("IAP TCP 尚未连接。");
            }
        }

        /// <summary>
        /// 校验目标端点和连接超时。
        /// </summary>
        /// <param name="address">待连接的 IP 地址。</param>
        /// <param name="port">待连接端口。</param>
        /// <param name="timeout">连接总超时。</param>
        private static void ValidateEndpoint(
            IPAddress address,
            int port,
            TimeSpan timeout)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.Broadcast))
            {
                throw new ArgumentException("IAP 目标必须是明确的 IPv4 地址。", nameof(address));
            }

            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "TCP 端口必须位于 1 至 65535。");
            }

            ValidateTimeout(timeout);
        }

        /// <summary>
        /// 校验通信总超时为有限正数。
        /// </summary>
        /// <param name="timeout">待校验超时。</param>
        private static void ValidateTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "IAP 超时必须为有限正数。");
            }
        }
    }
}
