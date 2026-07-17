using System.IO.Ports;
using System.Text;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Settings
{
    /// <summary>
    /// 验证设置文件缺失、合法往返、损坏隔离、安全标志归零和 UTF-8 原子保存。
    /// </summary>
    [TestFixture]
    public sealed class JsonSettingsStoreTests
    {
        /// <summary>
        /// 验证设置文件不存在时返回安全默认值，且加载操作不会隐式创建文件。
        /// </summary>
        [Test]
        public async Task LoadAsync_MissingFile_ReturnsManualSafeDefaultsWithoutCreatingFile()
        {
            string directory = CreateTemporaryDirectory();

            try
            {
                string filePath = Path.Combine(directory, "settings.json");
                JsonSettingsStore store = new(filePath);

                AppSettings loaded = await store.LoadAsync();

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(loaded.PortName, Is.EqualTo("COM1"));
                    Assert.That(loaded.BaudRate, Is.EqualTo(9600));
                    Assert.That(loaded.DataBits, Is.EqualTo(8));
                    Assert.That(loaded.Parity, Is.EqualTo(Parity.None));
                    Assert.That(loaded.StopBits, Is.EqualTo(StopBits.One));
                    Assert.That(loaded.SlaveAddress, Is.EqualTo(1));
                    Assert.That(loaded.AutomaticSendOnConnect, Is.False);
                    Assert.That(loaded.PeriodicSendEnabled, Is.False);
                    Assert.That(File.Exists(filePath), Is.False);
                }));
            }
            finally
            {
                DeleteTemporaryDirectory(directory);
            }
        }

        /// <summary>
        /// 验证合法串口与中文偏好往返，保存文件带 BOM，自动发送和定时启用状态始终强制关闭。
        /// </summary>
        [Test]
        public async Task SaveAndLoadAsync_ValidSettings_RoundTripsUtf8AndForcesAutomaticFlagsOff()
        {
            string directory = CreateTemporaryDirectory();

            try
            {
                string filePath = Path.Combine(directory, "settings.json");
                JsonSettingsStore store = new(filePath);
                AppSettings settings = new()
                {
                    PortName = "COM17",
                    BaudRate = 57600,
                    DataBits = 8,
                    Parity = Parity.Even,
                    StopBits = StopBits.One,
                    SlaveAddress = 64,
                    ResponseTimeoutMilliseconds = 2500,
                    RawInterByteTimeoutMilliseconds = 35,
                    PeriodicIntervalMilliseconds = 5000,
                    AutoAppendCrc = false,
                    LastCommand = "01 03 00 02 00 08",
                    LogExportDirectory = Path.Combine(directory, "中文日志"),
                    AutomaticSendOnConnect = true,
                    PeriodicSendEnabled = true,
                };

                await store.SaveAsync(settings);
                AppSettings loaded = await store.LoadAsync();
                byte[] bytes = await File.ReadAllBytesAsync(filePath);
                string json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                string[] temporaryFiles = Directory.GetFiles(directory, "*.tmp");

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
                    Assert.That(json, Does.Contain("中文日志"));
                    Assert.That(loaded.PortName, Is.EqualTo("COM17"));
                    Assert.That(loaded.BaudRate, Is.EqualTo(57600));
                    Assert.That(loaded.Parity, Is.EqualTo(Parity.Even));
                    Assert.That(loaded.SlaveAddress, Is.EqualTo(64));
                    Assert.That(loaded.ResponseTimeoutMilliseconds, Is.EqualTo(2500));
                    Assert.That(loaded.RawInterByteTimeoutMilliseconds, Is.EqualTo(35));
                    Assert.That(loaded.PeriodicIntervalMilliseconds, Is.EqualTo(5000));
                    Assert.That(loaded.AutoAppendCrc, Is.False);
                    Assert.That(loaded.AutomaticSendOnConnect, Is.False);
                    Assert.That(loaded.PeriodicSendEnabled, Is.False);
                    Assert.That(temporaryFiles, Is.Empty);
                }));
            }
            finally
            {
                DeleteTemporaryDirectory(directory);
            }
        }

        /// <summary>
        /// 验证损坏 JSON 被保留为带确定时间戳的 .corrupt 证据，并安全恢复默认值。
        /// </summary>
        [Test]
        public async Task LoadAsync_CorruptJson_QuarantinesOriginalAndReturnsDefaults()
        {
            string directory = CreateTemporaryDirectory();

            try
            {
                string filePath = Path.Combine(directory, "settings.json");
                string corruptContent = "{ 无效中文 JSON";
                await File.WriteAllTextAsync(filePath, corruptContent, new UTF8Encoding(true));
                ManualTimeProvider timeProvider = new(
                    new DateTimeOffset(2026, 7, 17, 3, 4, 5, 678, TimeSpan.Zero));
                JsonSettingsStore store = new(filePath, timeProvider);

                AppSettings loaded = await store.LoadAsync();
                string[] corruptFiles = Directory.GetFiles(
                    directory,
                    "settings.json.20260717T030405678Z.corrupt*");
                string preserved = await File.ReadAllTextAsync(corruptFiles.Single(), Encoding.UTF8);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(loaded.BaudRate, Is.EqualTo(9600));
                    Assert.That(loaded.AutomaticSendOnConnect, Is.False);
                    Assert.That(loaded.PeriodicSendEnabled, Is.False);
                    Assert.That(File.Exists(filePath), Is.False);
                    Assert.That(corruptFiles, Has.Length.EqualTo(1));
                    Assert.That(preserved, Is.EqualTo(corruptContent));
                }));
            }
            finally
            {
                DeleteTemporaryDirectory(directory);
            }
        }

        /// <summary>
        /// 在测试工作目录下创建唯一临时目录。
        /// </summary>
        /// <returns>已经创建的绝对目录路径。</returns>
        private static string CreateTemporaryDirectory()
        {
            string directory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"settings-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return directory;
        }

        /// <summary>
        /// 删除由当前测试创建的临时目录和全部文件。
        /// </summary>
        /// <param name="directory">由 <see cref="CreateTemporaryDirectory"/> 创建的绝对路径。</param>
        private static void DeleteTemporaryDirectory(string directory)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
