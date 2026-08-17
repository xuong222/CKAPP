using System.IO.Ports;
using System.Text.Json.Serialization;
using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Settings
{
    /// <summary>
    /// 指定普通串口助手输入或显示数据的编码模式。
    /// </summary>
    public enum SerialAssistantDataMode
    {
        /// <summary>
        /// 使用 UTF-8 文本编码或解码。
        /// </summary>
        Utf8 = 0,

        /// <summary>
        /// 使用空格分隔的大写十六进制字节表示。
        /// </summary>
        Hex = 1,
    }

    /// <summary>
    /// 保存串口助手可安全持久化的线路、显示和发送偏好，不包含任何运行状态。
    /// </summary>
    public sealed class SerialAssistantPreferences
    {
        /// <summary>
        /// 获取或设置最近选择的助手串口名称。
        /// </summary>
        [JsonPropertyName("port_name")]
        public string PortName { get; set; } = "COM1";

        /// <summary>
        /// 获取或设置最近选择的助手线路波特率。
        /// </summary>
        [JsonPropertyName("baud_rate")]
        public int BaudRate { get; set; } = 115200;

        /// <summary>
        /// 获取或设置助手线路数据位数。
        /// </summary>
        [JsonPropertyName("data_bits")]
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// 获取或设置助手线路奇偶校验方式。
        /// </summary>
        [JsonPropertyName("parity")]
        public Parity Parity { get; set; } = Parity.None;

        /// <summary>
        /// 获取或设置助手线路停止位方式。
        /// </summary>
        [JsonPropertyName("stop_bits")]
        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>
        /// 获取或设置发送输入的 UTF-8 或 HEX 模式。
        /// </summary>
        [JsonPropertyName("send_mode")]
        public SerialAssistantDataMode SendMode { get; set; } = SerialAssistantDataMode.Utf8;

        /// <summary>
        /// 获取或设置接收视图的 UTF-8 或 HEX 模式。
        /// </summary>
        [JsonPropertyName("receive_mode")]
        public SerialAssistantDataMode ReceiveMode { get; set; } = SerialAssistantDataMode.Utf8;

        /// <summary>
        /// 获取或设置发送时是否在负载末尾追加 CRLF 字节 <c>0D 0A</c>。
        /// </summary>
        [JsonPropertyName("append_new_line")]
        public bool AppendNewLine { get; set; }

        /// <summary>
        /// 获取或设置接收视图是否显示批次时间戳。
        /// </summary>
        [JsonPropertyName("show_timestamps")]
        public bool ShowTimestamps { get; set; }

        /// <summary>
        /// 获取或设置接收视图有新内容时是否自动滚动到底部。
        /// </summary>
        [JsonPropertyName("auto_scroll")]
        public bool AutoScroll { get; set; } = true;

        /// <summary>
        /// 获取或设置最近选择的定时发送间隔毫秒数。
        /// </summary>
        [JsonPropertyName("periodic_interval_ms")]
        public int PeriodicIntervalMilliseconds { get; set; } = 1000;

        /// <summary>
        /// 获取或设置最近保存接收视图所使用的目录。
        /// </summary>
        [JsonPropertyName("save_directory")]
        public string SaveDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 获取或设置最近一次发送区输入，不代表自动发送授权。
        /// </summary>
        [JsonPropertyName("last_input")]
        public string LastInput { get; set; } = string.Empty;

        /// <summary>
        /// 创建经过线路、枚举、间隔和文本长度校验的独立偏好副本。
        /// </summary>
        /// <returns>可以安全保存或交给界面的全新偏好对象。</returns>
        public SerialAssistantPreferences CreateValidatedCopy()
        {
            if (!Enum.IsDefined(SendMode))
            {
                throw new InvalidDataException("串口助手发送模式无效。");
            }

            if (!Enum.IsDefined(ReceiveMode))
            {
                throw new InvalidDataException("串口助手接收模式无效。");
            }

            if (PeriodicIntervalMilliseconds is < 100 or > 3_600_000)
            {
                throw new InvalidDataException("串口助手定时发送间隔必须位于 100 毫秒至 1 小时。");
            }

            string normalizedPortName = PortName?.Trim() ?? string.Empty;
            string normalizedSaveDirectory = SaveDirectory?.Trim() ?? string.Empty;
            string normalizedLastInput = LastInput ?? string.Empty;

            if (normalizedPortName.Length > 64)
            {
                throw new InvalidDataException("串口助手端口名称不能超过 64 个字符。");
            }

            if (normalizedSaveDirectory.Length > 1024)
            {
                throw new InvalidDataException("串口助手保存目录不能超过 1024 个字符。");
            }

            if (normalizedLastInput.Length > 262144)
            {
                throw new InvalidDataException("串口助手最近输入不能超过 262144 个字符。");
            }

            SerialLineSettings lineSettings = new(
                normalizedPortName,
                BaudRate,
                DataBits,
                Parity,
                StopBits);

            return new SerialAssistantPreferences
            {
                PortName = lineSettings.PortName,
                BaudRate = lineSettings.BaudRate,
                DataBits = lineSettings.DataBits,
                Parity = lineSettings.Parity,
                StopBits = lineSettings.StopBits,
                SendMode = SendMode,
                ReceiveMode = ReceiveMode,
                AppendNewLine = AppendNewLine,
                ShowTimestamps = ShowTimestamps,
                AutoScroll = AutoScroll,
                PeriodicIntervalMilliseconds = PeriodicIntervalMilliseconds,
                SaveDirectory = normalizedSaveDirectory,
                LastInput = normalizedLastInput,
            };
        }
    }
}
