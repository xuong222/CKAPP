namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 以大小写不敏感方式登记进程内串口占用，并通过租约精确控制释放时刻。
    /// </summary>
    public sealed class SerialPortUsageRegistry
    {
        /// <summary>
        /// 保护全部活动租约的同步门。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 按操作系统端口名称保存当前唯一租约。
        /// </summary>
        private readonly Dictionary<string, SerialPortLease> activeLeases =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 为指定页面取得一个端口的唯一占用租约。
        /// </summary>
        /// <param name="portName">待占用的操作系统串口名称。</param>
        /// <param name="ownerName">面向用户显示的占用方名称。</param>
        /// <returns>必须在物理句柄完全释放后才释放的唯一租约。</returns>
        /// <exception cref="ArgumentException">端口名或占用方名称为空时抛出。</exception>
        /// <exception cref="SerialPortInUseException">端口已经由进程内另一会话占用时抛出。</exception>
        public SerialPortLease Acquire(
            string portName,
            string ownerName)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                throw new ArgumentException("串口名称不能为空。", nameof(portName));
            }

            if (string.IsNullOrWhiteSpace(ownerName))
            {
                throw new ArgumentException("串口占用方名称不能为空。", nameof(ownerName));
            }

            string normalizedPortName = portName.Trim();
            string normalizedOwnerName = ownerName.Trim();

            lock (syncRoot)
            {
                if (activeLeases.TryGetValue(normalizedPortName, out SerialPortLease? existingLease))
                {
                    throw new SerialPortInUseException(
                        normalizedPortName,
                        existingLease.OwnerName);
                }

                SerialPortLease lease = new(
                    this,
                    normalizedPortName,
                    normalizedOwnerName);
                activeLeases.Add(normalizedPortName, lease);
                return lease;
            }
        }

        /// <summary>
        /// 仅在调用者仍是登记中的同一租约时释放端口，防止迟到的重复释放误伤新会话。
        /// </summary>
        /// <param name="lease">请求释放的唯一租约实例。</param>
        internal void Release(SerialPortLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);

            lock (syncRoot)
            {
                if (activeLeases.TryGetValue(lease.PortName, out SerialPortLease? activeLease) &&
                    ReferenceEquals(activeLease, lease))
                {
                    activeLeases.Remove(lease.PortName);
                }
            }
        }
    }

    /// <summary>
    /// 表示一个端口在进程内的唯一占用权。
    /// </summary>
    public sealed class SerialPortLease : IDisposable
    {
        /// <summary>
        /// 负责登记和释放本租约的共享注册表。
        /// </summary>
        private SerialPortUsageRegistry? registry;

        /// <summary>
        /// 初始化只能由注册表创建的唯一租约。
        /// </summary>
        /// <param name="registry">拥有本租约的共享注册表。</param>
        /// <param name="portName">租约占用的规范化串口名称。</param>
        /// <param name="ownerName">面向用户显示的占用方名称。</param>
        internal SerialPortLease(
            SerialPortUsageRegistry registry,
            string portName,
            string ownerName)
        {
            this.registry = registry;
            PortName = portName;
            OwnerName = ownerName;
        }

        /// <summary>
        /// 获取租约占用的串口名称。
        /// </summary>
        public string PortName { get; }

        /// <summary>
        /// 获取面向用户显示的占用方名称。
        /// </summary>
        public string OwnerName { get; }

        /// <summary>
        /// 幂等释放端口占用权，使后续会话可以重新取得该端口。
        /// </summary>
        public void Dispose()
        {
            SerialPortUsageRegistry? ownerRegistry = Interlocked.Exchange(ref registry, null);
            ownerRegistry?.Release(this);
        }
    }

    /// <summary>
    /// 表示目标串口已被当前进程内另一页面占用。
    /// </summary>
    public sealed class SerialPortInUseException : InvalidOperationException
    {
        /// <summary>
        /// 初始化包含端口和现有占用方的中文冲突异常。
        /// </summary>
        /// <param name="portName">发生冲突的串口名称。</param>
        /// <param name="currentOwner">当前持有租约的页面或会话名称。</param>
        public SerialPortInUseException(
            string portName,
            string currentOwner)
            : base($"串口 {portName} 已由“{currentOwner}”占用，请选择其他串口或先断开现有连接。")
        {
            PortName = portName;
            CurrentOwner = currentOwner;
        }

        /// <summary>
        /// 获取发生冲突的串口名称。
        /// </summary>
        public string PortName { get; }

        /// <summary>
        /// 获取当前持有该端口的占用方名称。
        /// </summary>
        public string CurrentOwner { get; }
    }
}
