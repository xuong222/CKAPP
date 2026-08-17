using System.IO;
using System.Runtime.InteropServices;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证模拟串口的会话隔离、有界数据路径和故障传播契约。
    /// </summary>
    [TestFixture]
    public sealed class FakeSerialTransportTests
    {
        /// <summary>
        /// 验证打开成功和活动会话关闭都会递增代次，而重复关闭保持幂等。
        /// </summary>
        [Test]
        public async Task OpenAfterClose_ShouldIncrementPortGeneration()
        {
            await using FakeSerialTransport transport = new();
            SerialSettings settings = SerialSettings.CreateDefault("COM_TEST");

            await transport.OpenAsync(settings, CancellationToken.None);
            int firstGeneration = transport.PortGeneration;

            await transport.CloseAsync(CancellationToken.None);
            await transport.CloseAsync(CancellationToken.None);
            await transport.OpenAsync(settings, CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(firstGeneration, Is.EqualTo(1));
                Assert.That(transport.PortGeneration, Is.EqualTo(3));
                Assert.That(transport.IsOpen, Is.True);
            }));
        }

        /// <summary>
        /// 验证关闭会完成当前接收序列，使挂起的单后台读取无需真实等待即可退出。
        /// </summary>
        [Test]
        public async Task CloseWhileReadPending_ShouldCompleteReadSequence()
        {
            await using FakeSerialTransport transport = new();
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            await using IAsyncEnumerator<SerialReceiveChunk> enumerator = transport
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator();

            ValueTask<bool> pendingMove = enumerator.MoveNextAsync();
            Assert.That(pendingMove.IsCompleted, Is.False);

            await transport.CloseAsync(CancellationToken.None);

            Assert.That(await pendingMove, Is.False);
        }

        /// <summary>
        /// 验证写入历史独占输入字节，且公开快照遭修改后不会反向污染内部记录。
        /// </summary>
        [Test]
        public async Task WriteAndSnapshot_ShouldBothUseDefensiveCopies()
        {
            await using FakeSerialTransport transport = new();
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            byte[] callerFrame = [0x01, 0x03, 0x00, 0x00];

            await transport.WriteAsync(
                callerFrame,
                static () => true,
                CancellationToken.None);
            callerFrame[0] = 0xFF;

            ReadOnlyMemory<byte> exposedSnapshot = transport.WrittenFrames.Single();
            Assert.That(
                MemoryMarshal.TryGetArray(exposedSnapshot, out ArraySegment<byte> exposedArray),
                Is.True);
            exposedArray.Array![exposedArray.Offset] = 0xEE;

            Assert.That(
                transport.WrittenFrames.Single().ToArray(),
                Is.EqualTo(new byte[] { 0x01, 0x03, 0x00, 0x00 }));
        }

        /// <summary>
        /// 验证注入块自动携带当前代次、递增序号以及注入时间源的双时间基准。
        /// </summary>
        [Test]
        public async Task InjectReceive_ShouldStampGenerationSequenceAndManualTime()
        {
            DateTimeOffset initialUtc = new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
            ManualTimeProvider timeProvider = new(initialUtc, 100);
            await using FakeSerialTransport transport = new(timeProvider);
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            await using IAsyncEnumerator<SerialReceiveChunk> enumerator = transport
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator();

            await transport.InjectReceiveAsync(new byte[] { 0x01, 0x02 });
            Assert.That(await enumerator.MoveNextAsync(), Is.True);
            SerialReceiveChunk first = enumerator.Current;

            timeProvider.Advance(TimeSpan.FromMilliseconds(25));
            await transport.InjectReceiveAsync(new byte[] { 0x03 });
            Assert.That(await enumerator.MoveNextAsync(), Is.True);
            SerialReceiveChunk second = enumerator.Current;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(first.PortGeneration, Is.EqualTo(1));
                Assert.That(first.ReceiveSequence, Is.EqualTo(1));
                Assert.That(first.ArrivedAtUtc, Is.EqualTo(initialUtc));
                Assert.That(first.MonotonicTimestamp, Is.EqualTo(100));
                Assert.That(second.PortGeneration, Is.EqualTo(1));
                Assert.That(second.ReceiveSequence, Is.EqualTo(2));
                Assert.That(second.ArrivedAtUtc, Is.EqualTo(initialUtc.AddMilliseconds(25)));
                Assert.That(
                    second.MonotonicTimestamp,
                    Is.EqualTo(100 + TimeSpan.FromMilliseconds(25).Ticks));
            }));
        }

        /// <summary>
        /// 验证远端故障会关闭打开状态，并把原始 I/O 异常传播给正在枚举的读取方。
        /// </summary>
        [Test]
        public async Task RemoteDisconnectWithError_ShouldFaultReaderAndCloseSession()
        {
            await using FakeSerialTransport transport = new();
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            await using IAsyncEnumerator<SerialReceiveChunk> enumerator = transport
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator();

            transport.RemoteDisconnect(new IOException("设备已拔出"));

            IOException? exception = Assert.ThrowsAsync<IOException>((Func<Task>)(async () =>
            {
                await enumerator.MoveNextAsync();
            }));
            Assert.Multiple((Action)(() =>
            {
                Assert.That(exception!.Message, Is.EqualTo("设备已拔出"));
                Assert.That(transport.IsOpen, Is.False);
            }));
        }
    }
}
