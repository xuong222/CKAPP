using System.IO;
using System.Net;
using System.Net.Sockets;

using CH32UpperComputer.Infrastructure.Iap;

namespace CH32UpperComputer.Infrastructure.Tests.Iap
{
    /// <summary>
    /// 使用本机回环 TCP 验证生产传输的短读、粘包拆分和中途断开行为。
    /// </summary>
    [TestFixture]
    public sealed class TcpIapTransportTests
    {
        /// <summary>
        /// 验证服务端每次只发送一个字节时仍能精确读满目标。
        /// </summary>
        [Test]
        public async Task ReadExactAsync_OneByteSegments_FillsDestination()
        {
            byte[] expected = Enumerable.Range(0, 48)
                .Select(index => checked((byte)index))
                .ToArray();
            using TcpListener listener = CreateStartedListener();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = Task.Run(
                async () =>
                {
                    using TcpClient accepted = await listener.AcceptTcpClientAsync();
                    NetworkStream stream = accepted.GetStream();

                    foreach (byte value in expected)
                    {
                        await stream.WriteAsync(new byte[] { value });
                    }
                });
            await using TcpIapTransport transport = new();
            await transport.ConnectAsync(
                IPAddress.Loopback,
                port,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            byte[] actual = new byte[expected.Length];

            await transport.ReadExactAsync(
                actual,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            await serverTask;

            Assert.That(actual, Is.EqualTo(expected));
        }

        /// <summary>
        /// 验证一次到达的两个响应头可按两次固定长度读取拆分，不丢失第二帧。
        /// </summary>
        [Test]
        public async Task ReadExactAsync_TwoFramesInOneTcpWrite_SplitsAtRequestedLengths()
        {
            byte[] expected = Enumerable.Range(0, 48)
                .Select(index => checked((byte)(index + 1)))
                .ToArray();
            using TcpListener listener = CreateStartedListener();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = Task.Run(
                async () =>
                {
                    using TcpClient accepted = await listener.AcceptTcpClientAsync();
                    await accepted.GetStream().WriteAsync(expected);
                });
            await using TcpIapTransport transport = new();
            await transport.ConnectAsync(
                IPAddress.Loopback,
                port,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            byte[] first = new byte[24];
            byte[] second = new byte[24];

            await transport.ReadExactAsync(
                first,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            await transport.ReadExactAsync(
                second,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            await serverTask;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(first, Is.EqualTo(expected[..24]));
                Assert.That(second, Is.EqualTo(expected[24..]));
            }));
        }

        /// <summary>
        /// 验证响应尚未读满时远端关闭会得到明确短帧异常。
        /// </summary>
        [Test]
        public async Task ReadExactAsync_RemoteClosesMidFrame_ThrowsEndOfStream()
        {
            using TcpListener listener = CreateStartedListener();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = Task.Run(
                async () =>
                {
                    using TcpClient accepted = await listener.AcceptTcpClientAsync();
                    await accepted.GetStream().WriteAsync(new byte[7]);
                });
            await using TcpIapTransport transport = new();
            await transport.ConnectAsync(
                IPAddress.Loopback,
                port,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            EndOfStreamException? exception = null;

            try
            {
                await transport.ReadExactAsync(
                    new byte[24],
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None);
            }
            catch (EndOfStreamException captured)
            {
                exception = captured;
            }

            await serverTask;
            Assert.That(exception, Is.Not.Null);
        }

        /// <summary>
        /// 创建绑定回环随机端口并已开始监听的 TCP 服务端。
        /// </summary>
        /// <returns>由测试负责释放的监听器。</returns>
        private static TcpListener CreateStartedListener()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }
    }
}
