using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Framing
{
    /// <summary>
    /// 保存一次 Modbus RTU 接收组帧器使用的不可变容量与超时参数。
    /// </summary>
    public sealed class RtuFramerOptions
    {
        /// <summary>
        /// Modbus RTU 标准帧允许的生产默认最大字节数。
        /// </summary>
        public const int DefaultMaximumFrameBytes = 256;

        /// <summary>
        /// 接收状态机允许占用的生产默认缓存字节数。
        /// </summary>
        public const int DefaultReceiveBufferBytes = 4096;

        /// <summary>
        /// 原始调试模式允许捕获的生产默认最大字节数。
        /// </summary>
        public const int DefaultRawCaptureMaxBytes = 4096;

        /// <summary>
        /// 初始化一组经过完整校验的组帧参数。
        /// </summary>
        /// <param name="maximumFrameBytes">单个结构化 RTU 帧允许的最大字节数，闭区间为 4 至 256。</param>
        /// <param name="receiveBufferBytes">内部有界接收缓存容量，闭区间为 4 至 4096。</param>
        /// <param name="rawCaptureMaxBytes">原始调试捕获上限，不得超过接收缓存容量。</param>
        /// <param name="rawInterByteTimeout">原始调试模式判定一段接收结束的字节间静默时间。</param>
        /// <param name="responseTimeout">一轮响应捕获允许持续的总时间。</param>
        /// <param name="timeProvider">同时提供单调时间戳与 UTC 日历时间的统一时间源。</param>
        /// <exception cref="ArgumentOutOfRangeException">容量或超时超出允许范围时抛出。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> 为空时抛出。</exception>
        public RtuFramerOptions(
            int maximumFrameBytes,
            int receiveBufferBytes,
            int rawCaptureMaxBytes,
            TimeSpan rawInterByteTimeout,
            TimeSpan responseTimeout,
            TimeProvider timeProvider)
        {
            if (maximumFrameBytes is < 4 or > DefaultMaximumFrameBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumFrameBytes),
                    maximumFrameBytes,
                    "标准帧最大字节数必须位于 4 至 256。");
            }

            if (receiveBufferBytes is < 4 or > DefaultReceiveBufferBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(receiveBufferBytes),
                    receiveBufferBytes,
                    "接收缓存容量必须位于 4 至 4096。");
            }

            if (receiveBufferBytes < maximumFrameBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(receiveBufferBytes),
                    receiveBufferBytes,
                    "接收缓存容量不得小于标准帧最大字节数。");
            }

            if (rawCaptureMaxBytes is < 4 or > DefaultRawCaptureMaxBytes ||
                rawCaptureMaxBytes > receiveBufferBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rawCaptureMaxBytes),
                    rawCaptureMaxBytes,
                    "原始捕获上限必须位于 4 至 4096，且不得超过接收缓存容量。");
            }

            ValidateTimeout(
                rawInterByteTimeout,
                TimeSpan.FromMilliseconds(2),
                TimeSpan.FromMilliseconds(1000),
                nameof(rawInterByteTimeout),
                "原始模式字节间超时");
            ValidateTimeout(
                responseTimeout,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(5000),
                nameof(responseTimeout),
                "响应总超时");
            ArgumentNullException.ThrowIfNull(timeProvider);

            MaximumFrameBytes = maximumFrameBytes;
            ReceiveBufferBytes = receiveBufferBytes;
            RawCaptureMaxBytes = rawCaptureMaxBytes;
            RawInterByteTimeout = rawInterByteTimeout;
            ResponseTimeout = responseTimeout;
            TimeProvider = timeProvider;
        }

        /// <summary>
        /// 获取单个结构化 RTU 帧允许的最大字节数。
        /// </summary>
        public int MaximumFrameBytes { get; }

        /// <summary>
        /// 获取状态机内部有界接收缓存容量。
        /// </summary>
        public int ReceiveBufferBytes { get; }

        /// <summary>
        /// 获取原始调试模式单段捕获允许的最大字节数。
        /// </summary>
        public int RawCaptureMaxBytes { get; }

        /// <summary>
        /// 获取原始调试模式用于分隔接收段的字节间静默时间。
        /// </summary>
        public TimeSpan RawInterByteTimeout { get; }

        /// <summary>
        /// 获取一轮响应捕获的总超时。
        /// </summary>
        public TimeSpan ResponseTimeout { get; }

        /// <summary>
        /// 获取所有超时计算必须使用的时间源。
        /// </summary>
        public TimeProvider TimeProvider { get; }

        /// <summary>
        /// 由已验证的串口参数创建使用生产容量上限的组帧参数。
        /// </summary>
        /// <param name="settings">提供响应总超时和原始字节间超时的串口参数。</param>
        /// <param name="timeProvider">可选统一时间源；未提供时使用系统时间源。</param>
        /// <returns>使用 256 字节帧上限和 4096 字节有界缓存的新参数。</returns>
        public static RtuFramerOptions FromSerialSettings(
            SerialSettings settings,
            TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(settings);

            return new RtuFramerOptions(
                DefaultMaximumFrameBytes,
                DefaultReceiveBufferBytes,
                DefaultRawCaptureMaxBytes,
                settings.RawInterByteTimeout,
                settings.ResponseTimeout,
                timeProvider ?? TimeProvider.System);
        }

        /// <summary>
        /// 创建采用应用默认超时和生产容量上限的组帧参数。
        /// </summary>
        /// <param name="timeProvider">可选统一时间源；未提供时使用系统时间源。</param>
        /// <returns>适用于 9600 波特率默认串口设置的新参数。</returns>
        public static RtuFramerOptions CreateDefault(TimeProvider? timeProvider = null)
        {
            return FromSerialSettings(
                SerialSettings.CreateDefault("COM_PLACEHOLDER"),
                timeProvider);
        }

        /// <summary>
        /// 校验一个有限超时是否位于指定闭区间内。
        /// </summary>
        /// <param name="value">待校验的超时。</param>
        /// <param name="minimum">允许的最小超时。</param>
        /// <param name="maximum">允许的最大超时。</param>
        /// <param name="parameterName">用于异常定位的参数名。</param>
        /// <param name="displayName">用于异常消息的字段名称。</param>
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
