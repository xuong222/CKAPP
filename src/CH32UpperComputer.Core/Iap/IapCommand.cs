namespace CH32UpperComputer.Core.Iap
{
    /// <summary>
    /// 定义 CH32V317 Ethernet IAP 协议的固定命令数值。
    /// </summary>
    public enum IapCommand : uint
    {
        /// <summary>
        /// 查询 Bootloader 和当前 App 信息。
        /// </summary>
        Hello = 0x48454C4F,

        /// <summary>
        /// 声明镜像并让 Bootloader 擦除 App 区。
        /// </summary>
        Begin = 0x4245474E,

        /// <summary>
        /// 顺序写入一个固件分片。
        /// </summary>
        Data = 0x44415441,

        /// <summary>
        /// 完成并校验整个镜像。
        /// </summary>
        End = 0x454E4420,

        /// <summary>
        /// 请求 Bootloader 跳转到已验证 App。
        /// </summary>
        Run = 0x52554E20,

        /// <summary>
        /// 取消当前升级并保持 App 无效。
        /// </summary>
        Abort = 0x41425254,
    }

    /// <summary>
    /// 定义 Bootloader 响应头中的确认状态。
    /// </summary>
    public enum IapResponseStatus : uint
    {
        /// <summary>
        /// 请求已经被正确处理。
        /// </summary>
        Ack = 0U,

        /// <summary>
        /// 请求被拒绝，detail 携带错误信息。
        /// </summary>
        Nack = 1U,
    }

    /// <summary>
    /// 定义完整固件升级流程的互斥状态。
    /// </summary>
    public enum IapUpgradeState
    {
        /// <summary>
        /// 尚未选择固件或升级流程已经复位。
        /// </summary>
        Idle,

        /// <summary>
        /// 固件文件已经完成校验。
        /// </summary>
        FirmwareReady,

        /// <summary>
        /// 正在等待目标 Bootloader TCP 服务。
        /// </summary>
        Connecting,

        /// <summary>
        /// HELLO 已经验证目标设备。
        /// </summary>
        BootloaderConnected,

        /// <summary>
        /// BEGIN 已发送，正在等待擦除完成。
        /// </summary>
        Erasing,

        /// <summary>
        /// 正在顺序发送 DATA 分片。
        /// </summary>
        Transferring,

        /// <summary>
        /// END 后正在重新查询并验证 App 元数据。
        /// </summary>
        Verifying,

        /// <summary>
        /// App 有效状态、大小和 CRC32 已经匹配。
        /// </summary>
        UpgradeSucceeded,

        /// <summary>
        /// 用户主动取消升级。
        /// </summary>
        Cancelled,

        /// <summary>
        /// 已经确定升级失败。
        /// </summary>
        Failed,

        /// <summary>
        /// 网络状态导致无法确定设备最终结果。
        /// </summary>
        Uncertain,
    }

    /// <summary>
    /// 定义 RUN 命令独立于固件升级结果的状态。
    /// </summary>
    public enum IapRunState
    {
        /// <summary>
        /// 尚未请求运行 App。
        /// </summary>
        NotRequested,

        /// <summary>
        /// 正在发送 RUN 并等待响应。
        /// </summary>
        Starting,

        /// <summary>
        /// 已收到完整匹配的 RUN ACK。
        /// </summary>
        Confirmed,

        /// <summary>
        /// RUN 可能已送达，但未能取得完整 ACK。
        /// </summary>
        Uncertain,

        /// <summary>
        /// Bootloader 明确返回 RUN NACK。
        /// </summary>
        Rejected,
    }
}
