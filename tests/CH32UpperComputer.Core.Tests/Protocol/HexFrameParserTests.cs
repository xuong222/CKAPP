using CH32UpperComputer.Core.Protocol;

using System.Runtime.InteropServices;

namespace CH32UpperComputer.Core.Tests.Protocol
{
    /// <summary>
    /// 验证十六进制帧文本的解析、校验、格式化和尾随 CRC 检测行为。
    /// </summary>
    public sealed class HexFrameParserTests
    {
        /// <summary>
        /// 验证大小写混合的十六进制数字均可解析为相同字节值。
        /// </summary>
        [Test]
        public void TryParse_MixedCaseHexDigits_ReturnsExpectedBytes()
        {
            HexFrameParseResult result = HexFrameParser.TryParse("0a Ab cD EF", 256);

            Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
            Assert.That(result.Bytes.ToArray(), Is.EqualTo(new byte[] { 0x0A, 0xAB, 0xCD, 0xEF }));
        }

        /// <summary>
        /// 验证空格、制表符、回车、换行和短横线均可作为字节分隔符。
        /// </summary>
        [Test]
        public void TryParse_AllowedSeparators_ReturnsExpectedBytes()
        {
            HexFrameParseResult result = HexFrameParser.TryParse("01 02\t03\r\n04-05", 256);

            Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
            Assert.That(result.Bytes.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }));
        }

        /// <summary>
        /// 验证奇数个十六进制数字会返回可操作的补全提示。
        /// </summary>
        [Test]
        public void TryParse_OddHexDigitCount_ReturnsActionableError()
        {
            HexFrameParseResult result = HexFrameParser.TryParse("01 A", 256);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("偶数"));
            Assert.That(result.ErrorMessage, Does.Contain("3"));
        }

        /// <summary>
        /// 验证非法字符错误会指出字符本身及其在原输入中的位置。
        /// </summary>
        [Test]
        public void TryParse_IllegalCharacter_ReturnsCharacterAndPosition()
        {
            HexFrameParseResult result = HexFrameParser.TryParse("01 G2", 256);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("G"));
            Assert.That(result.ErrorMessage, Does.Contain("位置 4"));
        }

        /// <summary>
        /// 验证空引用、空字符串以及仅含分隔符的输入均返回明确校验错误。
        /// </summary>
        [Test]
        public void TryParse_EmptyInput_ReturnsValidationError()
        {
            string?[] emptyInputs = [null, string.Empty, " \t\r\n-"];

            foreach (string? input in emptyInputs)
            {
                HexFrameParseResult result = HexFrameParser.TryParse(input, 256);

                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.ErrorMessage, Does.Contain("不能为空"));
            }
        }

        /// <summary>
        /// 验证解析后的字节数超过指定上限时返回包含上限值的校验错误。
        /// </summary>
        [Test]
        public void TryParse_MoreThanMaximumBytes_ReturnsValidationError()
        {
            string text = string.Join(' ', Enumerable.Repeat("00", 257));

            HexFrameParseResult result = HexFrameParser.TryParse(text, 256);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("256"));
        }

        /// <summary>
        /// 验证字节格式化结果统一使用大写两位十六进制并以单个空格连接。
        /// </summary>
        [Test]
        public void Format_Bytes_ReturnsUppercaseTwoDigitHexSeparatedBySingleSpaces()
        {
            byte[] bytes = [0x00, 0x0A, 0xA5, 0xFF];

            string text = HexFrameParser.Format(bytes);

            Assert.That(text, Is.EqualTo("00 0A A5 FF"));
        }

        /// <summary>
        /// 验证解析结果能够识别输入末尾已经存在的有效 Modbus CRC16。
        /// </summary>
        [Test]
        public void TryParse_FrameWithValidTrailingCrc_ReportsCrcPresent()
        {
            HexFrameParseResult result = HexFrameParser.TryParse("01 03 00 00 00 01 84 0A", 256);

            Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
            Assert.That(result.HasValidTrailingCrc, Is.True);
        }

        /// <summary>
        /// 验证不含有效尾随 CRC 的普通报文体不会被误判为完整 CRC 帧。
        /// </summary>
        [Test]
        public void TryParse_FrameWithoutValidTrailingCrc_ReportsCrcAbsent()
        {
            HexFrameParseResult result = HexFrameParser.TryParse("01 03 00 00 00 01", 256);

            Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
            Assert.That(result.HasValidTrailingCrc, Is.False);
        }

        /// <summary>
        /// 验证调用方即使从一次字节视图中取回并修改底层数组，也不能改变解析结果的内部帧数据。
        /// </summary>
        [Test]
        public void Bytes_MutatingRecoveredBackingArray_DoesNotChangeResult()
        {
            byte[] expectedFrame = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A];
            HexFrameParseResult result = HexFrameParser.TryParse("01 03 00 00 00 01 84 0A", 256);
            ReadOnlyMemory<byte> exposedBytes = result.Bytes;

            bool recoveredArray = MemoryMarshal.TryGetArray(exposedBytes, out ArraySegment<byte> backingSegment);

            Assert.That(recoveredArray, Is.True);
            Assert.That(backingSegment.Array, Is.Not.Null);

            backingSegment.Array![backingSegment.Offset] ^= 0xFF;

            Assert.That(result.Bytes.ToArray(), Is.EqualTo(expectedFrame));
            Assert.That(result.HasValidTrailingCrc, Is.True);
        }
    }
}
