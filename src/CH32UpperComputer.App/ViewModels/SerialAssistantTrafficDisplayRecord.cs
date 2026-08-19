using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 描述串口画布上一项可分色的 TX 或 RX 消息，以及保存文本所需的等价格式。
    /// </summary>
    public sealed class SerialAssistantTrafficDisplayRecord
    {
        /// <summary>
        /// 初始化一项方向明确的双行串口消息。
        /// </summary>
        /// <param name="direction">消息来自成功发送还是线路接收。</param>
        /// <param name="headerText">包含可选秒级时间戳和 TX/RX 标签的标题行。</param>
        /// <param name="payloadText">已经按 UTF-8 或 HEX 模式转换并规范化换行的正文。</param>
        public SerialAssistantTrafficDisplayRecord(
            SerialAssistantTrafficDirection direction,
            string headerText,
            string payloadText)
        {
            ArgumentNullException.ThrowIfNull(headerText);
            ArgumentNullException.ThrowIfNull(payloadText);
            Direction = direction;
            HeaderText = headerText;
            PayloadText = payloadText;
            PlainText = CreatePlainText(headerText, payloadText);
        }

        /// <summary>
        /// 获取消息的发送或接收方向。
        /// </summary>
        public SerialAssistantTrafficDirection Direction { get; }

        /// <summary>
        /// 获取标题行；时间戳启用时格式为“[HH:mm:ss]  TX”。
        /// </summary>
        public string HeaderText { get; }

        /// <summary>
        /// 获取不含额外尾部换行的显示正文。
        /// </summary>
        public string PayloadText { get; }

        /// <summary>
        /// 获取是否为发送消息，供 XAML 数据触发器选择 TX 暖色。
        /// </summary>
        public bool IsTransmit => Direction == SerialAssistantTrafficDirection.Transmit;

        /// <summary>
        /// 获取与画布双行排版等价、可直接保存到文本文件的消息块。
        /// </summary>
        public string PlainText { get; }

        /// <summary>
        /// 创建带五空格正文缩进和消息间空行的纯文本块。
        /// </summary>
        /// <param name="headerText">已经构造完成的标题行。</param>
        /// <param name="payloadText">已经规范化为 CRLF 的正文。</param>
        /// <returns>可直接追加到保存视图的双行消息文本。</returns>
        private static string CreatePlainText(
            string headerText,
            string payloadText)
        {
            string indentedPayload = payloadText.Replace(
                "\r\n",
                "\r\n     ",
                StringComparison.Ordinal);
            return headerText + "\r\n     " + indentedPayload + "\r\n\r\n";
        }
    }
}
