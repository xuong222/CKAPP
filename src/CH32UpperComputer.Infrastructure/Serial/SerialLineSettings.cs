using System.IO.Ports;

namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 表示与上层协议无关的一组不可变串口线路参数。
    /// </summary>
    public class SerialLineSettings
    {
        /// <summary>
        /// 普通串口线路统一支持的固定波特率集合。
        /// </summary>
        private static readonly HashSet<int> SupportedBaudRates =
        [
            2400,
            4800,
            9600,
            19200,
            38400,
            57600,
            115200,
        ];

        /// <summary>
        /// 初始化一组经过完整校验的串口线路参数。
        /// </summary>
        /// <param name="portName">操作系统串口名称，例如 <c>COM3</c>。</param>
        /// <param name="baudRate">线路波特率，必须属于应用支持的固定集合。</param>
        /// <param name="dataBits">每个字符的数据位数，允许 5 至 8。</param>
        /// <param name="parity">串口奇偶校验方式。</param>
        /// <param name="stopBits">串口停止位设置。</param>
        /// <exception cref="ArgumentException"><paramref name="portName"/> 为空或仅包含空白字符时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">任一数值或枚举参数超出允许范围时抛出。</exception>
        public SerialLineSettings(
            string portName,
            int baudRate,
            int dataBits,
            Parity parity,
            StopBits stopBits)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                throw new ArgumentException("串口名称不能为空。", nameof(portName));
            }

            if (!SupportedBaudRates.Contains(baudRate))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baudRate),
                    baudRate,
                    "波特率必须为 2400、4800、9600、19200、38400、57600 或 115200。");
            }

            if (dataBits is < 5 or > 8)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dataBits),
                    dataBits,
                    "数据位数必须位于 5 至 8。");
            }

            if (!Enum.IsDefined(parity))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(parity),
                    parity,
                    "奇偶校验方式必须是已定义的枚举值。");
            }

            if (!Enum.IsDefined(stopBits) || stopBits == System.IO.Ports.StopBits.None)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopBits),
                    stopBits,
                    "停止位必须是 SerialPort 支持的 One、Two 或 OnePointFive。");
            }

            PortName = portName.Trim();
            BaudRate = baudRate;
            DataBits = dataBits;
            Parity = parity;
            StopBits = stopBits;
        }

        /// <summary>
        /// 获取串口助手默认使用的 115200-8-N-1 线路参数。
        /// </summary>
        /// <param name="portName">操作系统串口名称，例如 <c>COM1</c>。</param>
        /// <returns>使用指定端口名称的默认线路参数。</returns>
        public static SerialLineSettings CreateAssistantDefault(string portName)
        {
            return new SerialLineSettings(
                portName,
                115200,
                8,
                System.IO.Ports.Parity.None,
                System.IO.Ports.StopBits.One);
        }

        /// <summary>
        /// 获取操作系统串口名称。
        /// </summary>
        public string PortName { get; }

        /// <summary>
        /// 获取线路波特率。
        /// </summary>
        public int BaudRate { get; }

        /// <summary>
        /// 获取每个字符的数据位数。
        /// </summary>
        public int DataBits { get; }

        /// <summary>
        /// 获取奇偶校验方式。
        /// </summary>
        public Parity Parity { get; }

        /// <summary>
        /// 获取停止位设置。
        /// </summary>
        public StopBits StopBits { get; }
    }
}
