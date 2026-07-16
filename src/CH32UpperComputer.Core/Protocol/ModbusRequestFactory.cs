namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 提供受支持 Modbus RTU 请求的统一参数校验、线路编码和响应签名构建功能。
    /// </summary>
    public static class ModbusRequestFactory
    {
        /// <summary>
        /// 普通设备允许使用的最小从站地址。
        /// </summary>
        private const byte MinimumSlaveAddress = 1;

        /// <summary>
        /// 当前设备协议允许使用的最大从站地址。
        /// </summary>
        private const byte MaximumSlaveAddress = 64;

        /// <summary>
        /// 0x03 单次请求允许读取的最大寄存器数量。
        /// </summary>
        private const ushort MaximumReadQuantity = 125;

        /// <summary>
        /// 0x10 单次请求允许写入的最大寄存器数量。
        /// </summary>
        private const int MaximumWriteQuantity = 123;

        /// <summary>
        /// 固件私有未知地址查询使用的固定请求地址。
        /// </summary>
        private const byte UnknownAddressQuerySlaveAddress = 0xFE;

        /// <summary>
        /// 创建读取连续保持寄存器的 0x03 Modbus RTU 请求。
        /// </summary>
        /// <param name="slaveAddress">普通从站地址，允许范围为 1 至 64。</param>
        /// <param name="startAddress">首个待读取协议寄存器的零基地址。</param>
        /// <param name="quantity">待读取寄存器数量，允许范围为 1 至 125。</param>
        /// <returns>包含完整线路帧和读响应长度签名的不可变请求。</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="slaveAddress"/> 或 <paramref name="quantity"/> 超出协议范围时抛出。
        /// </exception>
        public static ModbusRequest CreateReadHoldingRegisters(
            byte slaveAddress,
            ushort startAddress,
            ushort quantity)
        {
            ValidateOrdinarySlaveAddress(slaveAddress);
            ValidateReadQuantity(quantity);

            Span<byte> body = stackalloc byte[6];
            body[0] = slaveAddress;
            body[1] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
            WriteBigEndianUInt16(body, 2, startAddress);
            WriteBigEndianUInt16(body, 4, quantity);

            byte[] rawFrame = ModbusCrc16.Append(body);
            int expectedResponseLength = 5 + (quantity * 2);

            return new ModbusRequest(
                slaveAddress,
                ModbusFunctionCode.ReadHoldingRegisters,
                startAddress,
                quantity,
                ReadOnlySpan<ushort>.Empty,
                rawFrame,
                expectedResponseLength,
                false);
        }

        /// <summary>
        /// 创建写入单个保持寄存器的 0x06 Modbus RTU 请求。
        /// </summary>
        /// <param name="slaveAddress">普通从站地址，允许范围为 1 至 64。</param>
        /// <param name="registerAddress">待写入协议寄存器的零基地址。</param>
        /// <param name="value">待写入的原始 16 位寄存器字。</param>
        /// <returns>包含完整线路帧和精确回显签名的不可变请求。</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="slaveAddress"/> 超出协议范围时抛出。
        /// </exception>
        public static ModbusRequest CreateWriteSingleRegister(
            byte slaveAddress,
            ushort registerAddress,
            ushort value)
        {
            ValidateOrdinarySlaveAddress(slaveAddress);

            Span<byte> body = stackalloc byte[6];
            body[0] = slaveAddress;
            body[1] = (byte)ModbusFunctionCode.WriteSingleRegister;
            WriteBigEndianUInt16(body, 2, registerAddress);
            WriteBigEndianUInt16(body, 4, value);

            byte[] rawFrame = ModbusCrc16.Append(body);
            Span<ushort> values = stackalloc ushort[1];
            values[0] = value;

            return new ModbusRequest(
                slaveAddress,
                ModbusFunctionCode.WriteSingleRegister,
                registerAddress,
                1,
                values,
                rawFrame,
                8,
                false);
        }

        /// <summary>
        /// 创建写入多个连续保持寄存器的 0x10 Modbus RTU 请求。
        /// </summary>
        /// <param name="slaveAddress">普通从站地址，允许范围为 1 至 64。</param>
        /// <param name="startAddress">首个待写入协议寄存器的零基地址。</param>
        /// <param name="values">按寄存器地址递增顺序排列的原始 16 位字，数量必须为 1 至 123。</param>
        /// <returns>包含大端寄存器数据、精确字节数和回显签名的不可变请求。</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="values"/> 为空时抛出。
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="slaveAddress"/> 超出协议范围，或 <paramref name="values"/> 多于 123 项时抛出。
        /// </exception>
        public static ModbusRequest CreateWriteMultipleRegisters(
            byte slaveAddress,
            ushort startAddress,
            ReadOnlySpan<ushort> values)
        {
            ValidateOrdinarySlaveAddress(slaveAddress);
            ValidateWriteValues(values);

            int byteCount = values.Length * 2;
            byte[] body = new byte[7 + byteCount];
            body[0] = slaveAddress;
            body[1] = (byte)ModbusFunctionCode.WriteMultipleRegisters;
            WriteBigEndianUInt16(body, 2, startAddress);
            WriteBigEndianUInt16(body, 4, checked((ushort)values.Length));
            body[6] = checked((byte)byteCount);

            for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                WriteBigEndianUInt16(body, 7 + (valueIndex * 2), values[valueIndex]);
            }

            byte[] rawFrame = ModbusCrc16.Append(body);

            return new ModbusRequest(
                slaveAddress,
                ModbusFunctionCode.WriteMultipleRegisters,
                startAddress,
                checked((ushort)values.Length),
                values,
                rawFrame,
                8,
                false);
        }

        /// <summary>
        /// 创建固件唯一支持的固定未知地址查询帧 <c>FE 03 00 00 00 01 + CRC</c>。
        /// </summary>
        /// <returns>请求地址为 0xFE、只读取 0x0000 一个寄存器的不可变特殊请求。</returns>
        public static ModbusRequest CreateUnknownAddressQuery()
        {
            Span<byte> body = stackalloc byte[6];
            body[0] = UnknownAddressQuerySlaveAddress;
            body[1] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
            WriteBigEndianUInt16(body, 2, 0);
            WriteBigEndianUInt16(body, 4, 1);

            byte[] rawFrame = ModbusCrc16.Append(body);

            return new ModbusRequest(
                UnknownAddressQuerySlaveAddress,
                ModbusFunctionCode.ReadHoldingRegisters,
                0,
                1,
                ReadOnlySpan<ushort>.Empty,
                rawFrame,
                7,
                true);
        }

        /// <summary>
        /// 验证地址是否处于当前设备协议定义的普通从站范围内。
        /// </summary>
        /// <param name="slaveAddress">待验证的普通从站地址。</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="slaveAddress"/> 不在 1 至 64 范围内时抛出。
        /// </exception>
        private static void ValidateOrdinarySlaveAddress(byte slaveAddress)
        {
            if (slaveAddress is < MinimumSlaveAddress or > MaximumSlaveAddress)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slaveAddress),
                    slaveAddress,
                    "普通 Modbus 从站地址必须位于 1 至 64。");
            }
        }

        /// <summary>
        /// 验证 0x03 读取数量是否处于 Modbus 协议允许范围内。
        /// </summary>
        /// <param name="quantity">待验证的寄存器数量。</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="quantity"/> 不在 1 至 125 范围内时抛出。
        /// </exception>
        private static void ValidateReadQuantity(ushort quantity)
        {
            if (quantity is < 1 or > MaximumReadQuantity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(quantity),
                    quantity,
                    "0x03 读取寄存器数量必须位于 1 至 125。");
            }
        }

        /// <summary>
        /// 验证 0x10 写入值数量是否非空且不超过协议上限。
        /// </summary>
        /// <param name="values">待验证的连续寄存器字。</param>
        /// <exception cref="ArgumentException"><paramref name="values"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="values"/> 多于 123 项时抛出。</exception>
        private static void ValidateWriteValues(ReadOnlySpan<ushort> values)
        {
            if (values.IsEmpty)
            {
                throw new ArgumentException(
                    "0x10 至少需要写入一个寄存器值。",
                    nameof(values));
            }

            if (values.Length > MaximumWriteQuantity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    values.Length,
                    "0x10 单次最多写入 123 个寄存器值。");
            }
        }

        /// <summary>
        /// 按 Modbus 数据域的大端顺序写入一个 16 位无符号值。
        /// </summary>
        /// <param name="destination">接收大端字节的目标缓冲区。</param>
        /// <param name="offset">高字节在目标缓冲区中的零基位置。</param>
        /// <param name="value">待编码的 16 位无符号值。</param>
        private static void WriteBigEndianUInt16(
            Span<byte> destination,
            int offset,
            ushort value)
        {
            destination[offset] = (byte)(value >> 8);
            destination[offset + 1] = (byte)(value & 0x00FF);
        }
    }
}
