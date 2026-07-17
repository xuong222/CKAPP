namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示专家寄存器工具中一个原始 16 位字的三种固定视图和可选语义解释。
    /// </summary>
    public sealed class RegisterResultRowViewModel
    {
        /// <summary>
        /// 初始化一行不会用全局 int16 规则覆盖原始事实的寄存器结果。
        /// </summary>
        /// <param name="documentAddress">面向用户的四万区地址。</param>
        /// <param name="protocolAddress">Modbus PDU 使用的零基地址。</param>
        /// <param name="rawWord">响应中的原始 16 位字。</param>
        /// <param name="semanticValue">由该地址独立寄存器定义生成的可选语义显示。</param>
        public RegisterResultRowViewModel(
            int documentAddress,
            ushort protocolAddress,
            ushort rawWord,
            string semanticValue)
        {
            DocumentAddress = documentAddress;
            ProtocolAddress = protocolAddress;
            SignedValue = unchecked((short)rawWord).ToString(System.Globalization.CultureInfo.InvariantCulture);
            UnsignedValue = rawWord.ToString(System.Globalization.CultureInfo.InvariantCulture);
            HexValue = $"0x{rawWord:X4}";
            SemanticValue = semanticValue ?? string.Empty;
        }

        /// <summary>
        /// 获取四万区文档地址。
        /// </summary>
        public int DocumentAddress { get; }

        /// <summary>
        /// 获取 Modbus PDU 零基地址。
        /// </summary>
        public ushort ProtocolAddress { get; }

        /// <summary>
        /// 获取同一原始字按二进制补码显示的有符号十进制文本。
        /// </summary>
        public string SignedValue { get; }

        /// <summary>
        /// 获取同一原始字按无符号十进制显示的文本。
        /// </summary>
        public string UnsignedValue { get; }

        /// <summary>
        /// 获取同一原始字的四位大写十六进制文本。
        /// </summary>
        public string HexValue { get; }

        /// <summary>
        /// 获取由当前寄存器独立定义生成的语义显示文本。
        /// </summary>
        public string SemanticValue { get; }
    }
}
