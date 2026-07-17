using CH32UpperComputer.Infrastructure.Settings;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 提供不访问网络、不收集隐私的本地应用、运行环境和协议支持信息。
    /// </summary>
    public sealed class SystemInfoViewModel
    {
        /// <summary>
        /// 初始化系统信息页的固定本地字段。
        /// </summary>
        /// <param name="settingsStore">用于显示配置文件绝对路径的设置存储。</param>
        /// <param name="logExportDirectory">当前日志导出目录。</param>
        public SystemInfoViewModel(
            JsonSettingsStore settingsStore,
            string logExportDirectory)
        {
            ArgumentNullException.ThrowIfNull(settingsStore);
            Assembly assembly = typeof(SystemInfoViewModel).Assembly;
            AppVersion = assembly.GetName().Version?.ToString() ?? "1.0.0";
            DotNetVersion = RuntimeInformation.FrameworkDescription;
            OperatingSystem = RuntimeInformation.OSDescription;
            SettingsFilePath = Path.GetFullPath(settingsStore.FilePath);
            LogExportDirectory = string.IsNullOrWhiteSpace(logExportDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetFullPath(logExportDirectory);
        }

        /// <summary>
        /// 获取应用程序集版本。
        /// </summary>
        public string AppVersion { get; }

        /// <summary>
        /// 获取当前 .NET 运行环境说明。
        /// </summary>
        public string DotNetVersion { get; }

        /// <summary>
        /// 获取当前 Windows 操作系统说明。
        /// </summary>
        public string OperatingSystem { get; }

        /// <summary>
        /// 获取 UTF-8 JSON 设置文件绝对路径。
        /// </summary>
        public string SettingsFilePath { get; }

        /// <summary>
        /// 获取当前日志导出目录绝对路径。
        /// </summary>
        public string LogExportDirectory { get; }

        /// <summary>
        /// 获取本期明确支持的 Modbus RTU 功能范围。
        /// </summary>
        public string ProtocolSupport => "Modbus-RTU：0x03、0x06、0x10、固定 0xFE 查询";

        /// <summary>
        /// 获取当前寄存器点表版本说明。
        /// </summary>
        public string RegisterMapVersion => "CH32 多传感器寄存器表 40001～40036 / v1";

        /// <summary>
        /// 获取应用不替代安全认证和计量标定的免责声明。
        /// </summary>
        public string Disclaimer =>
            "本工具用于设备调试与状态监控；MQ2 数值不是标定 ppm，报警与控制结果不得替代法定计量、消防或人身安全系统。";
    }
}
