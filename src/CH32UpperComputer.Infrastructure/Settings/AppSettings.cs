using System.IO.Ports;
using System.Text.Json.Serialization;
using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Settings
{
    /// <summary>
    /// 保存上位机允许持久化的非敏感用户偏好，并集中约束串口、地址、超时和定时间隔范围。
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// 当前设置文件结构版本。
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// 获取或设置设置文件结构版本。
        /// </summary>
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// 获取或设置最近选择的操作系统串口名称。
        /// </summary>
        [JsonPropertyName("port_name")]
        public string PortName { get; set; } = "COM1";

        /// <summary>
        /// 获取或设置最近选择的串口波特率。
        /// </summary>
        [JsonPropertyName("baud_rate")]
        public int BaudRate { get; set; } = 9600;

        /// <summary>
        /// 获取或设置串口数据位数。
        /// </summary>
        [JsonPropertyName("data_bits")]
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// 获取或设置串口奇偶校验方式。
        /// </summary>
        [JsonPropertyName("parity")]
        public Parity Parity { get; set; } = Parity.None;

        /// <summary>
        /// 获取或设置串口停止位方式。
        /// </summary>
        [JsonPropertyName("stop_bits")]
        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>
        /// 获取或设置普通 Modbus 从站地址，允许范围为 1 至 64。
        /// </summary>
        [JsonPropertyName("slave_address")]
        public int SlaveAddress { get; set; } = 1;

        /// <summary>
        /// 获取或设置标准事务响应总超时的毫秒数。
        /// </summary>
        [JsonPropertyName("response_timeout_ms")]
        public int ResponseTimeoutMilliseconds { get; set; } = 1000;

        /// <summary>
        /// 获取或设置原始调试模式字节间静默超时的毫秒数。
        /// </summary>
        [JsonPropertyName("raw_inter_byte_timeout_ms")]
        public int RawInterByteTimeoutMilliseconds { get; set; } = 20;

        /// <summary>
        /// 获取或设置最近选择的定时发送间隔；是否启用不会持久化为真。
        /// </summary>
        [JsonPropertyName("periodic_interval_ms")]
        public int PeriodicIntervalMilliseconds { get; set; } = 1000;

        /// <summary>
        /// 获取或设置手动十六进制输入是否默认自动补充 CRC。
        /// </summary>
        [JsonPropertyName("auto_append_crc")]
        public bool AutoAppendCrc { get; set; } = true;

        /// <summary>
        /// 获取或设置最近一次手动输入的指令文本。
        /// </summary>
        [JsonPropertyName("last_command")]
        public string LastCommand { get; set; } = string.Empty;

        /// <summary>
        /// 获取或设置最近选择的日志导出目录。
        /// </summary>
        [JsonPropertyName("log_export_directory")]
        public string LogExportDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 获取或设置连接后自动发送标记；保存和加载时必须强制归零，连接始终等待手动指令。
        /// </summary>
        [JsonPropertyName("automatic_send_on_connect")]
        public bool AutomaticSendOnConnect { get; set; }

        /// <summary>
        /// 获取或设置定时发送启用标记；保存和加载时必须强制归零，只能由用户本次会话手动开启。
        /// </summary>
        [JsonPropertyName("periodic_send_enabled")]
        public bool PeriodicSendEnabled { get; set; }

        /// <summary>
        /// 创建采用 9600-8-N-1、地址一和默认超时的全新设置。
        /// </summary>
        /// <returns>自动发送与定时发送均关闭的默认设置。</returns>
        public static AppSettings CreateDefault()
        {
            return new AppSettings();
        }

        /// <summary>
        /// 创建经过完整范围校验和安全标志归零的独立持久化副本。
        /// </summary>
        /// <returns>可以安全写入设置文件或交给界面的新设置对象。</returns>
        /// <exception cref="InvalidDataException">设置版本、地址、文本长度或定时间隔不合法时抛出。</exception>
        /// <exception cref="ArgumentException">串口名称或串口枚举设置不合法时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">串口数值或超时不合法时抛出。</exception>
        public AppSettings CreateValidatedCopy()
        {
            if (SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"不支持设置文件结构版本 {SchemaVersion}，当前仅支持 {CurrentSchemaVersion}。");
            }

            if (SlaveAddress is < 1 or > 64)
            {
                throw new InvalidDataException("普通 Modbus 从站地址必须位于 1 至 64。");
            }

            if (PeriodicIntervalMilliseconds is < 100 or > 3_600_000)
            {
                throw new InvalidDataException("定时发送间隔必须位于 100 毫秒至 1 小时。");
            }

            string normalizedLastCommand = LastCommand?.Trim() ?? string.Empty;
            string normalizedExportDirectory = LogExportDirectory?.Trim() ?? string.Empty;
            string normalizedPortName = PortName?.Trim() ?? string.Empty;

            if (normalizedPortName.Length > 64)
            {
                throw new InvalidDataException("串口名称不能超过 64 个字符。");
            }

            if (normalizedLastCommand.Length > 4096)
            {
                throw new InvalidDataException("最近指令文本不能超过 4096 个字符。");
            }

            if (normalizedExportDirectory.Length > 1024)
            {
                throw new InvalidDataException("日志导出目录不能超过 1024 个字符。");
            }

            SerialSettings serialSettings = new(
                normalizedPortName,
                BaudRate,
                DataBits,
                Parity,
                StopBits,
                TimeSpan.FromMilliseconds(ResponseTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(RawInterByteTimeoutMilliseconds));

            return new AppSettings
            {
                SchemaVersion = CurrentSchemaVersion,
                PortName = serialSettings.PortName,
                BaudRate = serialSettings.BaudRate,
                DataBits = serialSettings.DataBits,
                Parity = serialSettings.Parity,
                StopBits = serialSettings.StopBits,
                SlaveAddress = SlaveAddress,
                ResponseTimeoutMilliseconds = checked((int)serialSettings.ResponseTimeout.TotalMilliseconds),
                RawInterByteTimeoutMilliseconds = checked((int)serialSettings.RawInterByteTimeout.TotalMilliseconds),
                PeriodicIntervalMilliseconds = PeriodicIntervalMilliseconds,
                AutoAppendCrc = AutoAppendCrc,
                LastCommand = normalizedLastCommand,
                LogExportDirectory = normalizedExportDirectory,
                AutomaticSendOnConnect = false,
                PeriodicSendEnabled = false,
            };
        }
    }
}
