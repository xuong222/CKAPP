using System.Threading.Channels;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Framing;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证可控双工流、唯一读取泵、接收块通道和 RTU 组帧器组成的完整接收链路。
    /// </summary>
    [TestFixture]
    public sealed class ReceivePipelineTests
    {
        /// <summary>
        /// 验证第一帧跨块分段且第二块同时粘连下一帧时，完整链路仍只输出两个独立帧。
        /// </summary>
        [Test]
        public async Task PumpToFramer_SplitThenCoalescedFrames_ProducesTwoFrames()
        {
            byte[] firstFrame = ModbusCrc16.Append(
                [0x01, 0x03, 0x04, 0x00, 0x01, 0x00, 0x02]);
            byte[] secondFrame = ModbusCrc16.Append(
                [0x01, 0x06, 0x00, 0x02, 0x00, 0x03]);
            await using ControllableDuplexStream stream = new();
            stream.QueueRead(firstFrame[..4]);
            stream.QueueRead([.. firstFrame[4..], .. secondFrame]);
            stream.QueueEndOfStream();
            ManualTimeProvider timeProvider = new();
            Channel<SerialReceiveChunk> channel = Channel.CreateBounded<SerialReceiveChunk>(
                new BoundedChannelOptions(SerialPortTransport.ReceiveChannelCapacity)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                });
            RtuReceiveFramer framer = new(RtuFramerOptions.CreateDefault(timeProvider));
            framer.StartCapture(RtuFramingMode.Standard, 9, timeProvider.GetTimestamp());
            Task pump = SerialReadPump.RunAsync(
                stream,
                channel.Writer,
                9,
                timeProvider,
                CancellationToken.None);
            List<ReceivedFrame> receivedFrames = [];

            await foreach (SerialReceiveChunk chunk in channel.Reader.ReadAllAsync())
            {
                receivedFrames.AddRange(framer.Append(chunk));
            }

            await pump.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(receivedFrames, Has.Count.EqualTo(2));
                Assert.That(receivedFrames[0].Data.ToArray(), Is.EqualTo(firstFrame));
                Assert.That(receivedFrames[1].Data.ToArray(), Is.EqualTo(secondFrame));
                Assert.That(receivedFrames.Select(frame => frame.FrameSequence), Is.EqualTo(new long[] { 1, 2 }));
                Assert.That(receivedFrames[0].FirstReceiveSequence, Is.EqualTo(1));
                Assert.That(receivedFrames[0].LastReceiveSequence, Is.EqualTo(2));
                Assert.That(receivedFrames[1].FirstReceiveSequence, Is.EqualTo(2));
            }));
        }
    }
}
