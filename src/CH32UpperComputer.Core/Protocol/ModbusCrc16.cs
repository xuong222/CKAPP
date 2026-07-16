namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 提供 Modbus RTU 帧所使用的 CRC16 计算、追加与校验功能。
    /// </summary>
    public static class ModbusCrc16
    {
        /// <summary>
        /// Modbus CRC16 算法规定的初始寄存器值。
        /// </summary>
        private const ushort InitialValue = 0xFFFF;

        /// <summary>
        /// Modbus CRC16 逐位右移算法使用的反向多项式。
        /// </summary>
        private const ushort Polynomial = 0xA001;

        /// <summary>
        /// 计算指定数据的 Modbus CRC16 值。
        /// </summary>
        /// <param name="data">需要参与 CRC16 计算的连续字节数据。</param>
        /// <returns>计算得到的 16 位 CRC 值；在线路帧中应先发送其低字节。</returns>
        public static ushort Compute(ReadOnlySpan<byte> data)
        {
            ushort crc = InitialValue;

            foreach (byte value in data)
            {
                crc ^= value;

                for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    bool isLeastSignificantBitSet = (crc & 0x0001) != 0;
                    crc >>= 1;

                    if (isLeastSignificantBitSet)
                    {
                        crc ^= Polynomial;
                    }
                }
            }

            return crc;
        }

        /// <summary>
        /// 创建数据副本，并按低字节、高字节的 Modbus RTU 线序追加 CRC16。
        /// </summary>
        /// <param name="data">需要复制并追加 CRC16 的报文字节；本方法不会修改该输入。</param>
        /// <returns>包含原始数据和两个尾随 CRC16 字节的新数组。</returns>
        public static byte[] Append(ReadOnlySpan<byte> data)
        {
            ushort crc = Compute(data);
            byte[] frame = new byte[data.Length + 2];

            data.CopyTo(frame);
            frame[^2] = (byte)(crc & 0x00FF);
            frame[^1] = (byte)(crc >> 8);

            return frame;
        }

        /// <summary>
        /// 判断完整帧的最后两个字节是否为其前置数据的有效 Modbus CRC16。
        /// </summary>
        /// <param name="frame">待校验的完整 Modbus RTU 帧，CRC16 必须以低字节在前的顺序位于末尾。</param>
        /// <returns>帧至少含一个数据字节且尾随 CRC16 正确时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
        public static bool IsValid(ReadOnlySpan<byte> frame)
        {
            const int checksumLength = 2;

            if (frame.Length <= checksumLength)
            {
                return false;
            }

            ReadOnlySpan<byte> body = frame[..^checksumLength];
            ushort expectedCrc = Compute(body);
            byte expectedLowByte = (byte)(expectedCrc & 0x00FF);
            byte expectedHighByte = (byte)(expectedCrc >> 8);

            return frame[^2] == expectedLowByte && frame[^1] == expectedHighByte;
        }
    }
}
