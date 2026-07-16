using CH32UpperComputer.Core.Protocol;

namespace CH32UpperComputer.Core.Tests.Protocol
{
    /// <summary>
    /// 验证 Modbus RTU CRC16 的计算、线序追加和完整帧校验行为。
    /// </summary>
    public sealed class ModbusCrc16Tests
    {
        /// <summary>
        /// 验证标准读保持寄存器请求体能够得到已知 CRC16 值。
        /// </summary>
        [Test]
        public void Compute_KnownReadRequest_ReturnsExpectedCrc()
        {
            byte[] body = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01];

            ushort crc = ModbusCrc16.Compute(body);

            Assert.That(crc, Is.EqualTo(0x0A84));
        }

        /// <summary>
        /// 验证追加 CRC 时遵循低字节在前的 Modbus RTU 线序，且不修改调用方输入。
        /// </summary>
        [Test]
        public void Append_KnownReadRequest_AppendsLowByteThenHighByteWithoutMutatingInput()
        {
            byte[] body = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01];
            byte[] originalBody = (byte[])body.Clone();

            byte[] frame = ModbusCrc16.Append(body);

            Assert.That(frame, Is.EqualTo(new byte[]
            {
                0x01,
                0x03,
                0x00,
                0x00,
                0x00,
                0x01,
                0x84,
                0x0A,
            }));
            Assert.That(body, Is.EqualTo(originalBody));
            Assert.That(frame, Is.Not.SameAs(body));
        }

        /// <summary>
        /// 验证带有正确尾随 CRC 的完整帧可通过校验。
        /// </summary>
        [Test]
        public void IsValid_FrameWithCorrectTrailingCrc_ReturnsTrue()
        {
            byte[] frame = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A];

            bool isValid = ModbusCrc16.IsValid(frame);

            Assert.That(isValid, Is.True);
        }

        /// <summary>
        /// 验证不足以包含报文体和两字节 CRC 的短帧不能通过校验。
        /// </summary>
        [Test]
        public void IsValid_ShortFrame_ReturnsFalse()
        {
            byte[][] shortFrames =
            [
                [],
                [0x01],
                [0x84, 0x0A],
            ];

            foreach (byte[] frame in shortFrames)
            {
                Assert.That(ModbusCrc16.IsValid(frame), Is.False);
            }
        }

        /// <summary>
        /// 验证任一数据字节被篡改后，原有尾随 CRC 不再有效。
        /// </summary>
        [Test]
        public void IsValid_TamperedFrame_ReturnsFalse()
        {
            byte[] tamperedFrame = [0x01, 0x03, 0x00, 0x01, 0x00, 0x01, 0x84, 0x0A];

            Assert.That(ModbusCrc16.IsValid(tamperedFrame), Is.False);
        }
    }
}
