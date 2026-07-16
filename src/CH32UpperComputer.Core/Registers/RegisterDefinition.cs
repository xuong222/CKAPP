namespace CH32UpperComputer.Core.Registers
{
    /// <summary>
    /// 表示一个设备寄存器不可变且完整的线路解释、有效范围与显示定义。
    /// </summary>
    public sealed class RegisterDefinition
    {
        /// <summary>
        /// 初始化一个寄存器定义。所有范围均为缩放前的业务原始整数范围。
        /// </summary>
        /// <param name="documentAddress">面向用户的四万区文档地址。</param>
        /// <param name="protocolAddress">Modbus PDU 中使用的零基协议地址。</param>
        /// <param name="name">寄存器中文名称。</param>
        /// <param name="rawEncoding">该寄存器明确指定的有符号性和业务语义。</param>
        /// <param name="expectedRawMinimum">读取值的预期原始下界；越界值只诊断、不钳位。</param>
        /// <param name="expectedRawMaximum">读取值的预期原始上界；越界值只诊断、不钳位。</param>
        /// <param name="writableRawMinimum">允许写入的原始下界；只读寄存器为 <see langword="null"/>。</param>
        /// <param name="writableRawMaximum">允许写入的原始上界；只读寄存器为 <see langword="null"/>。</param>
        /// <param name="displayScale">原始整数转换为显示数值时使用的精确十进制乘数。</param>
        /// <param name="decimalPlaces">界面显示的小数位数。</param>
        /// <param name="unit">该寄存器明确指定的显示单位或值域标签。</param>
        /// <param name="valueDescription">枚举、位定义或设备侧特殊处理的补充说明。</param>
        internal RegisterDefinition(
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
            DocumentAddress = documentAddress;
            ProtocolAddress = protocolAddress;
            Name = name;
            RawEncoding = rawEncoding;
            ExpectedRawMinimum = expectedRawMinimum;
            ExpectedRawMaximum = expectedRawMaximum;
            WritableRawMinimum = writableRawMinimum;
            WritableRawMaximum = writableRawMaximum;
            DisplayScale = displayScale;
            DecimalPlaces = decimalPlaces;
            Unit = unit;
            ValueDescription = valueDescription;
        }

        /// <summary>
        /// 获取面向用户的四万区文档地址。
        /// </summary>
        public int DocumentAddress { get; }

        /// <summary>
        /// 获取 Modbus PDU 中使用的零基协议地址。
        /// </summary>
        public ushort ProtocolAddress { get; }

        /// <summary>
        /// 获取寄存器中文名称。
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// 获取该寄存器明确指定的线路字解释和业务语义。
        /// </summary>
        public RegisterRawEncoding RawEncoding { get; }

        /// <summary>
        /// 获取读取值的预期原始下界。
        /// </summary>
        public int ExpectedRawMinimum { get; }

        /// <summary>
        /// 获取读取值的预期原始上界。
        /// </summary>
        public int ExpectedRawMaximum { get; }

        /// <summary>
        /// 获取允许写入的原始下界；只读寄存器为 <see langword="null"/>。
        /// </summary>
        public int? WritableRawMinimum { get; }

        /// <summary>
        /// 获取允许写入的原始上界；只读寄存器为 <see langword="null"/>。
        /// </summary>
        public int? WritableRawMaximum { get; }

        /// <summary>
        /// 获取指示寄存器是否允许写入的值。
        /// </summary>
        public bool IsWritable => WritableRawMinimum.HasValue && WritableRawMaximum.HasValue;

        /// <summary>
        /// 获取原始整数转换为显示数值时使用的精确十进制乘数。
        /// </summary>
        public decimal DisplayScale { get; }

        /// <summary>
        /// 获取界面显示的小数位数。
        /// </summary>
        public int DecimalPlaces { get; }

        /// <summary>
        /// 获取显示单位或值域标签。
        /// </summary>
        public string Unit { get; }

        /// <summary>
        /// 获取枚举、位定义或设备侧特殊处理的补充说明。
        /// </summary>
        public string ValueDescription { get; }
    }
}
