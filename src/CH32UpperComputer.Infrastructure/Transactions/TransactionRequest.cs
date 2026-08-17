using CH32UpperComputer.Core.Protocol;

namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 表示一次无队列事务提交所需的不可变发送帧、模式和超时参数。
    /// </summary>
    public sealed class TransactionRequest
    {
        /// <summary>
        /// Modbus RTU 发送帧允许的最大字节数。
        /// </summary>
        public const int MaximumRequestFrameBytes = 256;

        /// <summary>
        /// 标准事务和原始调试事务默认使用的响应总超时。
        /// </summary>
        public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 原始调试事务默认使用的字节间静默超时。
        /// </summary>
        public static readonly TimeSpan DefaultRawInterByteTimeout = TimeSpan.FromMilliseconds(20);

        /// <summary>
        /// 当前请求独占持有的完整线路帧。
        /// </summary>
        private readonly byte[] frame;

        /// <summary>
        /// 初始化一项已经完成模式与超时校验的不可变事务请求。
        /// </summary>
        /// <param name="mode">标准匹配或原始调试模式。</param>
        /// <param name="standardRequest">标准模式使用的结构化请求；原始模式为空。</param>
        /// <param name="frame">将被完整写入串口的非空线路帧。</param>
        /// <param name="responseTimeout">首个物理写动作开始后等待响应的总超时。</param>
        /// <param name="rawInterByteTimeout">原始调试模式用于结束未知结构捕获的字节间静默超时。</param>
        private TransactionRequest(
            TransactionMode mode,
            ModbusRequest? standardRequest,
            ReadOnlySpan<byte> frame,
            TimeSpan responseTimeout,
            TimeSpan rawInterByteTimeout)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "事务模式必须是已定义值。");
            }

            if (frame.IsEmpty || frame.Length > MaximumRequestFrameBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frame),
                    frame.Length,
                    "发送帧长度必须位于 1 至 256 字节。");
            }

            ValidateTimeout(
                responseTimeout,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(5000),
                nameof(responseTimeout),
                "响应总超时");
            ValidateTimeout(
                rawInterByteTimeout,
                TimeSpan.FromMilliseconds(2),
                TimeSpan.FromMilliseconds(1000),
                nameof(rawInterByteTimeout),
                "原始字节间静默超时");

            if (mode == TransactionMode.Standard && standardRequest is null)
            {
                throw new ArgumentException("标准事务必须携带结构化 Modbus 请求。", nameof(standardRequest));
            }

            if (mode == TransactionMode.RawDebug && standardRequest is not null)
            {
                throw new ArgumentException("原始调试事务不得携带标准请求签名。", nameof(standardRequest));
            }

            Mode = mode;
            StandardRequest = standardRequest;
            this.frame = frame.ToArray();
            ResponseTimeout = responseTimeout;
            RawInterByteTimeout = rawInterByteTimeout;
        }

        /// <summary>
        /// 获取标准响应匹配或原始调试捕获模式。
        /// </summary>
        public TransactionMode Mode { get; }

        /// <summary>
        /// 获取标准模式的结构化 Modbus 请求；原始调试模式返回 <see langword="null"/>。
        /// </summary>
        public ModbusRequest? StandardRequest { get; }

        /// <summary>
        /// 获取完整发送帧的防御性副本。
        /// </summary>
        public ReadOnlyMemory<byte> Frame => (byte[])frame.Clone();

        /// <summary>
        /// 获取从首个物理写动作开始时刻计算的响应总超时。
        /// </summary>
        public TimeSpan ResponseTimeout { get; }

        /// <summary>
        /// 获取原始调试模式的字节间静默超时。
        /// </summary>
        public TimeSpan RawInterByteTimeout { get; }

        /// <summary>
        /// 创建一项必须严格匹配结构化请求签名的标准事务。
        /// </summary>
        /// <param name="request">由统一工厂或严格请求解析器创建的 Modbus 请求。</param>
        /// <param name="responseTimeout">可选响应总超时；未提供时为一秒。</param>
        /// <param name="rawInterByteTimeout">组帧器溢出恢复使用的静默超时；未提供时为二十毫秒。</param>
        /// <returns>新的不可变标准事务请求。</returns>
        public static TransactionRequest CreateStandard(
            ModbusRequest request,
            TimeSpan? responseTimeout = null,
            TimeSpan? rawInterByteTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(request);

            return new TransactionRequest(
                TransactionMode.Standard,
                request,
                request.RawFrame.Span,
                responseTimeout ?? DefaultResponseTimeout,
                rawInterByteTimeout ?? DefaultRawInterByteTimeout);
        }

        /// <summary>
        /// 创建一项不要求请求签名的原始调试事务。
        /// </summary>
        /// <param name="frame">将被原样写入串口的非空线路帧。</param>
        /// <param name="responseTimeout">可选响应总超时；未提供时为一秒。</param>
        /// <param name="rawInterByteTimeout">未知结构捕获的字节间静默超时；未提供时为二十毫秒。</param>
        /// <returns>新的不可变原始调试事务请求。</returns>
        public static TransactionRequest CreateRawDebug(
            ReadOnlySpan<byte> frame,
            TimeSpan? responseTimeout = null,
            TimeSpan? rawInterByteTimeout = null)
        {
            return new TransactionRequest(
                TransactionMode.RawDebug,
                null,
                frame,
                responseTimeout ?? DefaultResponseTimeout,
                rawInterByteTimeout ?? DefaultRawInterByteTimeout);
        }

        /// <summary>
        /// 返回用于日志和残余歧义提示的稳定请求签名摘要。
        /// </summary>
        /// <returns>标准模式包含地址、功能、起始地址、数量和完整写值签名；原始模式包含帧长度。</returns>
        public string CreateSignatureSummary()
        {
            if (StandardRequest is null)
            {
                return $"RawDebug/{frame.Length} bytes";
            }

            string prefix = $"地址 0x{StandardRequest.SlaveAddress:X2}，" +
                $"功能 0x{(byte)StandardRequest.FunctionCode:X2}，" +
                $"起始 0x{StandardRequest.StartAddress:X4}，数量 {StandardRequest.Quantity}";
            ReadOnlyMemory<ushort> values = StandardRequest.Values;

            if (values.IsEmpty)
            {
                return prefix;
            }

            const int maximumDisplayedValueCount = 8;
            int displayedValueCount = Math.Min(values.Length, maximumDisplayedValueCount);
            string displayedValues = string.Join(
                ",",
                values.Span[..displayedValueCount].ToArray().Select(value => $"0x{value:X4}"));
            string omittedSuffix = values.Length > displayedValueCount
                ? $",…(+{values.Length - displayedValueCount})"
                : string.Empty;
            ulong valueSignature = ComputeValueSignature(values.Span);

            return $"{prefix}，写值 [{displayedValues}{omittedSuffix}]，" +
                $"值签名 0x{valueSignature:X16}";
        }

        /// <summary>
        /// 对完整写值序列计算稳定且有界显示的 64 位 FNV-1a 签名。
        /// </summary>
        /// <param name="values">按线路顺序排列的完整寄存器写值。</param>
        /// <returns>同时覆盖每个值高字节和低字节的稳定签名。</returns>
        private static ulong ComputeValueSignature(ReadOnlySpan<ushort> values)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offsetBasis;

            foreach (ushort value in values)
            {
                unchecked
                {
                    hash ^= (byte)(value >> 8);
                    hash *= prime;
                    hash ^= (byte)value;
                    hash *= prime;
                }
            }

            return hash;
        }

        /// <summary>
        /// 校验有限超时是否位于指定闭区间。
        /// </summary>
        /// <param name="value">待校验超时。</param>
        /// <param name="minimum">允许的最小超时。</param>
        /// <param name="maximum">允许的最大超时。</param>
        /// <param name="parameterName">用于异常定位的参数名称。</param>
        /// <param name="displayName">用于异常消息的中文字段名。</param>
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
