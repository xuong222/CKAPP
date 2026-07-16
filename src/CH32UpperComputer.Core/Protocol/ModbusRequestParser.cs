namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 表示手工输入帧的标准 Modbus 请求识别状态。
    /// </summary>
    public enum ModbusRequestParseStatus
    {
        /// <summary>
        /// 输入帧严格匹配一项受支持的标准请求。
        /// </summary>
        Succeeded,

        /// <summary>
        /// 输入帧不是受支持标准请求，应由上层保留给原始调试模式。
        /// </summary>
        NotSupported,
    }

    /// <summary>
    /// 表示一次非抛出标准 Modbus 请求识别的不可变结果。
    /// </summary>
    public sealed class ModbusRequestParseResult
    {
        /// <summary>
        /// 初始化一项标准请求识别结果。
        /// </summary>
        /// <param name="status">识别状态。</param>
        /// <param name="request">成功时恢复的不可变标准请求；失败时为 <see langword="null"/>。</param>
        /// <param name="errorMessage">失败原因；成功时为 <see langword="null"/>。</param>
        private ModbusRequestParseResult(
            ModbusRequestParseStatus status,
            ModbusRequest? request,
            string? errorMessage)
        {
            Status = status;
            Request = request;
            ErrorMessage = errorMessage;
        }

        /// <summary>
        /// 获取标准请求识别状态。
        /// </summary>
        public ModbusRequestParseStatus Status { get; }

        /// <summary>
        /// 获取成功恢复的不可变请求；未识别时为 <see langword="null"/>。
        /// </summary>
        public ModbusRequest? Request { get; }

        /// <summary>
        /// 获取未识别原因；成功时为 <see langword="null"/>。
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// 使用已经过完整结构验证的请求创建成功识别结果。
        /// </summary>
        /// <param name="request">成功恢复的标准请求。</param>
        /// <returns>包含指定请求的成功结果。</returns>
        internal static ModbusRequestParseResult CreateSuccess(ModbusRequest request)
        {
            return new ModbusRequestParseResult(
                ModbusRequestParseStatus.Succeeded,
                request,
                null);
        }

        /// <summary>
        /// 创建应保留给原始调试模式的未识别结果。
        /// </summary>
        /// <param name="errorMessage">说明帧未通过哪一项标准识别规则的清晰原因。</param>
        /// <returns>不包含标准请求的未识别结果。</returns>
        internal static ModbusRequestParseResult CreateNotSupported(string errorMessage)
        {
            return new ModbusRequestParseResult(
                ModbusRequestParseStatus.NotSupported,
                null,
                errorMessage);
        }
    }

    /// <summary>
    /// 提供完整手工输入帧的严格标准请求识别，并把非标准帧无异常地保留给原始调试模式。
    /// </summary>
    public static class ModbusRequestParser
    {
        /// <summary>
        /// Modbus RTU 标准应用数据单元允许的最大字节数。
        /// </summary>
        private const int MaximumAduLength = 256;

        /// <summary>
        /// 固定字段请求及其尾随 CRC16 所需的精确字节数。
        /// </summary>
        private const int FixedRequestLength = 8;

        /// <summary>
        /// 尝试把完整 CRC 有效帧识别为受支持的 0x03、0x06、0x10 或固定 0xFE 请求。
        /// </summary>
        /// <param name="frame">最终将在线路发送的完整帧，必须已经包含低字节在前的 CRC16。</param>
        /// <returns>严格识别成功时包含不可变请求；否则返回非抛出未识别原因，供上层选择 RawDebug。</returns>
        public static ModbusRequestParseResult TryParseSupported(ReadOnlySpan<byte> frame)
        {
            if (frame.Length > MaximumAduLength)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    $"标准 Modbus RTU 帧不得超过 {MaximumAduLength} 字节。");
            }

            if (frame.Length < FixedRequestLength)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    "输入帧过短，无法构成受支持的完整标准请求。");
            }

            if (!ModbusCrc16.IsValid(frame))
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    "输入帧的 Modbus CRC16 校验失败。");
            }

            if (frame[0] == 0xFE)
            {
                return ParseUnknownAddressQuery(frame);
            }

            try
            {
                return frame[1] switch
                {
                    (byte)ModbusFunctionCode.ReadHoldingRegisters => ParseReadRequest(frame),
                    (byte)ModbusFunctionCode.WriteSingleRegister => ParseWriteSingleRequest(frame),
                    (byte)ModbusFunctionCode.WriteMultipleRegisters => ParseWriteMultipleRequest(frame),
                    _ => ModbusRequestParseResult.CreateNotSupported(
                        $"功能码 0x{frame[1]:X2} 不属于可识别的标准请求。"),
                };
            }
            catch (ArgumentException exception)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    $"请求字段不符合标准约束：{exception.Message}");
            }
        }

        /// <summary>
        /// 通过固定查询工厂重建并逐字节比对 0xFE 请求。
        /// </summary>
        /// <param name="frame">地址首字节已经确认为 0xFE 的 CRC 有效完整帧。</param>
        /// <returns>仅精确匹配固件固定查询时成功的识别结果。</returns>
        private static ModbusRequestParseResult ParseUnknownAddressQuery(ReadOnlySpan<byte> frame)
        {
            ModbusRequest request = ModbusRequestFactory.CreateUnknownAddressQuery();

            return CreateResultFromExactFactoryFrame(
                frame,
                request,
                "0xFE 只支持固定查询 FE 03 00 00 00 01 + CRC。");
        }

        /// <summary>
        /// 解析固定八字节 0x03 请求并使用工厂执行协议范围校验。
        /// </summary>
        /// <param name="frame">功能码已经确认为 0x03 的 CRC 有效完整帧。</param>
        /// <returns>帧精确等于工厂规范编码时的成功结果，否则为未识别结果。</returns>
        private static ModbusRequestParseResult ParseReadRequest(ReadOnlySpan<byte> frame)
        {
            if (frame.Length != FixedRequestLength)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    "0x03 标准请求必须恰好为 8 字节。");
            }

            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                frame[0],
                ReadBigEndianUInt16(frame, 2),
                ReadBigEndianUInt16(frame, 4));

            return CreateResultFromExactFactoryFrame(
                frame,
                request,
                "0x03 请求字段不是规范编码。");
        }

        /// <summary>
        /// 解析固定八字节 0x06 请求并使用工厂构建精确回显签名。
        /// </summary>
        /// <param name="frame">功能码已经确认为 0x06 的 CRC 有效完整帧。</param>
        /// <returns>帧精确等于工厂规范编码时的成功结果，否则为未识别结果。</returns>
        private static ModbusRequestParseResult ParseWriteSingleRequest(ReadOnlySpan<byte> frame)
        {
            if (frame.Length != FixedRequestLength)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    "0x06 标准请求必须恰好为 8 字节。");
            }

            ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                frame[0],
                ReadBigEndianUInt16(frame, 2),
                ReadBigEndianUInt16(frame, 4));

            return CreateResultFromExactFactoryFrame(
                frame,
                request,
                "0x06 请求字段不是规范编码。");
        }

        /// <summary>
        /// 解析变长 0x10 请求，并使用工厂重建数量、字节数和大端值编码。
        /// </summary>
        /// <param name="frame">功能码已经确认为 0x10 的 CRC 有效完整帧。</param>
        /// <returns>数量、字节数、数据和完整帧均规范时的成功结果，否则为未识别结果。</returns>
        private static ModbusRequestParseResult ParseWriteMultipleRequest(ReadOnlySpan<byte> frame)
        {
            const int fixedBodyLength = 7;
            const int checksumLength = 2;
            const int minimumRegisterByteCount = 2;

            if (frame.Length < fixedBodyLength + minimumRegisterByteCount + checksumLength)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    "0x10 标准请求必须至少包含一个完整寄存器值。");
            }

            int byteCount = frame[6];
            int expectedFrameLength = fixedBodyLength + byteCount + checksumLength;

            if (byteCount < minimumRegisterByteCount || (byteCount & 1) != 0)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    "0x10 字节数必须是至少为 2 的偶数。");
            }

            if (frame.Length != expectedFrameLength)
            {
                return ModbusRequestParseResult.CreateNotSupported(
                    "0x10 字节数字段与完整帧长度不一致。");
            }

            ushort[] values = new ushort[byteCount / 2];

            for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                values[valueIndex] = ReadBigEndianUInt16(frame, 7 + (valueIndex * 2));
            }

            ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                frame[0],
                ReadBigEndianUInt16(frame, 2),
                values);

            return CreateResultFromExactFactoryFrame(
                frame,
                request,
                "0x10 数量、字节数或寄存器数据结构不一致。");
        }

        /// <summary>
        /// 要求输入帧逐字节等于统一工厂生成的规范帧，避免解析器维护第二套宽松规则。
        /// </summary>
        /// <param name="frame">待比对的调用方完整输入帧。</param>
        /// <param name="request">使用统一工厂从输入字段重建的标准请求。</param>
        /// <param name="mismatchMessage">逐字节不一致时返回的明确原因。</param>
        /// <returns>完整帧一致时返回成功结果，否则返回未识别结果。</returns>
        private static ModbusRequestParseResult CreateResultFromExactFactoryFrame(
            ReadOnlySpan<byte> frame,
            ModbusRequest request,
            string mismatchMessage)
        {
            if (!frame.SequenceEqual(request.RawFrame.Span))
            {
                return ModbusRequestParseResult.CreateNotSupported(mismatchMessage);
            }

            return ModbusRequestParseResult.CreateSuccess(request);
        }

        /// <summary>
        /// 从 Modbus 数据域按大端顺序读取一个 16 位无符号值。
        /// </summary>
        /// <param name="source">包含待读取字段的完整帧。</param>
        /// <param name="offset">字段高字节在帧中的零基位置。</param>
        /// <returns>解码得到的 16 位无符号值。</returns>
        private static ushort ReadBigEndianUInt16(ReadOnlySpan<byte> source, int offset)
        {
            return (ushort)((source[offset] << 8) | source[offset + 1]);
        }
    }
}
