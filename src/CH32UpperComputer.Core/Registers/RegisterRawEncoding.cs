namespace CH32UpperComputer.Core.Registers
{
    /// <summary>
    /// 定义单个设备寄存器对 16 位线路字的原始解释方式与业务语义。
    /// 每个寄存器必须显式选择一项，禁止依据地址范围推断有符号性。
    /// </summary>
    public enum RegisterRawEncoding
    {
        /// <summary>
        /// 无符号 16 位设备地址。
        /// </summary>
        UInt16Address,

        /// <summary>
        /// 无符号 16 位枚举代码。
        /// </summary>
        UInt16Enumeration,

        /// <summary>
        /// 有符号 16 位测量值，负值使用二进制补码。
        /// </summary>
        Int16Measurement,

        /// <summary>
        /// 无符号 16 位布尔状态，当前设备仅定义零和一。
        /// </summary>
        UInt16Boolean,

        /// <summary>
        /// 无符号 16 位位掩码。
        /// </summary>
        UInt16BitMask,

        /// <summary>
        /// 有符号 16 位报警阈值。
        /// </summary>
        Int16Threshold,

        /// <summary>
        /// 有符号 16 位报警回差。
        /// </summary>
        Int16Hysteresis,

        /// <summary>
        /// 有符号 16 位补偿值。
        /// </summary>
        Int16Compensation,

        /// <summary>
        /// 无符号 16 位瞬时命令字。
        /// </summary>
        UInt16Command,
    }
}
