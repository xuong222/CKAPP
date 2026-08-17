using System.Net;

namespace CH32UpperComputer.Infrastructure.Iap
{
    /// <summary>
    /// 抽象 IAP 协议客户端所需的可取消 TCP 字节流操作。
    /// </summary>
    public interface IIapTransport : IAsyncDisposable
    {
        /// <summary>
        /// 获取当前是否持有已连接的 TCP 流。
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 在总超时内连接指定 IPv4 端点。
        /// </summary>
        /// <param name="address">目标 IPv4 地址。</param>
        /// <param name="port">目标 TCP 端口。</param>
        /// <param name="timeout">本次连接允许的总时长。</param>
        /// <param name="cancellationToken">取消连接等待。</param>
        /// <returns>连接建立并可读写后的任务。</returns>
        ValueTask ConnectAsync(
            IPAddress address,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken);

        /// <summary>
        /// 完整写入一条已经编码的 IAP 帧。
        /// </summary>
        /// <param name="bytes">需要完整写出的帧字节。</param>
        /// <param name="cancellationToken">取消写入。</param>
        /// <returns>全部字节交给网络流后的任务。</returns>
        ValueTask WriteAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken);

        /// <summary>
        /// 在总超时内循环读取，直到填满目标缓冲区。
        /// </summary>
        /// <param name="destination">必须完整填充的目标缓冲区。</param>
        /// <param name="timeout">整次精确读取允许的总时长。</param>
        /// <param name="cancellationToken">取消读取。</param>
        /// <returns>目标缓冲区已经填满后的任务。</returns>
        ValueTask ReadExactAsync(
            Memory<byte> destination,
            TimeSpan timeout,
            CancellationToken cancellationToken);

        /// <summary>
        /// 立即使当前连接失效并释放底层 Socket。
        /// </summary>
        /// <returns>当前连接引用已经清除后的任务。</returns>
        ValueTask DisconnectAsync();
    }
}
