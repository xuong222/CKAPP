using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Framing;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Framing
{
    /// <summary>
    /// 验证 RTU 状态机的结构推导、分段粘连、原始边界、超时和容量约束。
    /// </summary>
    [TestFixture]
    public sealed class RtuReceiveFramerTests
    {
        /// <summary>
        /// 验证 0x03、0x06、0x10 和异常响应均按预期结构立即拆出。
        /// </summary>
        /// <param name="functionCode">待验证的 Modbus 功能码。</param>
        [TestCase(0x03)]
        [TestCase(0x06)]
        [TestCase(0x10)]
        [TestCase(0x83)]
        public void Append_KnownResponseStructure_EmitsStandardFrame(int functionCode)
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            framer.StartCapture(RtuFramingMode.Standard, 1, timeProvider.GetTimestamp());
            byte[] frame = CreateFrameForFunction((byte)functionCode);

            IReadOnlyList<ReceivedFrame> results = framer.Append(
                CreateChunk(frame, 1, 1, timeProvider.GetTimestamp()));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Kind, Is.EqualTo(ReceivedFrameKind.StandardFrame));
                Assert.That(results[0].Data.ToArray(), Is.EqualTo(frame));
                Assert.That(results[0].IsCrcValid, Is.True);
                Assert.That(results[0].FirstReceiveSequence, Is.EqualTo(1));
                Assert.That(results[0].LastReceiveSequence, Is.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证 0x03 字节计数声称的总长度超过 256 时立即报告协议错误并停止扩容。
        /// </summary>
        [Test]
        public void Append_ReadHoldingResponseOverMaximum_EmitsProtocolError()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            framer.StartCapture(RtuFramingMode.Standard, 1, 0);

            IReadOnlyList<ReceivedFrame> results = framer.Append(
                CreateChunk([0x01, 0x03, 0xFC], 1, 1, 0));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Kind, Is.EqualTo(ReceivedFrameKind.ProtocolError));
                Assert.That(results[0].Diagnostic, Does.Contain("257"));
                Assert.That(results[0].Data.Length, Is.EqualTo(3));
            }));
        }

        /// <summary>
        /// 验证一帧被拆成一字节、两字节和剩余字节三个接收块时只交付一次完整帧。
        /// </summary>
        [Test]
        public void Append_FrameSplitAcrossOneTwoAndRemainingBytes_EmitsOnce()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] frame = CreateFrameForFunction(0x03);
            framer.StartCapture(RtuFramingMode.Standard, 4, 0);

            IReadOnlyList<ReceivedFrame> first = framer.Append(CreateChunk(frame[..1], 4, 1, 0));
            timeProvider.Advance(TimeSpan.FromMilliseconds(2));
            IReadOnlyList<ReceivedFrame> second = framer.Append(CreateChunk(frame[1..3], 4, 2, timeProvider.GetTimestamp()));
            timeProvider.Advance(TimeSpan.FromMilliseconds(2));
            IReadOnlyList<ReceivedFrame> third = framer.Append(CreateChunk(frame[3..], 4, 3, timeProvider.GetTimestamp()));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(first, Is.Empty);
                Assert.That(second, Is.Empty);
                Assert.That(third, Has.Count.EqualTo(1));
                Assert.That(third[0].Data.ToArray(), Is.EqualTo(frame));
                Assert.That(third[0].FirstReceiveSequence, Is.EqualTo(1));
                Assert.That(third[0].LastReceiveSequence, Is.EqualTo(3));
            }));
        }

        /// <summary>
        /// 验证同一接收块中的两帧粘包被拆为两个独立且递增序号的记录。
        /// </summary>
        [Test]
        public void Append_TwoFramesInOneChunk_SplitsBothFrames()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] firstFrame = CreateFrameForFunction(0x06);
            byte[] secondFrame = CreateFrameForFunction(0x10);
            framer.StartCapture(RtuFramingMode.Standard, 1, 0);

            IReadOnlyList<ReceivedFrame> results = framer.Append(
                CreateChunk([.. firstFrame, .. secondFrame], 1, 1, 0));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(results, Has.Count.EqualTo(2));
                Assert.That(results[0].Data.ToArray(), Is.EqualTo(firstFrame));
                Assert.That(results[1].Data.ToArray(), Is.EqualTo(secondFrame));
                Assert.That(results[0].FrameSequence, Is.EqualTo(1));
                Assert.That(results[1].FrameSequence, Is.EqualTo(2));
            }));
        }

        /// <summary>
        /// 验证原始模式中有效标准帧后的噪声不会并入前帧，并在静默后单独保留。
        /// </summary>
        [Test]
        public void RawDebug_ValidFrameThenNoise_EmitsSeparateRawCaptureAfterSilence()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] frame = CreateFrameForFunction(0x06);
            framer.StartCapture(RtuFramingMode.RawDebug, 1, 0);

            IReadOnlyList<ReceivedFrame> immediate = framer.Append(
                CreateChunk([.. frame, 0xAA, 0x55, 0x11], 1, 1, 0));
            timeProvider.Advance(TimeSpan.FromMilliseconds(20));
            IReadOnlyList<ReceivedFrame> afterSilence = framer.ObserveTime(timeProvider.GetTimestamp());

            Assert.Multiple((Action)(() =>
            {
                Assert.That(immediate, Has.Count.EqualTo(1));
                Assert.That(immediate[0].Kind, Is.EqualTo(ReceivedFrameKind.StandardFrame));
                Assert.That(afterSilence, Has.Count.EqualTo(1));
                Assert.That(afterSilence[0].Kind, Is.EqualTo(ReceivedFrameKind.UnparsedRawCapture));
                Assert.That(afterSilence[0].Data.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0x55, 0x11 }));
            }));
        }

        /// <summary>
        /// 验证未知前导噪声不会吞并同一批后续 CRC 正确的标准帧。
        /// </summary>
        [Test]
        public void RawDebug_NoiseThenKnownFrameInSameChunk_Resynchronizes()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] frame = CreateFrameForFunction(0x10);
            framer.StartCapture(RtuFramingMode.RawDebug, 1, 0);

            IReadOnlyList<ReceivedFrame> results = framer.Append(
                CreateChunk([0xAA, 0x44, 0x55, .. frame], 1, 1, 0));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(results, Has.Count.EqualTo(2));
                Assert.That(results[0].Kind, Is.EqualTo(ReceivedFrameKind.UnparsedRawCapture));
                Assert.That(results[0].Data.ToArray(), Is.EqualTo(new byte[] { 0xAA, 0x44, 0x55 }));
                Assert.That(results[1].Kind, Is.EqualTo(ReceivedFrameKind.StandardFrame));
                Assert.That(results[1].Data.ToArray(), Is.EqualTo(frame));
            }));
        }

        /// <summary>
        /// 验证未知功能码从缓冲头选择最早有效 CRC 边界。
        /// </summary>
        [Test]
        public void RawDebug_UnknownFunctionWithCrc_EmitsEarliestRawCrcFrame()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] firstFrame = ModbusCrc16.Append([0x01, 0x44, 0xAB]);
            byte[] trailingFrame = ModbusCrc16.Append([0x01, 0x45, 0xCD]);
            framer.StartCapture(RtuFramingMode.RawDebug, 1, 0);

            IReadOnlyList<ReceivedFrame> results = framer.Append(
                CreateChunk([.. firstFrame, .. trailingFrame], 1, 1, 0));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(results, Has.Count.EqualTo(2));
                Assert.That(results[0].Kind, Is.EqualTo(ReceivedFrameKind.RawCrcFrame));
                Assert.That(results[0].Data.ToArray(), Is.EqualTo(firstFrame));
                Assert.That(results[1].Data.ToArray(), Is.EqualTo(trailingFrame));
            }));
        }

        /// <summary>
        /// 验证频繁观察时间不会移动最后字节基线，原始残片仍在固定静默截止点结束。
        /// </summary>
        [Test]
        public void RawDebug_RepeatedTimeObservation_DoesNotPostponeInterByteDeadline()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            framer.StartCapture(RtuFramingMode.RawDebug, 1, 0);
            framer.Append(CreateChunk([0x01, 0x44, 0xAA], 1, 1, 0));

            timeProvider.Advance(TimeSpan.FromMilliseconds(10));
            IReadOnlyList<ReceivedFrame> firstPoll = framer.ObserveTime(timeProvider.GetTimestamp());
            timeProvider.Advance(TimeSpan.FromMilliseconds(9));
            IReadOnlyList<ReceivedFrame> secondPoll = framer.ObserveTime(timeProvider.GetTimestamp());
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            IReadOnlyList<ReceivedFrame> deadlinePoll = framer.ObserveTime(timeProvider.GetTimestamp());

            Assert.Multiple((Action)(() =>
            {
                Assert.That(firstPoll, Is.Empty);
                Assert.That(secondPoll, Is.Empty);
                Assert.That(deadlinePoll, Has.Count.EqualTo(1));
                Assert.That(deadlinePoll[0].Kind, Is.EqualTo(ReceivedFrameKind.UnparsedRawCapture));
            }));
        }

        /// <summary>
        /// 验证标准模式残片只在固定响应总超时后作为未解析原始捕获结束。
        /// </summary>
        [Test]
        public void Standard_PartialFrameAtResponseTimeout_EmitsUnparsedCapture()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            framer.StartCapture(RtuFramingMode.Standard, 1, 0);
            framer.Append(CreateChunk([0x01, 0x03, 0x04], 1, 1, 0));

            timeProvider.Advance(TimeSpan.FromMilliseconds(999));
            IReadOnlyList<ReceivedFrame> before = framer.ObserveTime(timeProvider.GetTimestamp());
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            IReadOnlyList<ReceivedFrame> atDeadline = framer.ObserveTime(timeProvider.GetTimestamp());

            Assert.Multiple((Action)(() =>
            {
                Assert.That(before, Is.Empty);
                Assert.That(atDeadline, Has.Count.EqualTo(1));
                Assert.That(atDeadline[0].Kind, Is.EqualTo(ReceivedFrameKind.UnparsedRawCapture));
                Assert.That(atDeadline[0].IsCrcValid, Is.False);
            }));
        }

        /// <summary>
        /// 验证完全没有接收字节的标准事务在总超时时不伪造接收记录。
        /// </summary>
        [Test]
        public void Standard_NoBytesAtResponseTimeout_EmitsNothing()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            framer.StartCapture(RtuFramingMode.Standard, 1, 0);
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            IReadOnlyList<ReceivedFrame> results = framer.ObserveTime(timeProvider.GetTimestamp());

            Assert.That(results, Is.Empty);
        }

        /// <summary>
        /// 验证完整已知帧在固定响应截止时间后到达时不会被标记为可匹配标准响应。
        /// </summary>
        [Test]
        public void Standard_KnownFrameAfterResponseDeadline_IsFilteredAsLateRawCapture()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] frame = CreateFrameForFunction(0x06);
            framer.StartCapture(RtuFramingMode.Standard, 1, 0);
            timeProvider.Advance(TimeSpan.FromMilliseconds(1001));

            IReadOnlyList<ReceivedFrame> results = framer.Append(
                CreateChunk(frame, 1, 1, timeProvider.GetTimestamp()));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Kind, Is.EqualTo(ReceivedFrameKind.UnparsedRawCapture));
                Assert.That(results[0].IsCrcValid, Is.True);
                Assert.That(results[0].Diagnostic, Does.Contain("截止"));
            }));
        }

        /// <summary>
        /// 验证原始未知数据在响应截止后追加时立即形成诊断记录而不会长期滞留缓存。
        /// </summary>
        [Test]
        public void RawDebug_UnknownBytesAfterResponseDeadline_AreFlushedImmediately()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            framer.StartCapture(RtuFramingMode.RawDebug, 1, 0);
            timeProvider.Advance(TimeSpan.FromMilliseconds(1001));

            IReadOnlyList<ReceivedFrame> results = framer.Append(
                CreateChunk([0x01, 0x44, 0xAA], 1, 1, timeProvider.GetTimestamp()));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].Kind, Is.EqualTo(ReceivedFrameKind.UnparsedRawCapture));
                Assert.That(results[0].Data.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x44, 0xAA }));
            }));
        }

        /// <summary>
        /// 验证原始捕获恰好达到 4096 字节即输出一次截断溢出，并丢弃至连续静默。
        /// </summary>
        [Test]
        public void RawDebug_ExactlyAtBufferLimit_OverflowsOnceAndRecoversAfterSilence()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] overflowingData = CreateUnknownNonCrcData(4096);
            framer.StartCapture(RtuFramingMode.RawDebug, 1, 0);

            IReadOnlyList<ReceivedFrame> overflow = framer.Append(
                CreateChunk(overflowingData, 1, 1, 0));
            IReadOnlyList<ReceivedFrame> discarded = framer.Append(
                CreateChunk([0x11, 0x22], 1, 2, 0));
            timeProvider.Advance(TimeSpan.FromMilliseconds(19));
            IReadOnlyList<ReceivedFrame> beforeSilence = framer.ObserveTime(timeProvider.GetTimestamp());
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            IReadOnlyList<ReceivedFrame> atSilence = framer.ObserveTime(timeProvider.GetTimestamp());
            byte[] recoveredFrame = ModbusCrc16.Append([0x01, 0x44, 0x5A]);
            IReadOnlyList<ReceivedFrame> recovered = framer.Append(
                CreateChunk(recoveredFrame, 1, 3, timeProvider.GetTimestamp()));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(overflow, Has.Count.EqualTo(1));
                Assert.That(overflow[0].Kind, Is.EqualTo(ReceivedFrameKind.ReceiveOverflow));
                Assert.That(overflow[0].Data.Length, Is.EqualTo(256));
                Assert.That(overflow[0].ObservedByteCount, Is.EqualTo(4096));
                Assert.That(overflow[0].OverflowByteCount, Is.EqualTo(3840));
                Assert.That(discarded, Is.Empty);
                Assert.That(beforeSilence, Is.Empty);
                Assert.That(atSilence, Is.Empty);
                Assert.That(recovered, Has.Count.EqualTo(1));
                Assert.That(recovered[0].Kind, Is.EqualTo(ReceivedFrameKind.RawCrcFrame));
            }));
        }

        /// <summary>
        /// 验证溢出丢弃期间跨过固定响应截止点后，即使静默复位也不会把下一帧当作按时响应。
        /// </summary>
        [Test]
        public void RawDebug_OverflowDiscardCrossesResponseDeadline_NextFrameRemainsLate()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            framer.StartCapture(RtuFramingMode.RawDebug, 1, 0);
            framer.Append(CreateChunk(CreateUnknownNonCrcData(4096), 1, 1, 0));
            timeProvider.Advance(TimeSpan.FromMilliseconds(1001));

            IReadOnlyList<ReceivedFrame> reset = framer.ObserveTime(timeProvider.GetTimestamp());
            byte[] lateFrame = ModbusCrc16.Append([0x01, 0x44, 0x5A]);
            IReadOnlyList<ReceivedFrame> late = framer.Append(
                CreateChunk(lateFrame, 1, 2, timeProvider.GetTimestamp()));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(reset, Is.Empty);
                Assert.That(late, Has.Count.EqualTo(1));
                Assert.That(late[0].Kind, Is.EqualTo(ReceivedFrameKind.UnparsedRawCapture));
                Assert.That(late[0].Diagnostic, Does.Contain("截止"));
            }));
        }

        /// <summary>
        /// 验证端口代次不匹配和时间倒退均产生协议错误，且不污染当前标准帧残片。
        /// </summary>
        [Test]
        public void Append_RejectedChunk_DoesNotPolluteCurrentCapture()
        {
            ManualTimeProvider timeProvider = new();
            RtuReceiveFramer framer = CreateFramer(timeProvider);
            byte[] frame = CreateFrameForFunction(0x06);
            framer.StartCapture(RtuFramingMode.Standard, 7, 100);
            framer.Append(CreateChunk(frame[..3], 7, 1, 100));

            IReadOnlyList<ReceivedFrame> wrongGeneration = framer.Append(
                CreateChunk([0xFF], 8, 2, 101));
            IReadOnlyList<ReceivedFrame> timeRollback = framer.Append(
                CreateChunk([0xEE], 7, 2, 99));
            IReadOnlyList<ReceivedFrame> completed = framer.Append(
                CreateChunk(frame[3..], 7, 2, 102));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(wrongGeneration.Single().Kind, Is.EqualTo(ReceivedFrameKind.ProtocolError));
                Assert.That(timeRollback.Single().Kind, Is.EqualTo(ReceivedFrameKind.ProtocolError));
                Assert.That(completed.Single().Data.ToArray(), Is.EqualTo(frame));
            }));
        }

        /// <summary>
        /// 验证接收记录构造与公开数据属性都使用防御性复制。
        /// </summary>
        [Test]
        public void ReceivedFrame_Data_UsesDefensiveCopies()
        {
            byte[] source = [0x01, 0x02, 0x03, 0x04];
            ReceivedFrame frame = new(
                ReceivedFrameKind.RawCrcFrame,
                source,
                1,
                1,
                1,
                1,
                0,
                0,
                true,
                string.Empty,
                source.Length,
                0);
            source[0] = 0xFF;
            byte[] exposed = frame.Data.ToArray();
            exposed[1] = 0xEE;

            Assert.That(frame.Data.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
        }

        /// <summary>
        /// 创建使用生产容量、默认超时和受控时间源的组帧器。
        /// </summary>
        /// <param name="timeProvider">测试使用的确定性时间源。</param>
        /// <returns>新的 RTU 接收组帧器。</returns>
        private static RtuReceiveFramer CreateFramer(ManualTimeProvider timeProvider)
        {
            return new RtuReceiveFramer(RtuFramerOptions.CreateDefault(timeProvider));
        }

        /// <summary>
        /// 创建包含指定数据和来源元数据的不可变接收块。
        /// </summary>
        /// <param name="data">接收块线路字节。</param>
        /// <param name="generation">端口会话代次。</param>
        /// <param name="sequence">会话内接收序号。</param>
        /// <param name="timestamp">单调到达时间戳。</param>
        /// <returns>新的接收块。</returns>
        private static SerialReceiveChunk CreateChunk(
            ReadOnlySpan<byte> data,
            int generation,
            long sequence,
            long timestamp)
        {
            return new SerialReceiveChunk(
                data,
                generation,
                sequence,
                DateTimeOffset.UnixEpoch,
                timestamp);
        }

        /// <summary>
        /// 创建一个 CRC 正确的已知结构响应帧。
        /// </summary>
        /// <param name="functionCode">0x03、0x06、0x10 或异常功能码。</param>
        /// <returns>包含低字节在前 CRC 的完整帧。</returns>
        private static byte[] CreateFrameForFunction(byte functionCode)
        {
            return functionCode switch
            {
                0x03 => ModbusCrc16.Append([0x01, 0x03, 0x04, 0x00, 0x01, 0x00, 0x02]),
                0x06 => ModbusCrc16.Append([0x01, 0x06, 0x00, 0x01, 0x00, 0x02]),
                0x10 => ModbusCrc16.Append([0x01, 0x10, 0x00, 0x02, 0x00, 0x03]),
                0x83 => ModbusCrc16.Append([0x01, 0x83, 0x02]),
                _ => throw new ArgumentOutOfRangeException(nameof(functionCode)),
            };
        }

        /// <summary>
        /// 创建从缓冲头开始在 4 至 256 字节范围内都没有有效 CRC 的未知结构数据。
        /// </summary>
        /// <param name="length">需要创建的数据总长度。</param>
        /// <returns>适用于确定性溢出测试的新数组。</returns>
        private static byte[] CreateUnknownNonCrcData(int length)
        {
            byte[] data = Enumerable.Repeat((byte)0xA5, length).ToArray();
            data[1] = 0x44;
            int maximumCandidate = Math.Min(length, 256);

            for (int candidateLength = 4; candidateLength <= maximumCandidate; candidateLength++)
            {
                if (ModbusCrc16.IsValid(data.AsSpan(0, candidateLength)))
                {
                    data[candidateLength - 1] ^= 0x01;
                }
            }

            return data;
        }
    }
}
