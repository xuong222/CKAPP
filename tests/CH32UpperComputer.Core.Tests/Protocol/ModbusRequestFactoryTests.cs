using CH32UpperComputer.Core.Protocol;

using System.Runtime.InteropServices;

namespace CH32UpperComputer.Core.Tests.Protocol
{
    /// <summary>
    /// 验证标准 Modbus RTU 请求和未知地址查询请求的构建、边界校验与不可变性。
    /// </summary>
    public sealed class ModbusRequestFactoryTests
    {
        /// <summary>
        /// 验证 0x03 请求按大端字段和低字节在前的 CRC 生成完整标准帧及响应签名。
        /// </summary>
        [Test]
        public void CreateReadHoldingRegisters_ValidArguments_ReturnsExactFrameAndSignature()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                0x01,
                0x0000,
                0x0001);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(request.SlaveAddress, Is.EqualTo(0x01));
                Assert.That(request.FunctionCode, Is.EqualTo(ModbusFunctionCode.ReadHoldingRegisters));
                Assert.That(request.StartAddress, Is.EqualTo(0x0000));
                Assert.That(request.Quantity, Is.EqualTo(0x0001));
                Assert.That(request.Values.ToArray(), Is.Empty);
                Assert.That(request.ExpectedResponseLength, Is.EqualTo(7));
                Assert.That(request.IsUnknownAddressQuery, Is.False);
                Assert.That(request.RawFrame.ToArray(), Is.EqualTo(new byte[]
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
            }));
        }

        /// <summary>
        /// 验证 0x03 接受普通从站地址和数量的闭区间边界。
        /// </summary>
        [Test]
        public void CreateReadHoldingRegisters_InclusiveBoundaries_AcceptsArguments()
        {
            ModbusRequest lowest = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0000, 1);
            ModbusRequest highest = ModbusRequestFactory.CreateReadHoldingRegisters(64, 0x0000, 125);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(lowest.SlaveAddress, Is.EqualTo(1));
                Assert.That(lowest.Quantity, Is.EqualTo(1));
                Assert.That(highest.SlaveAddress, Is.EqualTo(64));
                Assert.That(highest.Quantity, Is.EqualTo(125));
                Assert.That(highest.ExpectedResponseLength, Is.EqualTo(255));
            }));
        }

        /// <summary>
        /// 验证 0x03 对非法从站地址和寄存器数量抛出指向对应参数的范围异常。
        /// </summary>
        [Test]
        public void CreateReadHoldingRegisters_OutOfRangeArguments_ThrowsClearException()
        {
            ArgumentOutOfRangeException zeroAddress = Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => ModbusRequestFactory.CreateReadHoldingRegisters(0, 0x0000, 1)))!;
            ArgumentOutOfRangeException excessiveAddress = Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => ModbusRequestFactory.CreateReadHoldingRegisters(65, 0x0000, 1)))!;
            ArgumentOutOfRangeException zeroQuantity = Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0000, 0)))!;
            ArgumentOutOfRangeException excessiveQuantity = Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0000, 126)))!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(zeroAddress.ParamName, Is.EqualTo("slaveAddress"));
                Assert.That(excessiveAddress.ParamName, Is.EqualTo("slaveAddress"));
                Assert.That(zeroQuantity.ParamName, Is.EqualTo("quantity"));
                Assert.That(excessiveQuantity.ParamName, Is.EqualTo("quantity"));
            }));
        }

        /// <summary>
        /// 验证 0x06 请求按大端地址和值生成固定八字节完整帧与精确回显签名。
        /// </summary>
        [Test]
        public void CreateWriteSingleRegister_ValidArguments_ReturnsExactFrameAndSignature()
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                0x01,
                0x0001,
                0x1234);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(request.FunctionCode, Is.EqualTo(ModbusFunctionCode.WriteSingleRegister));
                Assert.That(request.StartAddress, Is.EqualTo(0x0001));
                Assert.That(request.Quantity, Is.EqualTo(1));
                Assert.That(request.Values.ToArray(), Is.EqualTo(new ushort[] { 0x1234 }));
                Assert.That(request.ExpectedResponseLength, Is.EqualTo(8));
                Assert.That(request.RawFrame.ToArray(), Is.EqualTo(new byte[]
                {
                    0x01,
                    0x06,
                    0x00,
                    0x01,
                    0x12,
                    0x34,
                    0xD5,
                    0x7D,
                }));
            }));
        }

        /// <summary>
        /// 验证 0x06 仅接受地址 1 至 64 的普通从站。
        /// </summary>
        [Test]
        public void CreateWriteSingleRegister_InvalidSlaveAddress_ThrowsClearException()
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => ModbusRequestFactory.CreateWriteSingleRegister(65, 0x0001, 0x1234)))!;

            Assert.That(exception.ParamName, Is.EqualTo("slaveAddress"));
        }

        /// <summary>
        /// 验证 0x10 请求由值数量推导数量和字节数，并按大端顺序编码每个寄存器字。
        /// </summary>
        [Test]
        public void CreateWriteMultipleRegisters_ValidValues_ReturnsExactFrameAndSignature()
        {
            ushort[] values = [0x1234, 0xABCD];

            ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                0x01,
                0x0020,
                values);

            values[0] = 0xFFFF;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(request.FunctionCode, Is.EqualTo(ModbusFunctionCode.WriteMultipleRegisters));
                Assert.That(request.StartAddress, Is.EqualTo(0x0020));
                Assert.That(request.Quantity, Is.EqualTo(2));
                Assert.That(request.Values.ToArray(), Is.EqualTo(new ushort[] { 0x1234, 0xABCD }));
                Assert.That(request.ExpectedResponseLength, Is.EqualTo(8));
                Assert.That(request.RawFrame.ToArray(), Is.EqualTo(new byte[]
                {
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
                }));
            }));
        }

        /// <summary>
        /// 验证 0x10 拒绝空值集合和超过协议上限 123 的值集合。
        /// </summary>
        [Test]
        public void CreateWriteMultipleRegisters_InvalidValueCount_ThrowsClearException()
        {
            ArgumentException emptyException = Assert.Throws<ArgumentException>(
                (Action)(() => ModbusRequestFactory.CreateWriteMultipleRegisters(1, 0x0000, [])))!;
            ArgumentOutOfRangeException excessiveException = Assert.Throws<ArgumentOutOfRangeException>(
                (Action)(() => ModbusRequestFactory.CreateWriteMultipleRegisters(
                    1,
                    0x0000,
                    new ushort[124])))!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(emptyException.ParamName, Is.EqualTo("values"));
                Assert.That(excessiveException.ParamName, Is.EqualTo("values"));
            }));
        }

        /// <summary>
        /// 验证未知地址查询始终生成固件唯一支持的固定 0xFE 查询帧。
        /// </summary>
        [Test]
        public void CreateUnknownAddressQuery_ReturnsExactFixedFrameAndSignature()
        {
            ModbusRequest request = ModbusRequestFactory.CreateUnknownAddressQuery();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(request.SlaveAddress, Is.EqualTo(0xFE));
                Assert.That(request.FunctionCode, Is.EqualTo(ModbusFunctionCode.ReadHoldingRegisters));
                Assert.That(request.StartAddress, Is.EqualTo(0x0000));
                Assert.That(request.Quantity, Is.EqualTo(1));
                Assert.That(request.ExpectedResponseLength, Is.EqualTo(7));
                Assert.That(request.IsUnknownAddressQuery, Is.True);
                Assert.That(request.RawFrame.ToArray(), Is.EqualTo(new byte[]
                {
                    0xFE,
                    0x03,
                    0x00,
                    0x00,
                    0x00,
                    0x01,
                    0x90,
                    0x05,
                }));
            }));
        }

        /// <summary>
        /// 验证调用方无法经由请求公开的内存视图修改内部原始帧或写入值签名。
        /// </summary>
        [Test]
        public void PublicMemoryViews_MutatingRecoveredArrays_DoesNotChangeRequest()
        {
            byte[] expectedFrame =
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
            ushort[] expectedValues = [0x1234, 0xABCD];
            ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                1,
                0x0020,
                expectedValues);
            ReadOnlyMemory<byte> exposedFrame = request.RawFrame;
            ReadOnlyMemory<ushort> exposedValues = request.Values;

            bool recoveredFrame = MemoryMarshal.TryGetArray(
                exposedFrame,
                out ArraySegment<byte> frameSegment);
            bool recoveredValues = MemoryMarshal.TryGetArray(
                exposedValues,
                out ArraySegment<ushort> valuesSegment);

            Assert.That(recoveredFrame, Is.True);
            Assert.That(recoveredValues, Is.True);
            Assert.That(frameSegment.Array, Is.Not.Null);
            Assert.That(valuesSegment.Array, Is.Not.Null);

            frameSegment.Array![frameSegment.Offset] = 0xFF;
            valuesSegment.Array![valuesSegment.Offset] = 0xFFFF;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(request.RawFrame.ToArray(), Is.EqualTo(expectedFrame));
                Assert.That(request.Values.ToArray(), Is.EqualTo(expectedValues));
            }));
        }
    }
}
