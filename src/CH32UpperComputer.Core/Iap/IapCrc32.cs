namespace CH32UpperComputer.Core.Iap
{
    /// <summary>
    /// 提供与 Bootloader 一致的标准 IEEE CRC32 计算。
    /// </summary>
    public static class IapCrc32
    {
        /// <summary>
        /// 计算一段连续字节的 IEEE CRC32。
        /// </summary>
        /// <param name="data">需要参与计算的原始字节。</param>
        /// <returns>采用多项式 0xEDB88320、初值和结果异或均为 0xFFFFFFFF 的 CRC32。</returns>
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFU;

            foreach (byte value in data)
            {
                crc ^= value;

                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = unchecked((uint)-(int)(crc & 1U));
                    crc = (crc >> 1) ^ (0xEDB88320U & mask);
                }
            }

            return crc ^ 0xFFFFFFFFU;
        }
    }
}
