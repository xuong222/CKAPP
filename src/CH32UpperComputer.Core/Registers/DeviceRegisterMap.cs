using System.Collections.ObjectModel;

namespace CH32UpperComputer.Core.Registers
{
    /// <summary>
    /// 提供当前设备 40001 至 40036 的唯一、只读寄存器定义表。
    /// </summary>
    public static class DeviceRegisterMap
    {
        /// <summary>
        /// 按协议地址升序保存的只读寄存器定义。
        /// </summary>
        private static readonly ReadOnlyCollection<RegisterDefinition> Definitions =
            Array.AsReadOnly(CreateDefinitions());

        /// <summary>
        /// 按协议地址建立的内部唯一索引。
        /// </summary>
        private static readonly IReadOnlyDictionary<ushort, RegisterDefinition> ByProtocolAddress =
            new ReadOnlyDictionary<ushort, RegisterDefinition>(
                Definitions.ToDictionary(definition => definition.ProtocolAddress));

        /// <summary>
        /// 按文档地址建立的内部唯一索引。
        /// </summary>
        private static readonly IReadOnlyDictionary<int, RegisterDefinition> ByDocumentAddress =
            new ReadOnlyDictionary<int, RegisterDefinition>(
                Definitions.ToDictionary(definition => definition.DocumentAddress));

        /// <summary>
        /// 获取按协议地址升序排列的全部寄存器定义只读视图。
        /// </summary>
        public static IReadOnlyList<RegisterDefinition> All => Definitions;

        /// <summary>
        /// 根据零基协议地址取得寄存器定义。
        /// </summary>
        /// <param name="protocolAddress">Modbus PDU 中的零基寄存器地址。</param>
        /// <returns>与协议地址唯一对应的寄存器定义。</returns>
        /// <exception cref="KeyNotFoundException">协议地址不属于当前 36 项设备定义时抛出。</exception>
        public static RegisterDefinition GetByProtocolAddress(ushort protocolAddress)
        {
            return ByProtocolAddress.TryGetValue(protocolAddress, out RegisterDefinition? definition)
                ? definition
                : throw new KeyNotFoundException($"协议地址 0x{protocolAddress:X4} 不在设备寄存器表中。");
        }

        /// <summary>
        /// 根据四万区文档地址取得寄存器定义。
        /// </summary>
        /// <param name="documentAddress">面向用户的四万区文档地址。</param>
        /// <returns>与文档地址唯一对应的寄存器定义。</returns>
        /// <exception cref="KeyNotFoundException">文档地址不属于 40001 至 40036 时抛出。</exception>
        public static RegisterDefinition GetByDocumentAddress(int documentAddress)
        {
            return ByDocumentAddress.TryGetValue(documentAddress, out RegisterDefinition? definition)
                ? definition
                : throw new KeyNotFoundException($"文档地址 {documentAddress} 不在设备寄存器表中。");
        }

        /// <summary>
        /// 尝试根据零基协议地址取得寄存器定义。
        /// </summary>
        /// <param name="protocolAddress">Modbus PDU 中的零基寄存器地址。</param>
        /// <param name="definition">成功时接收唯一寄存器定义，失败时为 <see langword="null"/>。</param>
        /// <returns>找到协议地址时为 <see langword="true"/>，否则为 <see langword="false"/>。</returns>
        public static bool TryGetByProtocolAddress(
            ushort protocolAddress,
            out RegisterDefinition? definition)
        {
            return ByProtocolAddress.TryGetValue(protocolAddress, out definition);
        }

        /// <summary>
        /// 构造当前固件协议明确规定的 36 项寄存器定义。
        /// </summary>
        /// <returns>按协议地址连续升序排列的新定义数组。</returns>
        private static RegisterDefinition[] CreateDefinitions()
        {
            return
            [
                Define(40001, 0x0000, "从站地址", RegisterRawEncoding.UInt16Address, 1, 64, 1, 64, 1m, 0, "地址", "闭区间 1 至 64"),
                Define(40002, 0x0001, "波特率代码", RegisterRawEncoding.UInt16Enumeration, 0, 5, 0, 5, 1m, 0, "代码", "0=2400, 1=4800, 2=9600, 3=19200, 4=38400, 5=57600"),
                Define(40003, 0x0002, "温度显示值", RegisterRawEncoding.Int16Measurement, -5000, 11000, null, null, 0.01m, 2, "℃", "原始值除以 100"),
                Define(40004, 0x0003, "烟雾显示值", RegisterRawEncoding.Int16Measurement, -1000, 5095, null, null, 1m, 0, "MQ2 线性单位", "非标定 ppm"),
                Define(40005, 0x0004, "PM2.5 显示值", RegisterRawEncoding.Int16Measurement, -200, 1199, null, null, 1m, 0, "μg/m³", "整数显示"),
                Define(40006, 0x0005, "CO 显示值", RegisterRawEncoding.Int16Measurement, -200, 10200, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
                Define(40007, 0x0006, "CO2 显示值", RegisterRawEncoding.Int16Measurement, -600, 6000, null, null, 1m, 0, "ppm", "整数显示"),
                Define(40008, 0x0007, "SO2 显示值", RegisterRawEncoding.Int16Measurement, -20, 220, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
                Define(40009, 0x0008, "综合报警继电器", RegisterRawEncoding.UInt16Boolean, 0, 1, null, null, 1m, 0, "状态", "0=正常, 1=报警"),
                Define(40010, 0x0009, "报警位", RegisterRawEncoding.UInt16BitMask, 0, 0x003F, null, null, 1m, 0, "掩码", "bit0 温度, bit1 烟雾, bit2 PM2.5, bit3 CO, bit4 CO2, bit5 SO2"),
                Define(40011, 0x000A, "温度报警阈值", RegisterRawEncoding.Int16Threshold, -40, 100, -40, 100, 1m, 0, "℃", "整数阈值"),
                Define(40012, 0x000B, "温度报警回差", RegisterRawEncoding.Int16Hysteresis, short.MinValue, 0, short.MinValue, 0, 1m, 0, "℃", "必须小于或等于零"),
                Define(40013, 0x000C, "烟雾报警阈值", RegisterRawEncoding.Int16Threshold, 0, 5000, 0, 5000, 1m, 0, "MQ2 线性单位", "整数阈值"),
                Define(40014, 0x000D, "烟雾报警回差", RegisterRawEncoding.Int16Hysteresis, short.MinValue, 0, short.MinValue, 0, 1m, 0, "MQ2 线性单位", "必须小于或等于零"),
                Define(40015, 0x000E, "PM2.5 报警阈值", RegisterRawEncoding.Int16Threshold, 0, 999, 0, 999, 1m, 0, "μg/m³", "整数阈值"),
                Define(40016, 0x000F, "PM2.5 报警回差", RegisterRawEncoding.Int16Hysteresis, short.MinValue, 0, short.MinValue, 0, 1m, 0, "μg/m³", "必须小于或等于零"),
                Define(40017, 0x0010, "CO 报警阈值", RegisterRawEncoding.Int16Threshold, 0, 1000, 0, 1000, 1m, 0, "ppm", "整数阈值"),
                Define(40018, 0x0011, "CO 报警回差", RegisterRawEncoding.Int16Hysteresis, short.MinValue, 0, short.MinValue, 0, 1m, 0, "ppm", "必须小于或等于零"),
                Define(40019, 0x0012, "CO2 报警阈值", RegisterRawEncoding.Int16Threshold, 400, 5000, 400, 5000, 1m, 0, "ppm", "整数阈值"),
                Define(40020, 0x0013, "CO2 报警回差", RegisterRawEncoding.Int16Hysteresis, short.MinValue, 0, short.MinValue, 0, 1m, 0, "ppm", "必须小于或等于零"),
                Define(40021, 0x0014, "SO2 报警阈值", RegisterRawEncoding.Int16Threshold, 0, 20, 0, 20, 1m, 0, "ppm", "整数阈值"),
                Define(40022, 0x0015, "SO2 报警回差", RegisterRawEncoding.Int16Hysteresis, short.MinValue, 0, short.MinValue, 0, 1m, 0, "ppm", "必须小于或等于零"),
                Define(40023, 0x0016, "报警使能掩码", RegisterRawEncoding.UInt16BitMask, 0, 0x003F, 0, 0x003F, 1m, 0, "掩码", "仅低六位可写，可使用十六进制或逐项开关"),
                Define(40024, 0x0017, "实测温度", RegisterRawEncoding.Int16Measurement, -4000, 10000, null, null, 0.01m, 2, "℃", "原始值除以 100"),
                Define(40025, 0x0018, "实测烟雾", RegisterRawEncoding.Int16Measurement, 0, 4095, null, null, 1m, 0, "MQ2 线性单位", "非标定 ppm"),
                Define(40026, 0x0019, "实测 PM2.5", RegisterRawEncoding.Int16Measurement, 0, 999, null, null, 1m, 0, "μg/m³", "整数显示"),
                Define(40027, 0x001A, "实测 CO", RegisterRawEncoding.Int16Measurement, 0, 10000, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
                Define(40028, 0x001B, "实测 CO2", RegisterRawEncoding.Int16Measurement, 400, 5000, null, null, 1m, 0, "ppm", "整数显示"),
                Define(40029, 0x001C, "实测 SO2", RegisterRawEncoding.Int16Measurement, 0, 200, null, null, 0.1m, 1, "ppm", "原始值除以 10"),
                Define(40030, 0x001D, "温度补偿", RegisterRawEncoding.Int16Compensation, -10, 10, -10, 10, 1m, 0, "℃", "设备内部应用时乘以 100"),
                Define(40031, 0x001E, "烟雾补偿", RegisterRawEncoding.Int16Compensation, -1000, 1000, -1000, 1000, 1m, 0, "MQ2 线性单位", "整数补偿"),
                Define(40032, 0x001F, "PM2.5 补偿", RegisterRawEncoding.Int16Compensation, -200, 200, -200, 200, 1m, 0, "μg/m³", "整数补偿"),
                Define(40033, 0x0020, "CO 补偿", RegisterRawEncoding.Int16Compensation, -200, 200, -200, 200, 0.1m, 1, "ppm", "原始值除以 10"),
                Define(40034, 0x0021, "CO2 补偿", RegisterRawEncoding.Int16Compensation, -1000, 1000, -1000, 1000, 1m, 0, "ppm", "整数补偿"),
                Define(40035, 0x0022, "SO2 补偿", RegisterRawEncoding.Int16Compensation, -20, 20, -20, 20, 0.1m, 1, "ppm", "原始值除以 10"),
                Define(40036, 0x0023, "恢复出厂", RegisterRawEncoding.UInt16Command, 0, 0, 1, 1, 1m, 0, "命令", "读取恒为 0，写入 1 触发且不得保持"),
            ];
        }

        /// <summary>
        /// 以参数名明确的方式创建一个不可变寄存器定义。
        /// </summary>
        /// <param name="documentAddress">四万区文档地址。</param>
        /// <param name="protocolAddress">零基协议地址。</param>
        /// <param name="name">寄存器中文名称。</param>
        /// <param name="rawEncoding">线路原始字解释和业务语义。</param>
        /// <param name="expectedRawMinimum">读取预期原始下界。</param>
        /// <param name="expectedRawMaximum">读取预期原始上界。</param>
        /// <param name="writableRawMinimum">可写原始下界或空值。</param>
        /// <param name="writableRawMaximum">可写原始上界或空值。</param>
        /// <param name="displayScale">原始整数到显示数值的十进制乘数。</param>
        /// <param name="decimalPlaces">显示小数位数。</param>
        /// <param name="unit">显示单位或值域标签。</param>
        /// <param name="valueDescription">特殊值、位或设备侧处理说明。</param>
        /// <returns>完整不可变的寄存器定义。</returns>
        private static RegisterDefinition Define(
            int documentAddress,
            ushort protocolAddress,
            string name,
            RegisterRawEncoding rawEncoding,
            int expectedRawMinimum,
            int expectedRawMaximum,
            int? writableRawMinimum,
            int? writableRawMaximum,
            decimal displayScale,
            int decimalPlaces,
            string unit,
            string valueDescription)
        {
            return new RegisterDefinition(
                documentAddress,
                protocolAddress,
                name,
                rawEncoding,
                expectedRawMinimum,
                expectedRawMaximum,
                writableRawMinimum,
                writableRawMaximum,
                displayScale,
                decimalPlaces,
                unit,
                valueDescription);
        }
    }
}
