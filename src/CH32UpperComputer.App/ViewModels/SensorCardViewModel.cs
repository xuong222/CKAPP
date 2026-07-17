using CommunityToolkit.Mvvm.ComponentModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示工业仪表盘中一张传感器数据卡的当前显示状态。
    /// </summary>
    public sealed partial class SensorCardViewModel : ObservableObject
    {
        /// <summary>
        /// 当前格式化数值；尚未收到有效值时为双短横线。
        /// </summary>
        [ObservableProperty]
        private string valueText = "--";

        /// <summary>
        /// 当前卡片状态说明。
        /// </summary>
        [ObservableProperty]
        private string stateText = "等待数据";

        /// <summary>
        /// 当前状态色语义名称，供 XAML 触发器选择正常、警告或异常颜色。
        /// </summary>
        [ObservableProperty]
        private string tone = "Normal";

        /// <summary>
        /// 初始化一张固定寄存器映射的数据卡。
        /// </summary>
        /// <param name="title">卡片主标题，例如“温度”。</param>
        /// <param name="documentAddress">面向用户显示的四万区寄存器地址。</param>
        /// <param name="protocolAddress">Modbus PDU 使用的零基寄存器地址。</param>
        /// <param name="unit">显示数值使用的工程单位。</param>
        /// <param name="symbol">卡片左上角使用的简短工业标识。</param>
        public SensorCardViewModel(
            string title,
            int documentAddress,
            ushort protocolAddress,
            string unit,
            string symbol)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
            Title = title;
            DocumentAddress = documentAddress;
            ProtocolAddress = protocolAddress;
            Unit = unit ?? string.Empty;
            Symbol = symbol;
        }

        /// <summary>
        /// 获取卡片主标题。
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// 获取四万区文档寄存器地址。
        /// </summary>
        public int DocumentAddress { get; }

        /// <summary>
        /// 获取 Modbus PDU 使用的零基寄存器地址。
        /// </summary>
        public ushort ProtocolAddress { get; }

        /// <summary>
        /// 获取数值工程单位。
        /// </summary>
        public string Unit { get; }

        /// <summary>
        /// 获取卡片使用的简短工业标识。
        /// </summary>
        public string Symbol { get; }

        /// <summary>
        /// 使用一项已解释寄存器值更新卡片显示。
        /// </summary>
        /// <param name="formattedValue">由寄存器定义决定精度的格式化显示值。</param>
        /// <param name="isWithinExpectedRange">原始值是否处于该寄存器定义的预期范围。</param>
        /// <param name="diagnostic">越界时显示的可选诊断。</param>
        public void UpdateValue(
            string formattedValue,
            bool isWithinExpectedRange,
            string? diagnostic)
        {
            ValueText = string.IsNullOrWhiteSpace(formattedValue) ? "--" : formattedValue;
            StateText = isWithinExpectedRange
                ? "数据有效"
                : diagnostic ?? "原始值超出预期范围";
            Tone = isWithinExpectedRange ? "Normal" : "Warning";
        }
    }
}
