namespace CH32UpperComputer.Core.Iap
{
    /// <summary>
    /// 表示一条待编码的固定 IAP 请求头。
    /// </summary>
    public sealed class IapRequestHeader
    {
        /// <summary>
        /// 初始化一条 IAP 请求头。
        /// </summary>
        /// <param name="command">需要执行的固定 IAP 命令。</param>
        /// <param name="sequence">当前 TCP 连接内从一开始递增的请求序号。</param>
        /// <param name="offset">DATA 相对于 App 基址的偏移，其他命令为零。</param>
        /// <param name="length">BEGIN 的镜像长度、DATA 的载荷长度或其他命令的零。</param>
        /// <param name="payloadCrc32">DATA 分片 CRC32，其他命令为零。</param>
        /// <param name="imageCrc32">BEGIN 整体镜像 CRC32，其他命令为零。</param>
        public IapRequestHeader(
            IapCommand command,
            uint sequence,
            uint offset,
            uint length,
            uint payloadCrc32,
            uint imageCrc32)
        {
            if (sequence == 0U)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sequence),
                    "IAP sequence 必须从一开始。");
            }

            Command = command;
            Sequence = sequence;
            Offset = offset;
            Length = length;
            PayloadCrc32 = payloadCrc32;
            ImageCrc32 = imageCrc32;
        }

        /// <summary>
        /// 获取命令码。
        /// </summary>
        public IapCommand Command { get; }

        /// <summary>
        /// 获取当前连接内请求序号。
        /// </summary>
        public uint Sequence { get; }

        /// <summary>
        /// 获取 DATA 相对偏移。
        /// </summary>
        public uint Offset { get; }

        /// <summary>
        /// 获取命令相关长度。
        /// </summary>
        public uint Length { get; }

        /// <summary>
        /// 获取 DATA 分片 CRC32。
        /// </summary>
        public uint PayloadCrc32 { get; }

        /// <summary>
        /// 获取 BEGIN 整体镜像 CRC32。
        /// </summary>
        public uint ImageCrc32 { get; }
    }

    /// <summary>
    /// 表示已经从固定 24 字节响应头解析出的字段。
    /// </summary>
    public sealed class IapResponseHeader
    {
        /// <summary>
        /// 初始化已经校验基本布局的响应头。
        /// </summary>
        /// <param name="command">Bootloader 回显的命令。</param>
        /// <param name="sequence">Bootloader 回显的请求序号。</param>
        /// <param name="status">ACK 或 NACK 状态。</param>
        /// <param name="detail">命令相关的确认值或错误码。</param>
        public IapResponseHeader(
            IapCommand command,
            uint sequence,
            IapResponseStatus status,
            uint detail)
        {
            Command = command;
            Sequence = sequence;
            Status = status;
            Detail = detail;
        }

        /// <summary>
        /// 获取回显命令。
        /// </summary>
        public IapCommand Command { get; }

        /// <summary>
        /// 获取回显序号。
        /// </summary>
        public uint Sequence { get; }

        /// <summary>
        /// 获取 ACK 或 NACK 状态。
        /// </summary>
        public IapResponseStatus Status { get; }

        /// <summary>
        /// 获取命令相关 detail。
        /// </summary>
        public uint Detail { get; }
    }

    /// <summary>
    /// 表示 HELLO ACK 后固定 24 字节设备信息。
    /// </summary>
    public sealed class IapHelloInfo
    {
        /// <summary>
        /// 初始化 Bootloader 和 App 元数据。
        /// </summary>
        /// <param name="bootVersion">Bootloader 原始版本值。</param>
        /// <param name="appBase">设备声明的 App Flash 基址。</param>
        /// <param name="appMaxSize">设备声明的 App 最大字节数。</param>
        /// <param name="appValid">当前 App 是否有效。</param>
        /// <param name="appSize">当前有效 App 的字节数。</param>
        /// <param name="appCrc32">当前有效 App 的整体 CRC32。</param>
        public IapHelloInfo(
            uint bootVersion,
            uint appBase,
            uint appMaxSize,
            bool appValid,
            uint appSize,
            uint appCrc32)
        {
            BootVersion = bootVersion;
            AppBase = appBase;
            AppMaxSize = appMaxSize;
            AppValid = appValid;
            AppSize = appSize;
            AppCrc32 = appCrc32;
        }

        /// <summary>
        /// 获取 Bootloader 原始版本。
        /// </summary>
        public uint BootVersion { get; }

        /// <summary>
        /// 获取 App 基址。
        /// </summary>
        public uint AppBase { get; }

        /// <summary>
        /// 获取 App 最大长度。
        /// </summary>
        public uint AppMaxSize { get; }

        /// <summary>
        /// 获取当前 App 是否有效。
        /// </summary>
        public bool AppValid { get; }

        /// <summary>
        /// 获取当前 App 长度。
        /// </summary>
        public uint AppSize { get; }

        /// <summary>
        /// 获取当前 App CRC32。
        /// </summary>
        public uint AppCrc32 { get; }
    }
}
