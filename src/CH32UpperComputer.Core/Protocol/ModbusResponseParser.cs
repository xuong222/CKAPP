namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 提供 Modbus RTU 响应候选帧的 CRC、完整结构和请求签名严格校验。
    /// </summary>
    public static class ModbusResponseParser
    {
        /// <summary>
        /// 标准 Modbus 异常响应的固定完整帧长度。
        /// </summary>
        private const int ExceptionResponseLength = 5;

        /// <summary>
        /// 0x06 和 0x10 正常回显响应的固定完整帧长度。
        /// </summary>
        private const int WriteResponseLength = 8;

        /// <summary>
        /// Modbus RTU 标准应用数据单元允许的最大字节数。
        /// </summary>
        private const int MaximumAduLength = 256;

        /// <summary>
        /// 当前设备协议允许使用的最小真实从站地址。
        /// </summary>
        private const byte MinimumSlaveAddress = 1;

        /// <summary>
        /// 当前设备协议允许使用的最大真实从站地址。
        /// </summary>
        private const byte MaximumSlaveAddress = 64;

        /// <summary>
        /// 解析一项完整响应候选，并严格匹配指定请求的地址、功能、长度、字节数或回显字段。
        /// </summary>
        /// <param name="request">已经由工厂或严格手输识别器创建的当前标准请求。</param>
        /// <param name="frame">包含低字节在前 CRC16 的完整响应候选帧。</param>
        /// <returns>分类为成功、标准 Modbus 异常或协议错误的不可变结果。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 <see langword="null"/> 时抛出。</exception>
        public static ModbusResponse Parse(
            ModbusRequest request,
            ReadOnlySpan<byte> frame)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (frame.Length < ExceptionResponseLength)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"Modbus RTU 响应至少需要 {ExceptionResponseLength} 字节。");
            }

            if (frame.Length > MaximumAduLength)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"Modbus RTU 响应不得超过 {MaximumAduLength} 字节。");
            }

            if (!ModbusCrc16.IsValid(frame))
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    "响应的 Modbus CRC16 校验失败。");
            }

            if (request.IsUnknownAddressQuery)
            {
                return ParseUnknownAddressResponse(request, frame);
            }

            if (frame[0] != request.SlaveAddress)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"响应地址 0x{frame[0]:X2} 与请求地址 0x{request.SlaveAddress:X2} 不一致。");
            }

            byte requestedFunction = (byte)request.FunctionCode;
            byte expectedExceptionFunction = (byte)(requestedFunction | 0x80);

            if (frame[1] == expectedExceptionFunction)
            {
                return ParseExceptionResponse(frame);
            }

            if (frame[1] != requestedFunction)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"响应功能码 0x{frame[1]:X2} 与请求功能码 0x{requestedFunction:X2} 不一致。");
            }

            return request.FunctionCode switch
            {
                ModbusFunctionCode.ReadHoldingRegisters => ParseReadResponse(request, frame),
                ModbusFunctionCode.WriteSingleRegister => ParseWriteSingleResponse(request, frame),
                ModbusFunctionCode.WriteMultipleRegisters => ParseWriteMultipleResponse(request, frame),
                _ => ModbusResponse.CreateProtocolError(
                    frame,
                    $"请求功能码 0x{requestedFunction:X2} 不受响应解析器支持。"),
            };
        }

        /// <summary>
        /// 验证固定五字节标准异常响应并保留设备异常码及中文含义。
        /// </summary>
        /// <param name="frame">地址和异常功能码已经匹配当前普通请求的 CRC 有效帧。</param>
        /// <returns>长度正确时为标准异常结果，否则为协议错误。</returns>
        private static ModbusResponse ParseExceptionResponse(ReadOnlySpan<byte> frame)
        {
            if (frame.Length != ExceptionResponseLength)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"标准 Modbus 异常响应必须恰好为 {ExceptionResponseLength} 字节。");
            }

            byte exceptionCode = frame[2];

            return ModbusResponse.CreateModbusException(
                frame[0],
                frame,
                exceptionCode,
                GetExceptionMeaning(exceptionCode));
        }

        /// <summary>
        /// 验证 0x03 响应的精确总长度、字节数和大端寄存器数据。
        /// </summary>
        /// <param name="request">当前 0x03 普通请求及其数量签名。</param>
        /// <param name="frame">地址、功能码和 CRC 已经匹配请求的响应帧。</param>
        /// <returns>结构完整时包含解码寄存器字的成功结果，否则为协议错误。</returns>
        private static ModbusResponse ParseReadResponse(
            ModbusRequest request,
            ReadOnlySpan<byte> frame)
        {
            int expectedByteCount = request.Quantity * 2;

            if (frame[2] != expectedByteCount)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"0x03 响应字节数 {frame[2]} 与请求数量要求的 {expectedByteCount} 不一致。");
            }

            if (frame.Length != request.ExpectedResponseLength)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"0x03 响应长度 {frame.Length} 与预期 {request.ExpectedResponseLength} 不一致。");
            }

            ushort[] registers = new ushort[request.Quantity];

            for (int registerIndex = 0; registerIndex < registers.Length; registerIndex++)
            {
                registers[registerIndex] = ReadBigEndianUInt16(
                    frame,
                    3 + (registerIndex * 2));
            }

            return ModbusResponse.CreateSuccess(
                request,
                frame[0],
                frame,
                registers);
        }

        /// <summary>
        /// 验证 0x06 响应是否为请求寄存器地址和值的精确八字节回显。
        /// </summary>
        /// <param name="request">当前 0x06 请求及其地址和值签名。</param>
        /// <param name="frame">地址、功能码和 CRC 已经匹配请求的响应帧。</param>
        /// <returns>完整回显一致时为成功结果，否则为协议错误。</returns>
        private static ModbusResponse ParseWriteSingleResponse(
            ModbusRequest request,
            ReadOnlySpan<byte> frame)
        {
            if (frame.Length != WriteResponseLength)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"0x06 正常响应必须恰好为 {WriteResponseLength} 字节。");
            }

            ushort echoedAddress = ReadBigEndianUInt16(frame, 2);
            ushort echoedValue = ReadBigEndianUInt16(frame, 4);
            ReadOnlyMemory<ushort> requestValues = request.Values;

            if (echoedAddress != request.StartAddress ||
                requestValues.Length != 1 ||
                echoedValue != requestValues.Span[0])
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    "0x06 响应没有完整回显请求的寄存器地址和值。");
            }

            return ModbusResponse.CreateSuccess(
                request,
                frame[0],
                frame,
                ReadOnlySpan<ushort>.Empty);
        }

        /// <summary>
        /// 验证 0x10 响应是否为请求起始地址和寄存器数量的精确八字节回显。
        /// </summary>
        /// <param name="request">当前 0x10 请求及其起始地址和数量签名。</param>
        /// <param name="frame">地址、功能码和 CRC 已经匹配请求的响应帧。</param>
        /// <returns>完整回显一致时为成功结果，否则为协议错误。</returns>
        private static ModbusResponse ParseWriteMultipleResponse(
            ModbusRequest request,
            ReadOnlySpan<byte> frame)
        {
            if (frame.Length != WriteResponseLength)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    $"0x10 正常响应必须恰好为 {WriteResponseLength} 字节。");
            }

            ushort echoedStartAddress = ReadBigEndianUInt16(frame, 2);
            ushort echoedQuantity = ReadBigEndianUInt16(frame, 4);

            if (echoedStartAddress != request.StartAddress ||
                echoedQuantity != request.Quantity)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    "0x10 响应没有完整回显请求的起始地址和寄存器数量。");
            }

            return ModbusResponse.CreateSuccess(
                request,
                frame[0],
                frame,
                ReadOnlySpan<ushort>.Empty);
        }

        /// <summary>
        /// 验证 0xFE 查询的专用响应地址、功能、单寄存器结构和地址值一致性。
        /// </summary>
        /// <param name="request">实际参与响应签名验证的固定 0xFE 查询请求对象。</param>
        /// <param name="frame">CRC 已经通过校验的未知地址查询响应候选。</param>
        /// <returns>只在真实地址 1 至 64 且寄存器值等于该地址时返回发现成功。</returns>
        private static ModbusResponse ParseUnknownAddressResponse(
            ModbusRequest request,
            ReadOnlySpan<byte> frame)
        {
            const int expectedResponseLength = 7;
            const int expectedByteCount = 2;

            if (frame[0] is < MinimumSlaveAddress or > MaximumSlaveAddress)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    "0xFE 查询响应必须使用 1 至 64 的真实从站地址，不能保存 0xFE。");
            }

            if (frame[1] != (byte)ModbusFunctionCode.ReadHoldingRegisters)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    "0xFE 查询响应功能码必须为 0x03。");
            }

            if (frame.Length != expectedResponseLength || frame[2] != expectedByteCount)
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    "0xFE 查询响应必须恰好返回一个寄存器。");
            }

            ushort returnedAddressValue = ReadBigEndianUInt16(frame, 3);

            if (returnedAddressValue != frame[0])
            {
                return ModbusResponse.CreateProtocolError(
                    frame,
                    "0xFE 查询响应的寄存器值必须与响应真实地址一致。");
            }

            Span<ushort> registers = stackalloc ushort[1];
            registers[0] = returnedAddressValue;

            return ModbusResponse.CreateSuccess(
                request,
                frame[0],
                frame,
                registers,
                frame[0]);
        }

        /// <summary>
        /// 返回标准或未知 Modbus 异常码的中文含义。
        /// </summary>
        /// <param name="exceptionCode">设备返回的单字节异常码。</param>
        /// <returns>已知异常码的标准中文含义，或包含原始十六进制码的未知异常说明。</returns>
        private static string GetExceptionMeaning(byte exceptionCode)
        {
            return exceptionCode switch
            {
                0x01 => "非法功能",
                0x02 => "非法数据地址",
                0x03 => "非法数据值",
                0x04 => "从站设备故障",
                _ => $"未知 Modbus 异常（0x{exceptionCode:X2}）",
            };
        }

        /// <summary>
        /// 从 Modbus 数据域按大端顺序读取一个 16 位无符号值。
        /// </summary>
        /// <param name="source">包含待读取字段的完整响应帧。</param>
        /// <param name="offset">字段高字节在响应帧中的零基位置。</param>
        /// <returns>解码得到的 16 位无符号值。</returns>
        private static ushort ReadBigEndianUInt16(ReadOnlySpan<byte> source, int offset)
        {
            return (ushort)((source[offset] << 8) | source[offset + 1]);
        }
    }
}
