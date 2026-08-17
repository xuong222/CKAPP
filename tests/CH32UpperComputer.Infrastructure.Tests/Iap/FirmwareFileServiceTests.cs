using System.IO;

using CH32UpperComputer.Infrastructure.Iap;

namespace CH32UpperComputer.Infrastructure.Tests.Iap
{
    /// <summary>
    /// 验证 .bin 文件类型、边界长度和不可变读取快照。
    /// </summary>
    [TestFixture]
    public sealed class FirmwareFileServiceTests
    {
        /// <summary>
        /// 验证一字节、1024、1025 和最大长度固件均可读取并计算 CRC32。
        /// </summary>
        /// <param name="length">合法文件长度。</param>
        [TestCase(1)]
        [TestCase(1024)]
        [TestCase(1025)]
        [TestCase(204800)]
        public async Task LoadAsync_ValidBoundaryLength_ReturnsImmutableSnapshot(int length)
        {
            string path = CreateTemporaryPath(".BiN");

            try
            {
                byte[] bytes = Enumerable.Range(0, length)
                    .Select(index => checked((byte)(index % 251)))
                    .ToArray();
                await File.WriteAllBytesAsync(path, bytes);
                FirmwareFileService service = new();

                Core.Iap.FirmwareImage image = await service.LoadAsync(
                    path,
                    CancellationToken.None);
                bytes[0] ^= 0xFF;

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(image.Length, Is.EqualTo(length));
                    Assert.That(image.Content.Span[0], Is.Not.EqualTo(bytes[0]));
                    Assert.That(image.FileName, Is.EqualTo(Path.GetFileName(path)));
                }));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 验证空文件和超出 App 区上限的文件均被拒绝。
        /// </summary>
        /// <param name="length">非法文件长度。</param>
        [TestCase(0)]
        [TestCase(204801)]
        public async Task LoadAsync_InvalidBoundaryLength_Throws(int length)
        {
            string path = CreateTemporaryPath(".bin");

            try
            {
                await File.WriteAllBytesAsync(path, new byte[length]);
                FirmwareFileService service = new();
                InvalidDataException? exception = null;

                try
                {
                    _ = await service.LoadAsync(path, CancellationToken.None);
                }
                catch (InvalidDataException captured)
                {
                    exception = captured;
                }

                Assert.That(exception, Is.Not.Null);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 验证非 bin 扩展名即使内容合法也不得读取。
        /// </summary>
        [Test]
        public async Task LoadAsync_NonBinExtension_Throws()
        {
            string path = CreateTemporaryPath(".hex");

            try
            {
                await File.WriteAllBytesAsync(path, new byte[] { 1 });
                FirmwareFileService service = new();
                InvalidDataException? exception = null;

                try
                {
                    _ = await service.LoadAsync(path, CancellationToken.None);
                }
                catch (InvalidDataException captured)
                {
                    exception = captured;
                }

                Assert.That(exception, Is.Not.Null);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 创建位于测试工作目录的唯一临时文件路径。
        /// </summary>
        /// <param name="extension">包含点号的目标扩展名。</param>
        /// <returns>尚未创建的完整文件路径。</returns>
        private static string CreateTemporaryPath(string extension)
        {
            return Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"firmware-{Guid.NewGuid():N}{extension}");
        }
    }
}
