namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 定义当前上位机能够作为标准事务识别和验证的 Modbus 功能码。
    /// </summary>
    public enum ModbusFunctionCode : byte
    {
        /// <summary>
        /// 读取一个或多个连续保持寄存器。
        /// </summary>
        ReadHoldingRegisters = 0x03,

        /// <summary>
        /// 写入单个保持寄存器，并要求响应完整回显请求地址和值。
        /// </summary>
        WriteSingleRegister = 0x06,

        /// <summary>
        /// 写入多个连续保持寄存器，并要求响应回显起始地址和寄存器数量。
        /// </summary>
        WriteMultipleRegisters = 0x10,
    }
}
