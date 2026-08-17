namespace CH32UpperComputer.Core.Iap
{
    /// <summary>
    /// 保存已经校验并固定下来的固件文件快照。
    /// </summary>
    public sealed class FirmwareImage
    {
        /// <summary>
        /// 固件原始字节的私有快照。
        /// </summary>
        private readonly byte[] content;

        /// <summary>
        /// 初始化一份不可随磁盘文件变化的固件快照。
        /// </summary>
        /// <param name="fullPath">用户选择时的规范化完整路径。</param>
        /// <param name="content">长度已经通过校验的固件原始字节。</param>
        public FirmwareImage(
            string fullPath,
            ReadOnlySpan<byte> content)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                throw new ArgumentException("固件路径不能为空。", nameof(fullPath));
            }

            if (content.Length is < 1 or > IapFrameCodec.MaximumImageLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(content),
                    $"固件长度必须位于 1 至 {IapFrameCodec.MaximumImageLength} 字节。");
            }

            FullPath = Path.GetFullPath(fullPath);
            FileName = Path.GetFileName(FullPath);
            this.content = content.ToArray();
            Crc32 = IapCrc32.Compute(this.content);
        }

        /// <summary>
        /// 获取选择时的规范化完整路径。
        /// </summary>
        public string FullPath { get; }

        /// <summary>
        /// 获取不含目录的固件文件名。
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// 获取固件长度。
        /// </summary>
        public int Length => content.Length;

        /// <summary>
        /// 获取完整固件 IEEE CRC32。
        /// </summary>
        public uint Crc32 { get; }

        /// <summary>
        /// 获取只读固件字节快照。
        /// </summary>
        public ReadOnlyMemory<byte> Content => content;
    }
}
