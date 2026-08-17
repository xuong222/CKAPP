using System.Globalization;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 合并 System.IO.Ports 注册表结果和 Windows PnP 友好名称的生产串口发现实现。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed partial class WindowsSerialPortDiscovery : ISerialPortDiscovery
    {
        /// <summary>
        /// 在后台线程执行可能阻塞的注册表和 WMI 查询。
        /// </summary>
        /// <param name="cancellationToken">取消尚未开始或尚未提交结果的设备查询。</param>
        /// <returns>自然排序后的串口描述和降级诊断。</returns>
        public async ValueTask<SerialPortDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SerialPortDiscoveryResult result = await Task.Run(
                DiscoverCore,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        /// <summary>
        /// 同步完成一次系统串口和 PnP 设备合并查询。
        /// </summary>
        /// <returns>端口号唯一且自然排序的发现结果。</returns>
        private static SerialPortDiscoveryResult DiscoverCore()
        {
            Dictionary<string, MutablePortRecord> records = new(
                StringComparer.OrdinalIgnoreCase);
            List<string> failures = [];

            try
            {
                foreach (string rawPortName in SerialPort.GetPortNames())
                {
                    if (TryNormalizePortName(rawPortName, out string portName))
                    {
                        records.TryAdd(
                            portName,
                            new MutablePortRecord(portName, portName, null));
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
            {
                failures.Add($"系统串口表查询失败：{exception.Message}");
            }

            try
            {
                using ManagementObjectSearcher searcher = new(
                    "SELECT Name, PNPDeviceID, Status, ConfigManagerErrorCode " +
                    "FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                using ManagementObjectCollection devices = searcher.Get();

                foreach (ManagementObject device in devices.Cast<ManagementObject>())
                {
                    string friendlyName = Convert.ToString(
                        device["Name"],
                        CultureInfo.InvariantCulture) ?? string.Empty;

                    if (!TryExtractPortName(friendlyName, out string portName))
                    {
                        continue;
                    }

                    string? pnpDeviceId = Convert.ToString(
                        device["PNPDeviceID"],
                        CultureInfo.InvariantCulture);

                    if (records.TryGetValue(portName, out MutablePortRecord? existing))
                    {
                        existing.FriendlyName = friendlyName;
                        existing.PnpDeviceId = pnpDeviceId;
                    }
                    else
                    {
                        records.Add(
                            portName,
                            new MutablePortRecord(
                                portName,
                                friendlyName,
                                pnpDeviceId));
                    }
                }
            }
            catch (Exception exception) when (
                exception is ManagementException or
                UnauthorizedAccessException or
                COMException)
            {
                failures.Add($"PnP 设备名称查询失败：{exception.Message}");
            }

            SerialPortDescriptor[] ports = records.Values
                .Select(
                    record => new SerialPortDescriptor(
                        record.PortName,
                        record.FriendlyName,
                        record.PnpDeviceId,
                        IsPreferredUsbDevice(record.FriendlyName, record.PnpDeviceId)))
                .OrderBy(
                    descriptor => ParsePortNumber(descriptor.PortName))
                .ThenBy(
                    descriptor => descriptor.PortName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string diagnosticMessage = CreateDiagnosticMessage(ports.Length, failures);
            return new SerialPortDiscoveryResult(ports, diagnosticMessage);
        }

        /// <summary>
        /// 将任意大小写的 COM 名称规范化，并拒绝非串口文本。
        /// </summary>
        /// <param name="candidate">待校验的端口名称。</param>
        /// <param name="portName">成功时接收规范化的大写端口号。</param>
        /// <returns>文本严格匹配 COM 加正整数时返回真。</returns>
        private static bool TryNormalizePortName(
            string? candidate,
            out string portName)
        {
            string normalized = candidate?.Trim().ToUpperInvariant() ?? string.Empty;
            bool isValid = PortNameRegex().IsMatch(normalized);
            portName = isValid ? normalized : string.Empty;
            return isValid;
        }

        /// <summary>
        /// 从“设备名称 (COM7)”形式的 PnP 友好名称中提取端口号。
        /// </summary>
        /// <param name="friendlyName">设备管理器友好名称。</param>
        /// <param name="portName">成功时接收规范化端口号。</param>
        /// <returns>找到合法 COM 端口号时返回真。</returns>
        private static bool TryExtractPortName(
            string friendlyName,
            out string portName)
        {
            Match match = PortNameWithinFriendlyNameRegex().Match(friendlyName);
            return TryNormalizePortName(
                match.Success ? match.Groups["port"].Value : null,
                out portName);
        }

        /// <summary>
        /// 根据友好名称和 PnP 标识识别当前项目优先使用的 WCH 系列 USB 串口。
        /// </summary>
        /// <param name="friendlyName">设备管理器友好名称。</param>
        /// <param name="pnpDeviceId">可选 PnP 设备标识。</param>
        /// <returns>匹配 WCH、CH340、CH341、CH32、USB-SERIAL 或常见 WCH VID 时返回真。</returns>
        private static bool IsPreferredUsbDevice(
            string friendlyName,
            string? pnpDeviceId)
        {
            string combined = $"{friendlyName} {pnpDeviceId}";
            return combined.Contains("WCH", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("CH340", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("CH341", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("CH32", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("USB-SERIAL", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("VID_4348", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 将规范 COM 端口名称转换为自然排序使用的整数编号。
        /// </summary>
        /// <param name="portName">已经规范化的 COM 端口名称。</param>
        /// <returns>COM 后的正整数编号。</returns>
        private static int ParsePortNumber(string portName)
        {
            return int.Parse(
                portName.AsSpan(3),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 根据发现数量和降级异常创建用户可行动的诊断信息。
        /// </summary>
        /// <param name="portCount">成功合并的端口数量。</param>
        /// <param name="failures">注册表或 PnP 查询的降级异常。</param>
        /// <returns>无异常且有端口时为空，否则为明确提示。</returns>
        private static string CreateDiagnosticMessage(
            int portCount,
            IReadOnlyList<string> failures)
        {
            if (portCount == 0)
            {
                string suffix = failures.Count == 0
                    ? string.Empty
                    : $" 诊断：{string.Join("；", failures)}";
                return $"未检测到可用串口，请检查 USB 连接和 WCH/CH340 驱动。{suffix}";
            }

            return failures.Count == 0
                ? string.Empty
                : $"串口列表已使用降级结果。{string.Join("；", failures)}";
        }

        /// <summary>
        /// 匹配完整 COM 端口名称。
        /// </summary>
        /// <returns>忽略大小写的编译期正则表达式。</returns>
        [GeneratedRegex("^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex PortNameRegex();

        /// <summary>
        /// 匹配 PnP 友好名称末尾括号中的 COM 端口号。
        /// </summary>
        /// <returns>带 port 命名组的编译期正则表达式。</returns>
        [GeneratedRegex(
            "\\((?<port>COM[1-9][0-9]*)\\)\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex PortNameWithinFriendlyNameRegex();

        /// <summary>
        /// 保存合并过程中允许 PnP 查询补充名称的可变端口记录。
        /// </summary>
        private sealed class MutablePortRecord
        {
            /// <summary>
            /// 初始化一个待合并端口记录。
            /// </summary>
            /// <param name="portName">规范 COM 端口号。</param>
            /// <param name="friendlyName">当前可用友好名称。</param>
            /// <param name="pnpDeviceId">当前可用 PnP 标识。</param>
            internal MutablePortRecord(
                string portName,
                string friendlyName,
                string? pnpDeviceId)
            {
                PortName = portName;
                FriendlyName = friendlyName;
                PnpDeviceId = pnpDeviceId;
            }

            /// <summary>
            /// 获取规范 COM 端口号。
            /// </summary>
            internal string PortName { get; }

            /// <summary>
            /// 获取或设置 PnP 查询补充后的友好名称。
            /// </summary>
            internal string FriendlyName { get; set; }

            /// <summary>
            /// 获取或设置 PnP 查询补充后的设备标识。
            /// </summary>
            internal string? PnpDeviceId { get; set; }
        }
    }
}
