using System.Buffers.Binary;
using System.Net;

using CH32UpperComputer.Core.Iap;
using CH32UpperComputer.Infrastructure.Iap;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Iap
{
    /// <summary>
    /// 验证 IAP TCP 会话的分段读取、响应匹配和连接内 sequence 规则。
    /// </summary>
    [TestFixture]
    public sealed class IapProtocolClientTests
    {
        /// <summary>
        /// 验证 HELLO NACK 只读取响应头，不错误等待不存在的附加信息。
        /// </summary>
        [Test]
        public async Task SendHelloAsync_Nack_ReadsOnlyResponseHeader()
        {
            ScriptedIapTransport transport = new();
            transport.QueueRead(CreateResponse(IapCommand.Hello, 1U, IapResponseStatus.Nack, 3U));
            IapCommunicationLogService logService = new(TimeProvider.System);
            await using IapProtocolClient client = new(
                transport,
                logService,
                TimeProvider.System);
            await client.ConnectAsync(
                IPAddress.Loopback,
                5000,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            IapExchangeResult result = await client.SendHelloAsync(
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Response.Status, Is.EqualTo(IapResponseStatus.Nack));
                Assert.That(result.HelloInfo, Is.Null);
                Assert.That(transport.RequestedReadLengths, Is.EqualTo(new[] { 24 }));
            }));
        }

        /// <summary>
        /// 验证新连接 sequence 从一开始，而原连接内继续递增。
        /// </summary>
        [Test]
        public async Task Reconnect_ResetsSequenceToOne()
        {
            ScriptedIapTransport transport = new();
            transport.QueueRead(CreateResponse(IapCommand.Hello, 1U, IapResponseStatus.Ack, 0U));
            transport.QueueRead(CreateHelloInfo());
            transport.QueueRead(CreateResponse(IapCommand.End, 2U, IapResponseStatus.Ack, 0U));
            transport.QueueRead(CreateResponse(IapCommand.Hello, 1U, IapResponseStatus.Ack, 0U));
            transport.QueueRead(CreateHelloInfo());
            IapCommunicationLogService logService = new(TimeProvider.System);
            await using IapProtocolClient client = new(
                transport,
                logService,
                TimeProvider.System);

            await client.ConnectAsync(
                IPAddress.Loopback,
                5000,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            _ = await client.SendHelloAsync(
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            _ = await client.SendEndAsync(
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            await client.DisconnectAsync();
            await client.ConnectAsync(
                IPAddress.Loopback,
                5000,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            _ = await client.SendHelloAsync(
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            uint[] sequences = transport.Writes
                .Select(frame => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12, 4)))
                .ToArray();
            Assert.That(sequences, Is.EqualTo(new uint[] { 1U, 2U, 1U }));
        }

        /// <summary>
        /// 验证 command 错配会立即关闭连接，避免未知附加数据污染下一事务。
        /// </summary>
        [Test]
        public async Task SendHelloAsync_CommandMismatch_Disconnects()
        {
            ScriptedIapTransport transport = new();
            transport.QueueRead(CreateResponse(IapCommand.End, 1U, IapResponseStatus.Ack, 0U));
            IapCommunicationLogService logService = new(TimeProvider.System);
            await using IapProtocolClient client = new(
                transport,
                logService,
                TimeProvider.System);
            await client.ConnectAsync(
                IPAddress.Loopback,
                5000,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            InvalidDataException? capturedException = null;

            try
            {
                _ = await client.SendHelloAsync(
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None);
            }
            catch (InvalidDataException exception)
            {
                capturedException = exception;
            }

            Assert.That(capturedException, Is.Not.Null);
            Assert.That(transport.IsConnected, Is.False);
        }

        /// <summary>
        /// 验证 HELLO ACK 附加信息不足 24 字节会终止会话而不是污染后续事务。
        /// </summary>
        [Test]
        public async Task SendHelloAsync_ShortHelloInfo_Disconnects()
        {
            ScriptedIapTransport transport = new();
            transport.QueueRead(CreateResponse(
                IapCommand.Hello,
                1U,
                IapResponseStatus.Ack,
                0U));
            transport.QueueRead(new byte[10]);
            IapCommunicationLogService logService = new(TimeProvider.System);
            await using IapProtocolClient client = new(
                transport,
                logService,
                TimeProvider.System);
            await client.ConnectAsync(
                IPAddress.Loopback,
                5000,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            InvalidDataException? exception = null;

            try
            {
                _ = await client.SendHelloAsync(
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None);
            }
            catch (InvalidDataException captured)
            {
                exception = captured;
            }

            Assert.Multiple((Action)(() =>
            {
                Assert.That(exception, Is.Not.Null);
                Assert.That(transport.IsConnected, Is.False);
                Assert.That(transport.RequestedReadLengths, Is.EqualTo(new[] { 24, 24 }));
            }));
        }

        /// <summary>
        /// 验证 HELLO 响应头和附加信息共用一个三秒总预算而不是各自重置超时。
        /// </summary>
        [Test]
        public async Task SendHelloAsync_TwoReads_ShareOneTotalTimeoutBudget()
        {
            ManualTimeProvider timeProvider = new();
            ScriptedIapTransport transport = new();
            transport.QueueRead(CreateResponse(
                IapCommand.Hello,
                1U,
                IapResponseStatus.Ack,
                0U));
            transport.QueueRead(CreateHelloInfo());
            transport.BeforeRead = _ =>
            {
                if (transport.RequestedReadLengths.Count == 1)
                {
                    timeProvider.Advance(TimeSpan.FromSeconds(2));
                }
            };
            IapCommunicationLogService logService = new(timeProvider);
            await using IapProtocolClient client = new(
                transport,
                logService,
                timeProvider);
            await client.ConnectAsync(
                IPAddress.Loopback,
                5000,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            _ = await client.SendHelloAsync(
                TimeSpan.FromSeconds(3),
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(transport.RequestedTimeouts, Has.Count.EqualTo(2));
                Assert.That(
                    transport.RequestedTimeouts[0],
                    Is.EqualTo(TimeSpan.FromSeconds(3)));
                Assert.That(
                    transport.RequestedTimeouts[1],
                    Is.EqualTo(TimeSpan.FromSeconds(1)));
            }));
        }

        /// <summary>
        /// 验证请求写入阻塞时也受事务总超时约束，并在超时后关闭可能残留半帧的连接。
        /// </summary>
        [Test]
        public async Task SendEndAsync_WriteBlocked_TimesOutAndDisconnects()
        {
            ScriptedIapTransport transport = new()
            {
                WriteHandler = cancellationToken =>
                    new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)),
            };
            IapCommunicationLogService logService = new(TimeProvider.System);
            await using IapProtocolClient client = new(
                transport,
                logService,
                TimeProvider.System);
            await client.ConnectAsync(
                IPAddress.Loopback,
                5000,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Exception? capturedException = null;

            try
            {
                _ = await client.SendEndAsync(
                    TimeSpan.FromMilliseconds(50),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                capturedException = exception;
            }

            Assert.Multiple((Action)(() =>
            {
                Assert.That(capturedException, Is.TypeOf<TimeoutException>());
                Assert.That(transport.IsConnected, Is.False);
            }));
        }

        /// <summary>
        /// 创建一条固定 24 字节 Bootloader 响应。
        /// </summary>
        /// <param name="command">回显命令。</param>
        /// <param name="sequence">回显 sequence。</param>
        /// <param name="status">ACK 或 NACK。</param>
        /// <param name="detail">命令相关 detail。</param>
        /// <returns>按小端编码的响应头。</returns>
        private static byte[] CreateResponse(
            IapCommand command,
            uint sequence,
            IapResponseStatus status,
            uint detail)
        {
            byte[] bytes = new byte[IapFrameCodec.ResponseHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), IapFrameCodec.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), IapFrameCodec.ResponseHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)command);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), (uint)status);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), detail);
            return bytes;
        }

        /// <summary>
        /// 创建一份与当前 Bootloader 分区一致的 HELLO 信息。
        /// </summary>
        /// <returns>固定 24 字节 HELLO 附加信息。</returns>
        private static byte[] CreateHelloInfo()
        {
            byte[] bytes = new byte[IapFrameCodec.HelloInfoSize];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x00010000U);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 0x0800C000U);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 204800U);
            return bytes;
        }

        /// <summary>
        /// 提供精确脚本响应并记录每次读取长度的内存 IAP 传输。
        /// </summary>
        private sealed class ScriptedIapTransport : IIapTransport
        {
            /// <summary>
            /// 尚未被读取的完整脚本块。
            /// </summary>
            private readonly Queue<byte[]> reads = new();

            /// <summary>
            /// 获取当前是否处于模拟连接状态。
            /// </summary>
            public bool IsConnected { get; private set; }

            /// <summary>
            /// 获取所有完整写帧。
            /// </summary>
            public List<byte[]> Writes { get; } = new();

            /// <summary>
            /// 获取每次 ReadExact 请求的目标长度。
            /// </summary>
            public List<int> RequestedReadLengths { get; } = new();

            /// <summary>
            /// 获取每次精确读取实际收到的剩余总超时。
            /// </summary>
            public List<TimeSpan> RequestedTimeouts { get; } = new();

            /// <summary>
            /// 获取或设置每次脚本读取前的同步测试回调。
            /// </summary>
            public Action<int>? BeforeRead { get; set; }

            /// <summary>
            /// 获取或设置可模拟阻塞、取消或故障的写入处理器。
            /// </summary>
            public Func<CancellationToken, ValueTask>? WriteHandler { get; set; }

            /// <summary>
            /// 追加下一次精确读取需要返回的脚本块。
            /// </summary>
            /// <param name="bytes">长度必须与下一次读取目标一致的字节。</param>
            public void QueueRead(byte[] bytes)
            {
                reads.Enqueue((byte[])bytes.Clone());
            }

            /// <summary>
            /// 建立模拟连接。
            /// </summary>
            /// <param name="address">目标 IPv4 地址。</param>
            /// <param name="port">目标 TCP 端口。</param>
            /// <param name="timeout">连接超时。</param>
            /// <param name="cancellationToken">取消连接。</param>
            /// <returns>连接状态已经更新后的任务。</returns>
            public ValueTask ConnectAsync(
                IPAddress address,
                int port,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                _ = address;
                _ = port;
                _ = timeout;
                cancellationToken.ThrowIfCancellationRequested();
                IsConnected = true;
                return ValueTask.CompletedTask;
            }

            /// <summary>
            /// 记录一个完整模拟写帧。
            /// </summary>
            /// <param name="bytes">待发送的完整帧。</param>
            /// <param name="cancellationToken">取消写入。</param>
            /// <returns>帧已经复制后的任务。</returns>
            public ValueTask WriteAsync(
                ReadOnlyMemory<byte> bytes,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Writes.Add(bytes.ToArray());
                return WriteHandler?.Invoke(cancellationToken) ??
                    ValueTask.CompletedTask;
            }

            /// <summary>
            /// 将下一脚本块完整复制到目标缓冲区。
            /// </summary>
            /// <param name="destination">必须读满的目标缓冲区。</param>
            /// <param name="timeout">读取总超时。</param>
            /// <param name="cancellationToken">取消读取。</param>
            /// <returns>目标缓冲区已经写满后的任务。</returns>
            public ValueTask ReadExactAsync(
                Memory<byte> destination,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestedReadLengths.Add(destination.Length);
                RequestedTimeouts.Add(timeout);
                BeforeRead?.Invoke(destination.Length);
                byte[] bytes = reads.Dequeue();

                if (bytes.Length != destination.Length)
                {
                    throw new InvalidDataException("脚本读取长度与目标缓冲区不一致。");
                }

                bytes.CopyTo(destination);
                return ValueTask.CompletedTask;
            }

            /// <summary>
            /// 断开模拟连接。
            /// </summary>
            /// <returns>连接状态已经清除后的任务。</returns>
            public ValueTask DisconnectAsync()
            {
                IsConnected = false;
                return ValueTask.CompletedTask;
            }

            /// <summary>
            /// 释放模拟连接。
            /// </summary>
            /// <returns>连接已经断开后的任务。</returns>
            public ValueTask DisposeAsync()
            {
                IsConnected = false;
                return ValueTask.CompletedTask;
            }
        }
    }
}
