namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 定义可替换的操作系统串口发现边界，使界面不直接依赖静态串口和 PnP 查询 API。
    /// </summary>
    public interface ISerialPortDiscovery
    {
        /// <summary>
        /// 异步枚举当前操作系统可见的串口及其友好名称。
        /// </summary>
        /// <param name="cancellationToken">取消尚未开始或尚未提交结果的设备查询。</param>
        /// <returns>自然排序后的串口描述和可行动诊断信息。</returns>
        ValueTask<SerialPortDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken);
    }
}
