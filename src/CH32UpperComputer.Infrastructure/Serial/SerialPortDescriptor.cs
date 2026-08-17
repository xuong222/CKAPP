namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 保存一个当前可见串口的端口号、PnP 友好名称和设备身份。
    /// </summary>
    public sealed class SerialPortDescriptor
    {
        /// <summary>
        /// 初始化一项不可变串口描述。
        /// </summary>
        /// <param name="portName">规范化为大写的 COM 端口号。</param>
        /// <param name="friendlyName">设备管理器提供的友好名称；未知时与端口号相同。</param>
        /// <param name="pnpDeviceId">可选 PnP 设备标识。</param>
        /// <param name="isPreferredUsbDevice">是否为当前项目优先使用的 WCH、CH340、CH341 或 CH32 USB 串口。</param>
        public SerialPortDescriptor(
            string portName,
            string friendlyName,
            string? pnpDeviceId,
            bool isPreferredUsbDevice)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(portName);
            ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);
            PortName = portName.Trim().ToUpperInvariant();
            FriendlyName = friendlyName.Trim();
            PnpDeviceId = string.IsNullOrWhiteSpace(pnpDeviceId)
                ? null
                : pnpDeviceId.Trim();
            IsPreferredUsbDevice = isPreferredUsbDevice;
        }

        /// <summary>
        /// 获取用于实际打开串口的规范 COM 端口号。
        /// </summary>
        public string PortName { get; }

        /// <summary>
        /// 获取操作系统设备管理器提供的友好名称。
        /// </summary>
        public string FriendlyName { get; }

        /// <summary>
        /// 获取可选 PnP 设备标识。
        /// </summary>
        public string? PnpDeviceId { get; }

        /// <summary>
        /// 获取是否为当前设备优先使用的 WCH 系列 USB 串口。
        /// </summary>
        public bool IsPreferredUsbDevice { get; }

        /// <summary>
        /// 获取串口下拉框中显示的端口号和设备名称组合。
        /// </summary>
        public string DisplayName => FriendlyName.Contains(
            PortName,
            StringComparison.OrdinalIgnoreCase)
                ? FriendlyName
                : $"{FriendlyName} ({PortName})";

        /// <summary>
        /// 返回编辑型下拉框需要写入的实际端口号，避免把友好名称当作 SerialPort.PortName。
        /// </summary>
        /// <returns>规范 COM 端口号。</returns>
        public override string ToString()
        {
            return PortName;
        }
    }

    /// <summary>
    /// 保存一次串口发现的不可变结果。
    /// </summary>
    public sealed class SerialPortDiscoveryResult
    {
        /// <summary>
        /// 初始化一份串口发现结果。
        /// </summary>
        /// <param name="ports">已经自然排序且端口号唯一的串口描述。</param>
        /// <param name="diagnosticMessage">枚举降级、失败或无设备时显示的诊断信息。</param>
        public SerialPortDiscoveryResult(
            IReadOnlyList<SerialPortDescriptor> ports,
            string diagnosticMessage)
        {
            ArgumentNullException.ThrowIfNull(ports);
            ArgumentNullException.ThrowIfNull(diagnosticMessage);
            Ports = Array.AsReadOnly(ports.ToArray());
            DiagnosticMessage = diagnosticMessage;
        }

        /// <summary>
        /// 获取端口号唯一且自然排序的串口描述。
        /// </summary>
        public IReadOnlyList<SerialPortDescriptor> Ports { get; }

        /// <summary>
        /// 获取枚举降级、失败或无设备时的可行动诊断信息。
        /// </summary>
        public string DiagnosticMessage { get; }
    }
}
