using CH32UpperComputer.Core.Iap;

namespace CH32UpperComputer.Infrastructure.Iap
{
    /// <summary>
    /// 在后台读取并校验原始 .bin 固件。
    /// </summary>
    public sealed class FirmwareFileService
    {
        /// <summary>
        /// 读取合法 .bin 并创建不会随磁盘变化的固件快照。
        /// </summary>
        /// <param name="filePath">用户选择的固件路径。</param>
        /// <param name="cancellationToken">取消文件读取。</param>
        /// <returns>包含字节、长度和整体 CRC32 的固件快照。</returns>
        /// <exception cref="InvalidDataException">扩展名、文件状态或长度不合法时抛出。</exception>
        public async Task<FirmwareImage> LoadAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new InvalidDataException("请选择 .bin 固件文件。");
            }

            string fullPath = Path.GetFullPath(filePath.Trim());

            if (!string.Equals(
                Path.GetExtension(fullPath),
                ".bin",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("固件文件扩展名必须为 .bin。");
            }

            FileInfo fileInfo = new(fullPath);

            if (!fileInfo.Exists)
            {
                throw new InvalidDataException("所选固件文件不存在。");
            }

            if (fileInfo.Length is < 1 or > IapFrameCodec.MaximumImageLength)
            {
                throw new InvalidDataException(
                    $"固件长度必须位于 1 至 {IapFrameCodec.MaximumImageLength} 字节。");
            }

            byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);

            if (bytes.LongLength != fileInfo.Length ||
                bytes.Length is < 1 or > IapFrameCodec.MaximumImageLength)
            {
                throw new InvalidDataException("固件在读取期间发生变化，请重新选择。");
            }

            return new FirmwareImage(fullPath, bytes);
        }
    }
}
