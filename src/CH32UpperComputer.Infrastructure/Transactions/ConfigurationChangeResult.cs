using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 指定专用安全配置流程的操作类型。
    /// </summary>
    public enum ConfigurationChangeKind
    {
        /// <summary>
        /// 修改设备从站地址寄存器 40001。
        /// </summary>
        SlaveAddress = 0,

        /// <summary>
        /// 修改设备波特率代码寄存器 40002。
        /// </summary>
        BaudRate = 1,

        /// <summary>
        /// 写入恢复出厂命令寄存器 40036。
        /// </summary>
        FactoryReset = 2,

        /// <summary>
        /// 使用固定 0xFE 请求发现总线上唯一设备的真实地址。
        /// </summary>
        UnknownAddressDiscovery = 3,
    }

    /// <summary>
    /// 指定专用配置流程的确定性结果分类。
    /// </summary>
    public enum ConfigurationChangeStatus
    {
        /// <summary>
        /// 标准响应完整回显，且所需本地状态或串口重连已经完成。
        /// </summary>
        Succeeded = 0,

        /// <summary>
        /// 操作确定失败，或设备配置已知但本地重连失败。
        /// </summary>
        Failed = 1,

        /// <summary>
        /// 另一专用流程或普通事务占用无队列活动门，本操作没有排队。
        /// </summary>
        Busy = 2,

        /// <summary>
        /// 写请求可能已经生效但没有获得完整回显，不能猜测设备实际配置。
        /// </summary>
        ConfigurationUncertain = 3,
    }

    /// <summary>
    /// 表示可供连接、恢复和界面展示使用的一组设备地址与串口参数。
    /// </summary>
    public sealed class DeviceConnectionConfiguration
    {
        /// <summary>
        /// 初始化一组经过地址范围校验的不可变设备连接配置。
        /// </summary>
        /// <param name="slaveAddress">真实设备从站地址，允许 1 至 64。</param>
        /// <param name="serialSettings">与该设备配置配套的完整串口参数。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="slaveAddress"/> 不在 1 至 64 时抛出。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="serialSettings"/> 为空时抛出。</exception>
        public DeviceConnectionConfiguration(
            byte slaveAddress,
            SerialSettings serialSettings)
        {
            if (slaveAddress is < 1 or > 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slaveAddress),
                    slaveAddress,
                    "真实设备从站地址必须位于 1 至 64。");
            }

            ArgumentNullException.ThrowIfNull(serialSettings);
            SlaveAddress = slaveAddress;
            SerialSettings = serialSettings;
        }

        /// <summary>
        /// 获取真实设备从站地址。
        /// </summary>
        public byte SlaveAddress { get; }

        /// <summary>
        /// 获取完整串口通信参数。
        /// </summary>
        public SerialSettings SerialSettings { get; }
    }

    /// <summary>
    /// 表示一次专用配置流程的不可变结果、事务证据和旧新恢复选项。
    /// </summary>
    public sealed class ConfigurationChangeResult
    {
        /// <summary>
        /// 初始化一项经过基本一致性校验的专用配置结果。
        /// </summary>
        /// <param name="kind">专用配置操作类型。</param>
        /// <param name="status">成功、失败、Busy 或配置不确定分类。</param>
        /// <param name="message">面向界面和日志的明确说明。</param>
        /// <param name="previousConfiguration">操作开始时的旧连接配置。</param>
        /// <param name="proposedConfiguration">新地址、新波特率或默认配置恢复选项。</param>
        /// <param name="localConfiguration">操作结束后软件当前保留的本地配置。</param>
        /// <param name="transactionResult">可选协调器提交结果。</param>
        /// <param name="discoveredSlaveAddress">0xFE 成功时严格发现的真实地址。</param>
        /// <param name="exception">重连或本地生命周期失败时的底层异常。</param>
        /// <exception cref="ArgumentOutOfRangeException">枚举或发现地址无效时抛出。</exception>
        /// <exception cref="ArgumentException">消息为空或不确定结果缺少新配置时抛出。</exception>
        internal ConfigurationChangeResult(
            ConfigurationChangeKind kind,
            ConfigurationChangeStatus status,
            string message,
            DeviceConnectionConfiguration previousConfiguration,
            DeviceConnectionConfiguration? proposedConfiguration,
            DeviceConnectionConfiguration localConfiguration,
            TransactionExecutionResult? transactionResult = null,
            byte? discoveredSlaveAddress = null,
            Exception? exception = null)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            if (!Enum.IsDefined(status))
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("配置流程结果说明不能为空。", nameof(message));
            }

            ArgumentNullException.ThrowIfNull(previousConfiguration);
            ArgumentNullException.ThrowIfNull(localConfiguration);

            if (status == ConfigurationChangeStatus.ConfigurationUncertain &&
                proposedConfiguration is null)
            {
                throw new ArgumentException("配置不确定结果必须同时提供旧配置和新配置恢复选项。");
            }

            if (discoveredSlaveAddress.HasValue)
            {
                if (kind != ConfigurationChangeKind.UnknownAddressDiscovery ||
                    status != ConfigurationChangeStatus.Succeeded ||
                    discoveredSlaveAddress.Value is < 1 or > 64)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(discoveredSlaveAddress),
                        discoveredSlaveAddress,
                        "发现地址只能用于成功的 0xFE 查询且必须位于 1 至 64。");
                }
            }

            Kind = kind;
            Status = status;
            Message = message;
            PreviousConfiguration = previousConfiguration;
            ProposedConfiguration = proposedConfiguration;
            LocalConfiguration = localConfiguration;
            TransactionResult = transactionResult;
            DiscoveredSlaveAddress = discoveredSlaveAddress;
            Exception = exception;
        }

        /// <summary>
        /// 获取专用配置操作类型。
        /// </summary>
        public ConfigurationChangeKind Kind { get; }

        /// <summary>
        /// 获取配置结果分类。
        /// </summary>
        public ConfigurationChangeStatus Status { get; }

        /// <summary>
        /// 获取面向界面和日志的明确说明。
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 获取操作开始时的旧配置；不确定状态下可供“尝试旧配置”使用。
        /// </summary>
        public DeviceConnectionConfiguration PreviousConfiguration { get; }

        /// <summary>
        /// 获取建议的新配置或默认配置；不确定状态下可供“尝试新配置”使用。
        /// </summary>
        public DeviceConnectionConfiguration? ProposedConfiguration { get; }

        /// <summary>
        /// 获取操作结束后软件当前保留的本地配置，不代表不确定状态下的设备真实配置。
        /// </summary>
        public DeviceConnectionConfiguration LocalConfiguration { get; }

        /// <summary>
        /// 获取协调器提交结果；专用服务自身 Busy 时可以为空。
        /// </summary>
        public TransactionExecutionResult? TransactionResult { get; }

        /// <summary>
        /// 获取 0xFE 查询严格发现的真实地址；其他流程为空。
        /// </summary>
        public byte? DiscoveredSlaveAddress { get; }

        /// <summary>
        /// 获取串口重连等本地生命周期故障；没有本地异常时为空。
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// 获取设备配置是否因响应丢失而无法确定。
        /// </summary>
        public bool IsConfigurationUncertain =>
            Status == ConfigurationChangeStatus.ConfigurationUncertain;
    }
}
