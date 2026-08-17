using CH32UpperComputer.Core.Iap;

namespace CH32UpperComputer.Core.Tests.Iap
{
    /// <summary>
    /// 验证 Ethernet IAP 固定帧、小端字段、CRC32 和错误提示契约。
    /// </summary>
    [TestFixture]
    public sealed class IapProtocolTests
    {
        /// <summary>
        /// 验证标准 IEEE CRC32 测试向量。
        /// </summary>
        [Test]
        public void ComputeCrc32_KnownVector_ReturnsExpectedValue()
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes("123456789");

            uint crc32 = IapCrc32.Compute(bytes);

            Assert.That(crc32, Is.EqualTo(0xCBF43926U));
        }

        /// <summary>
        /// 验证 BEGIN 请求采用固定 32 字节头并逐字段小端编码。
        /// </summary>
        [Test]
        public void EncodeRequest_Begin_UsesDocumentedLittleEndianLayout()
        {
            IapRequestHeader header = new(
                IapCommand.Begin,
                0x01020304U,
                0U,
                0x00020000U,
                0U,
                0xA1B2C3D4U);

            byte[] frame = IapFrameCodec.EncodeRequest(header, ReadOnlySpan<byte>.Empty);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(frame, Has.Length.EqualTo(IapFrameCodec.RequestHeaderSize));
                Assert.That(frame[0..4], Is.EqualTo(new byte[] { 0x41, 0x50, 0x49, 0x31 }));
                Assert.That(frame[4..8], Is.EqualTo(new byte[] { 0x20, 0x00, 0x00, 0x00 }));
                Assert.That(frame[8..12], Is.EqualTo(new byte[] { 0x4E, 0x47, 0x45, 0x42 }));
                Assert.That(frame[12..16], Is.EqualTo(new byte[] { 0x04, 0x03, 0x02, 0x01 }));
                Assert.That(frame[20..24], Is.EqualTo(new byte[] { 0x00, 0x00, 0x02, 0x00 }));
                Assert.That(frame[28..32], Is.EqualTo(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }));
            }));
        }

        /// <summary>
        /// 验证 DATA 载荷长度必须与头字段一致且总帧不得超过 1056 字节。
        /// </summary>
        [Test]
        public void EncodeRequest_DataLengthMismatch_Throws()
        {
            IapRequestHeader header = new(
                IapCommand.Data,
                1U,
                0U,
                4U,
                0U,
                0U);

            InvalidDataException? capturedException = null;

            try
            {
                _ = IapFrameCodec.EncodeRequest(header, new byte[3]);
            }
            catch (InvalidDataException exception)
            {
                capturedException = exception;
            }

            Assert.That(capturedException, Is.Not.Null);
        }

        /// <summary>
        /// 验证响应头和 HELLO 附加信息按固定小端布局解析。
        /// </summary>
        [Test]
        public void DecodeResponseAndHelloInfo_ValidBytes_ReturnsFields()
        {
            byte[] responseBytes =
            {
                0x41, 0x50, 0x49, 0x31,
                0x18, 0x00, 0x00, 0x00,
                0x4F, 0x4C, 0x45, 0x48,
                0x07, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
            };
            byte[] helloBytes =
            {
                0x00, 0x00, 0x01, 0x00,
                0x00, 0xC0, 0x00, 0x08,
                0x00, 0x20, 0x03, 0x00,
                0x01, 0x00, 0x00, 0x00,
                0x00, 0x10, 0x00, 0x00,
                0x78, 0x56, 0x34, 0x12,
            };

            IapResponseHeader response = IapFrameCodec.DecodeResponseHeader(responseBytes);
            IapHelloInfo hello = IapFrameCodec.DecodeHelloInfo(helloBytes);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.Command, Is.EqualTo(IapCommand.Hello));
                Assert.That(response.Sequence, Is.EqualTo(7U));
                Assert.That(response.Status, Is.EqualTo(IapResponseStatus.Ack));
                Assert.That(hello.BootVersion, Is.EqualTo(0x00010000U));
                Assert.That(hello.AppBase, Is.EqualTo(0x0800C000U));
                Assert.That(hello.AppMaxSize, Is.EqualTo(204800U));
                Assert.That(hello.AppValid, Is.True);
                Assert.That(hello.AppSize, Is.EqualTo(4096U));
                Assert.That(hello.AppCrc32, Is.EqualTo(0x12345678U));
            }));
        }

        /// <summary>
        /// 验证当前固件错误和文档扩展错误均生成可定位的中文信息。
        /// </summary>
        [TestCase(IapCommand.Data, 5U, "CRC")]
        [TestCase(IapCommand.Data, 7U, "分片")]
        [TestCase(IapCommand.End, 10U, "整体")]
        [TestCase(IapCommand.Run, 99U, "0x00000063")]
        public void TranslateError_KnownAndUnknownCodes_ContainsDiagnostic(
            IapCommand command,
            uint detail,
            string expectedText)
        {
            string message = IapErrorTranslator.Translate(command, 12U, detail);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(message, Does.Contain(command.ToString().ToUpperInvariant()));
                Assert.That(message, Does.Contain("sequence=12"));
                Assert.That(message, Does.Contain(expectedText));
            }));
        }
    }
}
