using System.IO.Ports;

namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 表示一次串口会话使用的不可变通信参数，并在进入传输层前统一约束可接受范围。
    /// </summary>
    public sealed class SerialSettings
    {
        /// <summary>
        /// 当前设备固件和界面共同支持的波特率集合。
        /// </summary>
        private static readonly HashSet<int> SupportedBaudRates =
        [
            2400,
            4800,
            9600,
            19200,
            38400,
            57600,
        ];

        /// <summary>
        /// 初始化一组经过完整校验的串口通信参数。
        /// </summary>
        /// <param name="portName">操作系统串口名称，例如 <c>COM3</c>。</param>
        /// <param name="baudRate">线路波特率，必须属于设备支持的固定集合。</param>
        /// <param name="dataBits">每个字符的数据位数，允许 5 至 8。</param>
        /// <param name="parity">串口奇偶校验方式。</param>
        /// <param name="stopBits">串口停止位设置。</param>
        /// <param name="responseTimeout">标准事务等待完整响应的总超时。</param>
        /// <param name="rawInterByteTimeout">原始调试接收中用于判定帧结束的字节间静默超时。</param>
        /// <exception cref="ArgumentException"><paramref name="portName"/> 为空或仅包含空白字符时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">任一数值或枚举参数超出允许范围时抛出。</exception>
        public SerialSettings(
            string portName,
            int baudRate,
            int dataBits,
            Parity parity,
            StopBits stopBits,
            TimeSpan responseTimeout,
            TimeSpan rawInterByteTimeout)
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
                    "波特率必须为 2400、4800、9600、19200、38400 或 57600。");
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

            ValidateTimeout(
                responseTimeout,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromSeconds(5),
                nameof(responseTimeout),
                "响应总超时");
            ValidateTimeout(
                rawInterByteTimeout,
                TimeSpan.FromMilliseconds(2),
                TimeSpan.FromSeconds(1),
                nameof(rawInterByteTimeout),
                "原始模式字节间超时");

            PortName = portName.Trim();
            BaudRate = baudRate;
            DataBits = dataBits;
            Parity = parity;
            StopBits = stopBits;
            ResponseTimeout = responseTimeout;
            RawInterByteTimeout = rawInterByteTimeout;
        }

        /// <summary>
        /// 获取应用推荐的 9600-8-N-1 参数；调用方只需提供目标串口名称。
        /// </summary>
        /// <param name="portName">操作系统串口名称，例如 <c>COM3</c>。</param>
        /// <returns>响应总超时 1000 毫秒、原始字节间超时 20 毫秒的默认设置。</returns>
        public static SerialSettings CreateDefault(string portName)
        {
            return new SerialSettings(
                portName,
                9600,
                8,
                System.IO.Ports.Parity.None,
                System.IO.Ports.StopBits.One,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(20));
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

        /// <summary>
        /// 获取标准事务的响应总超时。
        /// </summary>
        public TimeSpan ResponseTimeout { get; }

        /// <summary>
        /// 获取原始调试接收的字节间静默超时。
        /// </summary>
        public TimeSpan RawInterByteTimeout { get; }

        /// <summary>
        /// 校验一个有限正超时是否位于闭区间内。
        /// </summary>
        /// <param name="value">待校验的超时值。</param>
        /// <param name="minimum">允许的最小超时。</param>
        /// <param name="maximum">允许的最大超时。</param>
        /// <param name="parameterName">用于异常定位的构造函数参数名。</param>
        /// <param name="displayName">用于异常消息的中文字段名称。</param>
        private static void ValidateTimeout(
            TimeSpan value,
            TimeSpan minimum,
            TimeSpan maximum,
            string parameterName,
            string displayName)
        {
            if (value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    $"{displayName}必须位于 {minimum.TotalMilliseconds:0} 至 {maximum.TotalMilliseconds:0} 毫秒。");
            }
        }
    }
}
