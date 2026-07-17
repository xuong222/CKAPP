using System.IO;
using System.Threading.Channels;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证唯一后台读取循环的分段复制、顺序、终止和故障传播行为。
    /// </summary>
    [TestFixture]
    public sealed class SerialReadPumpTests
    {
        /// <summary>
        /// 验证底层两次非空读取分别成为两个独立、严格递增的不可变接收块。
        /// </summary>
        [Test]
        public async Task RunAsync_TwoReadSegments_EmitsTwoOrderedChunks()
        {
            await using ControllableDuplexStream stream = new();
            stream.QueueRead([0x01, 0x03, 0x04]);
            stream.QueueRead([0x00, 0x01, 0x00, 0x02]);
            stream.QueueEndOfStream();
            ManualTimeProvider timeProvider = new(
                new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
                100);
            Channel<SerialReceiveChunk> channel = CreateReceiveChannel();

            await SerialReadPump.RunAsync(
                stream,
                channel.Writer,
                7,
                timeProvider,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            List<SerialReceiveChunk> chunks = await ReadAllChunksAsync(channel.Reader);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(chunks, Has.Count.EqualTo(2));
                Assert.That(chunks[0].Data.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x03, 0x04 }));
                Assert.That(chunks[1].Data.ToArray(), Is.EqualTo(new byte[] { 0x00, 0x01, 0x00, 0x02 }));
                Assert.That(chunks.Select(chunk => chunk.ReceiveSequence), Is.EqualTo(new long[] { 1, 2 }));
                Assert.That(chunks.All(chunk => chunk.PortGeneration == 7), Is.True);
                Assert.That(chunks.All(chunk => chunk.MonotonicTimestamp == 100), Is.True);
            }));
        }

        /// <summary>
        /// 验证超过固定读取缓冲的单批线路字节被分段后仍恰好交付一次且顺序不变。
        /// </summary>
        [Test]
        public async Task RunAsync_DataLargerThanReadBuffer_PreservesEveryByteExactlyOnce()
        {
            await using ControllableDuplexStream stream = new();
            byte[] source = Enumerable.Range(0, 700).Select(index => (byte)index).ToArray();
            stream.QueueRead(source);
            stream.QueueEndOfStream();
            Channel<SerialReceiveChunk> channel = CreateReceiveChannel();

            await SerialReadPump.RunAsync(
                stream,
                channel.Writer,
                1,
                TimeProvider.System,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            List<SerialReceiveChunk> chunks = await ReadAllChunksAsync(channel.Reader);
            byte[] reconstructed = chunks.SelectMany(chunk => chunk.Data.ToArray()).ToArray();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(chunks, Has.Count.EqualTo(2));
                Assert.That(chunks[0].Data.Length, Is.EqualTo(SerialReadPump.ReadBufferBytes));
                Assert.That(chunks[1].Data.Length, Is.EqualTo(700 - SerialReadPump.ReadBufferBytes));
                Assert.That(reconstructed, Is.EqualTo(source));
            }));
        }

        /// <summary>
        /// 验证挂起读取收到会话取消后在一秒保险时限内正常完成通道。
        /// </summary>
        [Test]
        public async Task RunAsync_PendingReadCancelled_StopsPromptly()
        {
            await using ControllableDuplexStream stream = new();
            Channel<SerialReceiveChunk> channel = CreateReceiveChannel();
            using CancellationTokenSource cancellation = new();
            Task pump = SerialReadPump.RunAsync(
                stream,
                channel.Writer,
                1,
                TimeProvider.System,
                cancellation.Token);

            cancellation.Cancel();
            await pump.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.That(channel.Reader.Completion.IsCompletedSuccessfully, Is.True);
        }

        /// <summary>
        /// 验证释放挂起读取使用的流会令循环正常观察零字节结束，而不形成死锁。
        /// </summary>
        [Test]
        public async Task RunAsync_PendingStreamDisposed_StopsPromptly()
        {
            ControllableDuplexStream stream = new();
            Channel<SerialReceiveChunk> channel = CreateReceiveChannel();
            Task pump = SerialReadPump.RunAsync(
                stream,
                channel.Writer,
                1,
                TimeProvider.System,
                CancellationToken.None);

            await stream.DisposeAsync();
            await pump.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.That(channel.Reader.Completion.IsCompletedSuccessfully, Is.True);
        }

        /// <summary>
        /// 验证底层首次返回零字节会正常完成，且不会创建空接收块。
        /// </summary>
        [Test]
        public async Task RunAsync_ZeroByteRead_CompletesWithoutChunk()
        {
            await using ControllableDuplexStream stream = new();
            stream.QueueEndOfStream();
            Channel<SerialReceiveChunk> channel = CreateReceiveChannel();

            await SerialReadPump.RunAsync(
                stream,
                channel.Writer,
                1,
                TimeProvider.System,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            List<SerialReceiveChunk> chunks = await ReadAllChunksAsync(channel.Reader);

            Assert.That(chunks, Is.Empty);
        }

        /// <summary>
        /// 验证非关闭期 I/O 异常同时由读取循环任务和接收通道原样传播。
        /// </summary>
        [Test]
        public async Task RunAsync_IoFailure_FaultsCallerAndChannel()
        {
            await using ControllableDuplexStream stream = new();
            IOException expected = new("串口被拔出");
            stream.QueueIOException(expected);
            Channel<SerialReceiveChunk> channel = CreateReceiveChannel();

            IOException? pumpError = Assert.ThrowsAsync<IOException>((Func<Task>)(async () =>
            {
                await SerialReadPump.RunAsync(
                    stream,
                    channel.Writer,
                    1,
                    TimeProvider.System,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            }));
            IOException? channelError = Assert.ThrowsAsync<IOException>((Func<Task>)(async () =>
            {
                await channel.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            }));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(pumpError, Is.SameAs(expected));
                Assert.That(channelError, Is.SameAs(expected));
            }));
        }

        /// <summary>
        /// 创建与生产会话一致、采用等待背压策略的容量八接收通道。
        /// </summary>
        /// <returns>新的有界接收块通道。</returns>
        private static Channel<SerialReceiveChunk> CreateReceiveChannel()
        {
            return Channel.CreateBounded<SerialReceiveChunk>(
                new BoundedChannelOptions(SerialPortTransport.ReceiveChannelCapacity)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                });
        }

        /// <summary>
        /// 读取一个已由泵完成的通道中的所有接收块。
        /// </summary>
        /// <param name="reader">待读取通道的读取端。</param>
        /// <returns>保持通道顺序的接收块列表。</returns>
        private static async Task<List<SerialReceiveChunk>> ReadAllChunksAsync(
            ChannelReader<SerialReceiveChunk> reader)
        {
            List<SerialReceiveChunk> chunks = [];

            await foreach (SerialReceiveChunk chunk in reader.ReadAllAsync())
            {
                chunks.Add(chunk);
            }

            return chunks;
        }
    }
}
