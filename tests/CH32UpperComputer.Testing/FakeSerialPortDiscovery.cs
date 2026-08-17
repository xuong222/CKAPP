using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Testing
{
    /// <summary>
    /// 提供可脚本化且不访问操作系统设备管理器的串口发现服务。
    /// </summary>
    public sealed class FakeSerialPortDiscovery : ISerialPortDiscovery
    {
        /// <summary>
        /// 保护当前发现结果和查询次数。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 当前下一次查询需要返回的发现结果。
        /// </summary>
        private SerialPortDiscoveryResult result;

        /// <summary>
        /// 已经执行的发现查询次数。
        /// </summary>
        private int discoveryCount;

        /// <summary>
        /// 初始化一个返回指定端口描述的模拟发现服务。
        /// </summary>
        /// <param name="ports">每次查询返回的串口描述。</param>
        /// <param name="diagnosticMessage">每次查询返回的可选诊断信息。</param>
        public FakeSerialPortDiscovery(
            IEnumerable<SerialPortDescriptor> ports,
            string diagnosticMessage = "")
        {
            ArgumentNullException.ThrowIfNull(ports);
            ArgumentNullException.ThrowIfNull(diagnosticMessage);
            result = new SerialPortDiscoveryResult(ports.ToArray(), diagnosticMessage);
        }

        /// <summary>
        /// 获取已经执行的发现查询次数。
        /// </summary>
        public int DiscoveryCount
        {
            get
            {
                lock (syncRoot)
                {
                    return discoveryCount;
                }
            }
        }

        /// <summary>
        /// 原子替换后续发现调用返回的端口结果。
        /// </summary>
        /// <param name="ports">新的串口描述集合。</param>
        /// <param name="diagnosticMessage">新的可选诊断信息。</param>
        public void SetResult(
            IEnumerable<SerialPortDescriptor> ports,
            string diagnosticMessage = "")
        {
            ArgumentNullException.ThrowIfNull(ports);
            ArgumentNullException.ThrowIfNull(diagnosticMessage);

            lock (syncRoot)
            {
                result = new SerialPortDiscoveryResult(
                    ports.ToArray(),
                    diagnosticMessage);
            }
        }

        /// <summary>
        /// 返回当前脚本化结果的独立不可变副本。
        /// </summary>
        /// <param name="cancellationToken">在读取模拟结果前取消查询。</param>
        /// <returns>当前端口描述和诊断信息。</returns>
        public ValueTask<SerialPortDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (syncRoot)
            {
                discoveryCount = checked(discoveryCount + 1);
                return ValueTask.FromResult(
                    new SerialPortDiscoveryResult(
                        result.Ports,
                        result.DiagnosticMessage));
            }
        }
    }
}
