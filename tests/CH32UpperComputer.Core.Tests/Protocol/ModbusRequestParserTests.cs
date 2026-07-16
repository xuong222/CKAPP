using CH32UpperComputer.Core.Protocol;

namespace CH32UpperComputer.Core.Tests.Protocol
{
    /// <summary>
    /// 验证手工输入的完整 Modbus RTU 帧只在严格匹配受支持标准请求时被识别。
    /// </summary>
    public sealed class ModbusRequestParserTests
    {
        /// <summary>
        /// 验证完整合法的 0x03 帧可恢复为带有读响应签名的标准请求。
        /// </summary>
        [Test]
        public void TryParseSupported_ValidReadFrame_RestoresRequestSignature()
        {
            byte[] frame = [0x01, 0x03, 0x00, 0x20, 0x00, 0x02, 0xC5, 0xC1];

            ModbusRequestParseResult result = ModbusRequestParser.TryParseSupported(frame);

            Assert.That(result.Status, Is.EqualTo(ModbusRequestParseStatus.Succeeded), result.ErrorMessage);
            Assert.That(result.Request, Is.Not.Null);
            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Request!.SlaveAddress, Is.EqualTo(1));
                Assert.That(result.Request.FunctionCode, Is.EqualTo(ModbusFunctionCode.ReadHoldingRegisters));
                Assert.That(result.Request.StartAddress, Is.EqualTo(0x0020));
                Assert.That(result.Request.Quantity, Is.EqualTo(2));
                Assert.That(result.Request.ExpectedResponseLength, Is.EqualTo(9));
                Assert.That(result.Request.IsUnknownAddressQuery, Is.False);
            }));
        }

        /// <summary>
        /// 验证完整合法的 0x06 帧可恢复为带有精确地址和值回显签名的标准请求。
        /// </summary>
        [Test]
        public void TryParseSupported_ValidWriteSingleFrame_RestoresEchoSignature()
        {
            byte[] frame = [0x01, 0x06, 0x00, 0x01, 0x12, 0x34, 0xD5, 0x7D];

            ModbusRequestParseResult result = ModbusRequestParser.TryParseSupported(frame);

            Assert.That(result.Status, Is.EqualTo(ModbusRequestParseStatus.Succeeded), result.ErrorMessage);
            Assert.That(result.Request, Is.Not.Null);
            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Request!.FunctionCode, Is.EqualTo(ModbusFunctionCode.WriteSingleRegister));
                Assert.That(result.Request.StartAddress, Is.EqualTo(0x0001));
                Assert.That(result.Request.Quantity, Is.EqualTo(1));
                Assert.That(result.Request.Values.ToArray(), Is.EqualTo(new ushort[] { 0x1234 }));
                Assert.That(result.Request.ExpectedResponseLength, Is.EqualTo(8));
            }));
        }

        /// <summary>
        /// 验证完整合法的 0x10 帧可恢复为带有起始地址、数量和值序列的标准请求。
        /// </summary>
        [Test]
        public void TryParseSupported_ValidWriteMultipleFrame_RestoresEchoSignature()
        {
            byte[] frame =
            [
                0x01,
                0x10,
                0x00,
                0x20,
                0x00,
                0x02,
                0x04,
                0x12,
                0x34,
                0xAB,
                0xCD,
                0x0B,
                0xA4,
            ];

            ModbusRequestParseResult result = ModbusRequestParser.TryParseSupported(frame);

            Assert.That(result.Status, Is.EqualTo(ModbusRequestParseStatus.Succeeded), result.ErrorMessage);
            Assert.That(result.Request, Is.Not.Null);
            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Request!.FunctionCode, Is.EqualTo(ModbusFunctionCode.WriteMultipleRegisters));
                Assert.That(result.Request.StartAddress, Is.EqualTo(0x0020));
                Assert.That(result.Request.Quantity, Is.EqualTo(2));
                Assert.That(result.Request.Values.ToArray(), Is.EqualTo(new ushort[] { 0x1234, 0xABCD }));
                Assert.That(result.Request.ExpectedResponseLength, Is.EqualTo(8));
            }));
        }

        /// <summary>
        /// 验证唯一合法的 0xFE 固定查询帧可恢复为未知地址查询请求。
        /// </summary>
        [Test]
        public void TryParseSupported_ExactUnknownAddressQuery_RestoresSpecialRequest()
        {
            byte[] frame = [0xFE, 0x03, 0x00, 0x00, 0x00, 0x01, 0x90, 0x05];

            ModbusRequestParseResult result = ModbusRequestParser.TryParseSupported(frame);

            Assert.That(result.Status, Is.EqualTo(ModbusRequestParseStatus.Succeeded), result.ErrorMessage);
            Assert.That(result.Request, Is.Not.Null);
            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Request!.SlaveAddress, Is.EqualTo(0xFE));
                Assert.That(result.Request.StartAddress, Is.Zero);
                Assert.That(result.Request.Quantity, Is.EqualTo(1));
                Assert.That(result.Request.IsUnknownAddressQuery, Is.True);
            }));
        }

        /// <summary>
        /// 验证 CRC 错误作为非抛出“不支持标准识别”结果返回，以便上层转入 RawDebug。
        /// </summary>
        [Test]
        public void TryParseSupported_InvalidCrc_ReturnsNotSupportedWithoutThrowing()
        {
            byte[] frame = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x85, 0x0A];

            ModbusRequestParseResult? result = null;

            Assert.DoesNotThrow((Action)(() => result = ModbusRequestParser.TryParseSupported(frame)));
            Assert.That(result, Is.Not.Null);
            Assert.Multiple((Action)(() =>
            {
                Assert.That(result!.Status, Is.EqualTo(ModbusRequestParseStatus.NotSupported));
                Assert.That(result.Request, Is.Null);
                Assert.That(result.ErrorMessage, Does.Contain("CRC"));
            }));
        }

        /// <summary>
        /// 验证未知功能码即使 CRC 正确也不会被误识别为标准请求。
        /// </summary>
        [Test]
        public void TryParseSupported_UnknownFunctionCode_ReturnsNotSupported()
        {
            byte[] frame = ModbusCrc16.Append([0x01, 0x04, 0x00, 0x00, 0x00, 0x01]);

            ModbusRequestParseResult result = ModbusRequestParser.TryParseSupported(frame);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Status, Is.EqualTo(ModbusRequestParseStatus.NotSupported));
                Assert.That(result.Request, Is.Null);
                Assert.That(result.ErrorMessage, Does.Contain("功能码"));
            }));
        }

        /// <summary>
        /// 验证过短、过长或结构字段矛盾的标准功能帧均被拒绝且不会抛异常。
        /// </summary>
        [Test]
        public void TryParseSupported_InvalidStandardStructures_ReturnNotSupportedWithoutThrowing()
        {
            byte[][] invalidFrames =
            [
                [0x01, 0x03, 0x00, 0x00],
                ModbusCrc16.Append([0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00]),
                ModbusCrc16.Append([0x01, 0x10, 0x00, 0x20, 0x00, 0x02, 0x02, 0x12, 0x34]),
                ModbusCrc16.Append([0x01, 0x10, 0x00, 0x20, 0x00, 0x01, 0x03, 0x12, 0x34, 0x56]),
                ModbusCrc16.Append([0x01, 0x10, 0x00, 0x20, 0x00, 0x02, 0x04, 0x12, 0x34]),
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusRequestParseResult? result = null;

                Assert.DoesNotThrow((Action)(() => result = ModbusRequestParser.TryParseSupported(frame)));
                Assert.That(result, Is.Not.Null);
                Assert.That(result!.Status, Is.EqualTo(ModbusRequestParseStatus.NotSupported));
                Assert.That(result.Request, Is.Null);
                Assert.That(result.ErrorMessage, Is.Not.Empty);
            }
        }

        /// <summary>
        /// 验证 0xFE 地址上的任何非固定查询即使结构和 CRC 合法也保留给 RawDebug。
        /// </summary>
        [Test]
        public void TryParseSupported_NonExactUnknownAddressFrames_ReturnNotSupported()
        {
            byte[][] unsupportedFrames =
            [
                ModbusCrc16.Append([0xFE, 0x03, 0x00, 0x01, 0x00, 0x01]),
                ModbusCrc16.Append([0xFE, 0x03, 0x00, 0x00, 0x00, 0x02]),
                ModbusCrc16.Append([0xFE, 0x06, 0x00, 0x00, 0x00, 0x01]),
            ];

            foreach (byte[] frame in unsupportedFrames)
            {
                ModbusRequestParseResult result = ModbusRequestParser.TryParseSupported(frame);

                Assert.That(result.Status, Is.EqualTo(ModbusRequestParseStatus.NotSupported));
                Assert.That(result.Request, Is.Null);
            }
        }

        /// <summary>
        /// 验证普通请求地址超出 1 至 64 时，解析器复用工厂校验并返回非抛出失败。
        /// </summary>
        [Test]
        public void TryParseSupported_OutOfRangeOrdinaryAddress_ReturnsNotSupported()
        {
            byte[] frame = ModbusCrc16.Append([0x41, 0x03, 0x00, 0x00, 0x00, 0x01]);

            ModbusRequestParseResult? result = null;

            Assert.DoesNotThrow((Action)(() => result = ModbusRequestParser.TryParseSupported(frame)));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Status, Is.EqualTo(ModbusRequestParseStatus.NotSupported));
            Assert.That(result.Request, Is.Null);
        }
    }
}
