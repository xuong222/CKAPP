using System.Buffers.Binary;

namespace CH32UpperComputer.Core.Iap
{
    /// <summary>
    /// 按固定小端协议编码请求并解析响应。
    /// </summary>
    public static class IapFrameCodec
    {
        /// <summary>
        /// 所有请求和响应共同使用的魔数。
        /// </summary>
        public const uint Magic = 0x31495041U;

        /// <summary>
        /// 固定请求头长度。
        /// </summary>
        public const int RequestHeaderSize = 32;

        /// <summary>
        /// 固定响应头长度。
        /// </summary>
        public const int ResponseHeaderSize = 24;

        /// <summary>
        /// HELLO ACK 固定附加信息长度。
        /// </summary>
        public const int HelloInfoSize = 24;

        /// <summary>
        /// 当前 Bootloader 支持的最大 DATA 载荷。
        /// </summary>
        public const int MaximumDataLength = 1024;

        /// <summary>
        /// 上位机允许的最大镜像长度。
        /// </summary>
        public const int MaximumImageLength = 204800;

        /// <summary>
        /// 将请求头和可选 DATA 载荷编码为完整网络帧。
        /// </summary>
        /// <param name="header">包含命令、序号、偏移、长度和 CRC32 的请求头。</param>
        /// <param name="payload">仅 DATA 命令允许携带的载荷。</param>
        /// <returns>可一次写入 NetworkStream 的完整帧。</returns>
        /// <exception cref="InvalidDataException">命令字段形状、载荷长度或范围不合法时抛出。</exception>
        public static byte[] EncodeRequest(
            IapRequestHeader header,
            ReadOnlySpan<byte> payload)
        {
            ArgumentNullException.ThrowIfNull(header);
            ValidateRequestShape(header, payload.Length);
            byte[] frame = new byte[checked(RequestHeaderSize + payload.Length)];
            Span<byte> span = frame;
            BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], RequestHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], (uint)header.Command);
            BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], header.Sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], header.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..24], header.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], header.PayloadCrc32);
            BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], header.ImageCrc32);
            payload.CopyTo(span[RequestHeaderSize..]);
            return frame;
        }

        /// <summary>
        /// 解析并校验固定 24 字节响应头的基本布局。
        /// </summary>
        /// <param name="bytes">恰好一个响应头的原始字节。</param>
        /// <returns>已经验证 magic、长度、保留字段和状态值的响应头。</returns>
        /// <exception cref="InvalidDataException">响应头长度或固定字段不合法时抛出。</exception>
        public static IapResponseHeader DecodeResponseHeader(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != ResponseHeaderSize)
            {
                throw new InvalidDataException(
                    $"IAP 响应头必须为 {ResponseHeaderSize} 字节，实际为 {bytes.Length} 字节。");
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0..4]);
            ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]);
            ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
            uint statusValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);

            if (magic != Magic)
            {
                throw new InvalidDataException($"IAP 响应 magic 错误：0x{magic:X8}。");
            }

            if (headerSize != ResponseHeaderSize || reserved != 0U)
            {
                throw new InvalidDataException(
                    $"IAP 响应头字段错误：header_size={headerSize}，reserved={reserved}。");
            }

            if (statusValue > (uint)IapResponseStatus.Nack)
            {
                throw new InvalidDataException($"IAP 响应 status 非法：{statusValue}。");
            }

            return new IapResponseHeader(
                (IapCommand)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]),
                (IapResponseStatus)statusValue,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..24]));
        }

        /// <summary>
        /// 解析 HELLO ACK 后固定 24 字节设备信息。
        /// </summary>
        /// <param name="bytes">恰好一个 HELLO 信息块的原始字节。</param>
        /// <returns>Bootloader 版本和当前 App 元数据。</returns>
        /// <exception cref="InvalidDataException">长度或有效标志不合法时抛出。</exception>
        public static IapHelloInfo DecodeHelloInfo(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != HelloInfoSize)
            {
                throw new InvalidDataException(
                    $"HELLO 信息必须为 {HelloInfoSize} 字节，实际为 {bytes.Length} 字节。");
            }

            uint appValid = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);

            if (appValid > 1U)
            {
                throw new InvalidDataException($"HELLO app_valid 非法：{appValid}。");
            }

            return new IapHelloInfo(
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[0..4]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]),
                appValid == 1U,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..24]));
        }

        /// <summary>
        /// 校验命令相关字段和载荷长度，防止构造越界或含糊帧。
        /// </summary>
        /// <param name="header">需要校验的请求头。</param>
        /// <param name="payloadLength">实际跟随请求头的载荷字节数。</param>
        /// <exception cref="InvalidDataException">命令形状不符合协议时抛出。</exception>
        private static void ValidateRequestShape(
            IapRequestHeader header,
            int payloadLength)
        {
            if (header.Command is not (
                IapCommand.Hello or
                IapCommand.Begin or
                IapCommand.Data or
                IapCommand.End or
                IapCommand.Run or
                IapCommand.Abort))
            {
                throw new InvalidDataException(
                    $"不支持的 IAP 请求命令：0x{(uint)header.Command:X8}。");
            }

            if (payloadLength < 0 || payloadLength > MaximumDataLength)
            {
                throw new InvalidDataException($"IAP DATA 载荷不能超过 {MaximumDataLength} 字节。");
            }

            if (header.Command == IapCommand.Data)
            {
                if (payloadLength == 0 || header.Length != (uint)payloadLength)
                {
                    throw new InvalidDataException("DATA length 必须与非空实际载荷完全一致。");
                }

                if (header.ImageCrc32 != 0U ||
                    header.Offset > MaximumImageLength ||
                    header.Length > MaximumImageLength - header.Offset)
                {
                    throw new InvalidDataException("DATA 偏移、长度或整体 CRC 字段不合法。");
                }

                return;
            }

            if (payloadLength != 0)
            {
                throw new InvalidDataException("只有 DATA 命令允许携带载荷。");
            }

            if (header.Command == IapCommand.Begin)
            {
                if (header.Offset != 0U ||
                    header.Length is 0U or > MaximumImageLength ||
                    header.PayloadCrc32 != 0U)
                {
                    throw new InvalidDataException("BEGIN 的偏移、长度或分片 CRC 字段不合法。");
                }

                return;
            }

            if (header.Offset != 0U ||
                header.Length != 0U ||
                header.PayloadCrc32 != 0U ||
                header.ImageCrc32 != 0U)
            {
                throw new InvalidDataException("HELLO、END、RUN 和 ABORT 的可变字段必须为零。");
            }
        }
    }
}
