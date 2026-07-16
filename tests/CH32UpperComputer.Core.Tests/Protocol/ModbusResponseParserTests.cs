using CH32UpperComputer.Core.Protocol;

using System.Runtime.InteropServices;

namespace CH32UpperComputer.Core.Tests.Protocol
{
    /// <summary>
    /// 验证 Modbus RTU 正常响应、异常响应、请求签名匹配和未知地址查询专用匹配。
    /// </summary>
    public sealed class ModbusResponseParserTests
    {
        /// <summary>
        /// 验证 0x03 正常响应要求字节数等于请求数量两倍，并按大端顺序恢复寄存器字。
        /// </summary>
        [Test]
        public void Parse_ValidReadResponse_ReturnsSucceededWithBigEndianRegisters()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0020, 2);
            byte[] frame = [0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD, 0x00, 0x20];

            ModbusResponse response = ModbusResponseParser.Parse(request, frame);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.Succeeded), response.ErrorMessage);
                Assert.That(response.IsSuccess, Is.True);
                Assert.That(response.SlaveAddress, Is.EqualTo(1));
                Assert.That(response.Registers.ToArray(), Is.EqualTo(new ushort[] { 0x1234, 0xABCD }));
                Assert.That(response.ExceptionCode, Is.Null);
                Assert.That(response.DiscoveredSlaveAddress, Is.Null);
                Assert.That(response.RawFrame.ToArray(), Is.EqualTo(frame));
            }));
        }

        /// <summary>
        /// 验证 0x03 响应拒绝错误字节数、缺失数据和额外数据，不能只匹配地址与功能码。
        /// </summary>
        [Test]
        public void Parse_ReadResponseWithWrongByteCountOrLength_ReturnsProtocolError()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0020, 2);
            byte[][] invalidFrames =
            [
                ModbusCrc16.Append([0x01, 0x03, 0x02, 0x12, 0x34]),
                ModbusCrc16.Append([0x01, 0x03, 0x04, 0x12, 0x34]),
                ModbusCrc16.Append([0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD, 0x00]),
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusResponse response = ModbusResponseParser.Parse(request, frame);

                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.ProtocolError));
                Assert.That(response.IsSuccess, Is.False);
                Assert.That(response.ErrorMessage, Is.Not.Empty);
            }
        }

        /// <summary>
        /// 验证 0x06 固定八字节响应只有完整回显请求寄存器地址和值时才成功。
        /// </summary>
        [Test]
        public void Parse_ValidWriteSingleEcho_ReturnsSucceeded()
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(1, 0x0001, 0x1234);
            byte[] frame = [0x01, 0x06, 0x00, 0x01, 0x12, 0x34, 0xD5, 0x7D];

            ModbusResponse response = ModbusResponseParser.Parse(request, frame);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.Succeeded), response.ErrorMessage);
                Assert.That(response.Registers.ToArray(), Is.Empty);
                Assert.That(response.RawFrame.ToArray(), Is.EqualTo(frame));
            }));
        }

        /// <summary>
        /// 验证 0x06 响应拒绝错误寄存器地址、错误值和任何非八字节回显。
        /// </summary>
        [Test]
        public void Parse_WriteSingleWithWrongEcho_ReturnsProtocolError()
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(1, 0x0001, 0x1234);
            byte[][] invalidFrames =
            [
                ModbusCrc16.Append([0x01, 0x06, 0x00, 0x02, 0x12, 0x34]),
                ModbusCrc16.Append([0x01, 0x06, 0x00, 0x01, 0x12, 0x35]),
                ModbusCrc16.Append([0x01, 0x06, 0x00, 0x01, 0x12, 0x34, 0x00]),
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusResponse response = ModbusResponseParser.Parse(request, frame);

                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.ProtocolError));
            }
        }

        /// <summary>
        /// 验证 0x10 固定八字节响应精确回显请求起始地址与寄存器数量。
        /// </summary>
        [Test]
        public void Parse_ValidWriteMultipleEcho_ReturnsSucceeded()
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                1,
                0x0020,
                [0x1234, 0xABCD]);
            byte[] frame = [0x01, 0x10, 0x00, 0x20, 0x00, 0x02, 0x40, 0x02];

            ModbusResponse response = ModbusResponseParser.Parse(request, frame);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.Succeeded), response.ErrorMessage);
                Assert.That(response.SlaveAddress, Is.EqualTo(1));
                Assert.That(response.RawFrame.ToArray(), Is.EqualTo(frame));
            }));
        }

        /// <summary>
        /// 验证 0x10 响应拒绝错误起始地址、错误数量和任何非八字节回显。
        /// </summary>
        [Test]
        public void Parse_WriteMultipleWithWrongEcho_ReturnsProtocolError()
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                1,
                0x0020,
                [0x1234, 0xABCD]);
            byte[][] invalidFrames =
            [
                ModbusCrc16.Append([0x01, 0x10, 0x00, 0x21, 0x00, 0x02]),
                ModbusCrc16.Append([0x01, 0x10, 0x00, 0x20, 0x00, 0x01]),
                ModbusCrc16.Append([0x01, 0x10, 0x00, 0x20, 0x00, 0x02, 0x00]),
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusResponse response = ModbusResponseParser.Parse(request, frame);

                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.ProtocolError));
            }
        }

        /// <summary>
        /// 验证标准异常响应固定为五字节、功能码最高位置位，并保留异常码及中文含义。
        /// </summary>
        /// <param name="exceptionCode">待验证的标准 Modbus 异常码。</param>
        /// <param name="expectedMeaning">异常码对应的中文含义关键字。</param>
        [TestCase(0x01, "非法功能")]
        [TestCase(0x02, "非法数据地址")]
        [TestCase(0x03, "非法数据值")]
        [TestCase(0x04, "从站设备故障")]
        public void Parse_ValidExceptionResponse_ReturnsModbusException(
            byte exceptionCode,
            string expectedMeaning)
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0020, 2);
            byte[] frame = ModbusCrc16.Append([0x01, 0x83, exceptionCode]);

            ModbusResponse response = ModbusResponseParser.Parse(request, frame);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.ModbusException));
                Assert.That(response.IsSuccess, Is.False);
                Assert.That(response.ExceptionCode, Is.EqualTo(exceptionCode));
                Assert.That(response.ExceptionMeaning, Does.Contain(expectedMeaning));
                Assert.That(response.Registers.ToArray(), Is.Empty);
                Assert.That(response.RawFrame.ToArray(), Is.EqualTo(frame));
            }));
        }

        /// <summary>
        /// 验证异常响应拒绝错误异常功能码和任何非五字节结构。
        /// </summary>
        [Test]
        public void Parse_InvalidExceptionStructure_ReturnsProtocolError()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0020, 2);
            byte[][] invalidFrames =
            [
                ModbusCrc16.Append([0x01, 0x86, 0x02]),
                ModbusCrc16.Append([0x01, 0x83, 0x02, 0x00]),
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusResponse response = ModbusResponseParser.Parse(request, frame);

                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.ProtocolError));
                Assert.That(response.ExceptionCode, Is.Null);
            }
        }

        /// <summary>
        /// 验证 CRC 错误、短于五字节和超过 256 字节的响应均返回协议错误而不抛异常。
        /// </summary>
        [Test]
        public void Parse_InvalidCrcOrAduLength_ReturnsProtocolErrorWithoutThrowing()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0020, 2);
            byte[][] invalidFrames =
            [
                [0x01, 0x03, 0x04, 0x12],
                [0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD, 0x01, 0x20],
                new byte[257],
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusResponse? response = null;

                Assert.DoesNotThrow((Action)(() => response = ModbusResponseParser.Parse(request, frame)));
                Assert.That(response, Is.Not.Null);
                Assert.That(response!.Status, Is.EqualTo(ModbusResponseStatus.ProtocolError));
                Assert.That(response.ErrorMessage, Is.Not.Empty);
            }
        }

        /// <summary>
        /// 验证普通请求响应必须同时匹配请求从站地址与原始功能码。
        /// </summary>
        [Test]
        public void Parse_WrongAddressOrFunction_ReturnsProtocolError()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0020, 2);
            byte[][] invalidFrames =
            [
                ModbusCrc16.Append([0x02, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]),
                ModbusCrc16.Append([0x01, 0x04, 0x04, 0x12, 0x34, 0xAB, 0xCD]),
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusResponse response = ModbusResponseParser.Parse(request, frame);

                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.ProtocolError));
            }
        }

        /// <summary>
        /// 验证 0xFE 查询只接受真实地址、单寄存器和“寄存器值等于响应地址”的专用成功响应。
        /// </summary>
        [Test]
        public void Parse_ValidUnknownAddressResponse_ReturnsDiscoveredRealAddress()
        {
            ModbusRequest request = ModbusRequestFactory.CreateUnknownAddressQuery();
            byte[] frame = [0x05, 0x03, 0x02, 0x00, 0x05, 0x89, 0x87];

            ModbusResponse response = ModbusResponseParser.Parse(request, frame);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.Succeeded), response.ErrorMessage);
                Assert.That(response.SlaveAddress, Is.EqualTo(5));
                Assert.That(response.DiscoveredSlaveAddress, Is.EqualTo(5));
                Assert.That(response.DiscoveredSlaveAddress, Is.Not.EqualTo(0xFE));
                Assert.That(response.Registers.ToArray(), Is.EqualTo(new ushort[] { 5 }));
                Assert.That(response.RawFrame.ToArray(), Is.EqualTo(frame));
            }));
        }

        /// <summary>
        /// 验证 0xFE 查询接受真实地址范围 1 至 64 的闭区间边界。
        /// </summary>
        /// <param name="realAddress">待验证的真实响应地址边界值。</param>
        [TestCase(1)]
        [TestCase(64)]
        public void Parse_UnknownAddressResponseAtRealAddressBoundary_ReturnsSucceeded(byte realAddress)
        {
            ModbusRequest request = ModbusRequestFactory.CreateUnknownAddressQuery();
            byte[] frame = ModbusCrc16.Append(
            [
                realAddress,
                0x03,
                0x02,
                0x00,
                realAddress,
            ]);

            ModbusResponse response = ModbusResponseParser.Parse(request, frame);

            Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.Succeeded), response.ErrorMessage);
            Assert.That(response.DiscoveredSlaveAddress, Is.EqualTo(realAddress));
        }

        /// <summary>
        /// 验证 0xFE 查询拒绝非法真实地址、错误功能、错误数量和地址值不一致响应。
        /// </summary>
        [Test]
        public void Parse_InvalidUnknownAddressResponse_ReturnsProtocolErrorAndNeverDiscoversFe()
        {
            ModbusRequest request = ModbusRequestFactory.CreateUnknownAddressQuery();
            byte[][] invalidFrames =
            [
                ModbusCrc16.Append([0x00, 0x03, 0x02, 0x00, 0x00]),
                ModbusCrc16.Append([0x41, 0x03, 0x02, 0x00, 0x41]),
                ModbusCrc16.Append([0xFE, 0x03, 0x02, 0x00, 0xFE]),
                ModbusCrc16.Append([0x05, 0x04, 0x02, 0x00, 0x05]),
                ModbusCrc16.Append([0x05, 0x03, 0x04, 0x00, 0x05, 0x00, 0x05]),
                ModbusCrc16.Append([0x05, 0x03, 0x02, 0x00, 0x06]),
            ];

            foreach (byte[] frame in invalidFrames)
            {
                ModbusResponse response = ModbusResponseParser.Parse(request, frame);

                Assert.That(response.Status, Is.EqualTo(ModbusResponseStatus.ProtocolError));
                Assert.That(response.DiscoveredSlaveAddress, Is.Null);
            }
        }

        /// <summary>
        /// 验证调用方无法经由响应公开的内存视图修改内部原始帧或寄存器字。
        /// </summary>
        [Test]
        public void PublicMemoryViews_MutatingRecoveredArrays_DoesNotChangeResponse()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0020, 2);
            byte[] expectedFrame = [0x01, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD, 0x00, 0x20];
            ushort[] expectedRegisters = [0x1234, 0xABCD];
            ModbusResponse response = ModbusResponseParser.Parse(request, expectedFrame);
            ReadOnlyMemory<byte> exposedFrame = response.RawFrame;
            ReadOnlyMemory<ushort> exposedRegisters = response.Registers;

            bool recoveredFrame = MemoryMarshal.TryGetArray(
                exposedFrame,
                out ArraySegment<byte> frameSegment);
            bool recoveredRegisters = MemoryMarshal.TryGetArray(
                exposedRegisters,
                out ArraySegment<ushort> registerSegment);

            Assert.That(recoveredFrame, Is.True);
            Assert.That(recoveredRegisters, Is.True);
            Assert.That(frameSegment.Array, Is.Not.Null);
            Assert.That(registerSegment.Array, Is.Not.Null);

            frameSegment.Array![frameSegment.Offset] = 0xFF;
            registerSegment.Array![registerSegment.Offset] = 0xFFFF;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(response.RawFrame.ToArray(), Is.EqualTo(expectedFrame));
                Assert.That(response.Registers.ToArray(), Is.EqualTo(expectedRegisters));
            }));
        }
    }
}
