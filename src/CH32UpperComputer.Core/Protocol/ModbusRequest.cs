namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 表示一项结构已经验证且可用于匹配响应的不可变 Modbus RTU 请求。
    /// </summary>
    public sealed class ModbusRequest
    {
        /// <summary>
        /// 请求对象独占持有的完整线路帧，包含低字节在前的尾随 CRC16。
        /// </summary>
        private readonly byte[] rawFrame;

        /// <summary>
        /// 请求对象独占持有的写入寄存器字；读请求不包含写入值。
        /// </summary>
        private readonly ushort[] values;

        /// <summary>
        /// 初始化一项已经由请求工厂验证并编码的 Modbus 请求。
        /// </summary>
        /// <param name="slaveAddress">线路请求中的从站地址。</param>
        /// <param name="functionCode">线路请求中的受支持功能码。</param>
        /// <param name="startAddress">请求访问的首个协议寄存器地址。</param>
        /// <param name="quantity">请求读取或写入的寄存器数量。</param>
        /// <param name="values">写请求携带的寄存器字；读请求传入空序列。</param>
        /// <param name="rawFrame">已经追加有效 CRC16 的完整线路帧。</param>
        /// <param name="expectedResponseLength">正常响应的精确预期字节数。</param>
        /// <param name="isUnknownAddressQuery">指示请求是否为固件规定的固定 0xFE 未知地址查询。</param>
        internal ModbusRequest(
            byte slaveAddress,
            ModbusFunctionCode functionCode,
            ushort startAddress,
            ushort quantity,
            ReadOnlySpan<ushort> values,
            ReadOnlySpan<byte> rawFrame,
            int expectedResponseLength,
            bool isUnknownAddressQuery)
        {
            SlaveAddress = slaveAddress;
            FunctionCode = functionCode;
            StartAddress = startAddress;
            Quantity = quantity;
            this.values = values.ToArray();
            this.rawFrame = rawFrame.ToArray();
            ExpectedResponseLength = expectedResponseLength;
            IsUnknownAddressQuery = isUnknownAddressQuery;
        }

        /// <summary>
        /// 获取线路请求中的从站地址；固定未知地址查询为 0xFE。
        /// </summary>
        public byte SlaveAddress { get; }

        /// <summary>
        /// 获取请求使用的受支持 Modbus 功能码。
        /// </summary>
        public ModbusFunctionCode FunctionCode { get; }

        /// <summary>
        /// 获取请求访问的首个协议寄存器地址。
        /// </summary>
        public ushort StartAddress { get; }

        /// <summary>
        /// 获取请求读取或写入的寄存器数量。
        /// </summary>
        public ushort Quantity { get; }

        /// <summary>
        /// 获取写请求寄存器字的防御性副本；读请求返回空序列。
        /// 每次访问均返回独立数组，调用方无法修改请求内部的响应回显签名。
        /// </summary>
        public ReadOnlyMemory<ushort> Values => (ushort[])values.Clone();

        /// <summary>
        /// 获取包含尾随 CRC16 的完整请求帧防御性副本。
        /// 每次访问均返回独立数组，调用方无法修改请求内部帧。
        /// </summary>
        public ReadOnlyMemory<byte> RawFrame => (byte[])rawFrame.Clone();

        /// <summary>
        /// 获取正常响应的精确预期字节数；异常响应仍固定为五字节。
        /// </summary>
        public int ExpectedResponseLength { get; }

        /// <summary>
        /// 获取指示请求是否为固定 0xFE 未知地址查询的值。
        /// </summary>
        public bool IsUnknownAddressQuery { get; }
    }
}
