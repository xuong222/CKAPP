using System.IO;
using CH32UpperComputer.Core.Protocol;

namespace CH32UpperComputer.Testing
{
    /// <summary>
    /// 指定模拟 Modbus 设备处理下一项请求时需要制造的响应行为。
    /// </summary>
    public enum SimulatedModbusBehavior
    {
        /// <summary>
        /// 返回结构和 CRC 均有效的正常响应。
        /// </summary>
        Normal,

        /// <summary>
        /// 通过注入时间源或延迟委托等待后再返回正常响应。
        /// </summary>
        Delayed,

        /// <summary>
        /// 返回内容正确但尾随 CRC 被破坏的响应。
        /// </summary>
        CrcError,

        /// <summary>
        /// 返回固定五字节标准 Modbus 异常响应。
        /// </summary>
        ModbusException,

        /// <summary>
        /// 将一帧拆成两个连续接收块。
        /// </summary>
        Segmented,

        /// <summary>
        /// 将两帧粘连在同一个接收块中。
        /// </summary>
        StickyFrames,

        /// <summary>
        /// 接收请求但不返回任何数据。
        /// </summary>
        NoResponse,

        /// <summary>
        /// 模拟远端串口断开并使读取传播 I/O 异常。
        /// </summary>
        Disconnect,
    }

    /// <summary>
    /// 使用现有 Core 协议组件构建可脚本化 Modbus RTU 响应的轻量测试设备。
    /// </summary>
    public sealed class SimulatedModbusDevice
    {
        /// <summary>
        /// 接收模拟线路数据的内存传输。
        /// </summary>
        private readonly FakeSerialTransport transport;

        /// <summary>
        /// 执行延迟行为的可注入委托，测试可由手动时间源确定性驱动。
        /// </summary>
        private readonly Func<TimeSpan, CancellationToken, ValueTask> delayAsync;

        /// <summary>
        /// 串行化请求处理，确保并发脚本不会交错应用寄存器和从站地址状态。
        /// </summary>
        private readonly SemaphoreSlim requestGate = new(1, 1);

        /// <summary>
        /// 保护寄存器、从站地址和可配置响应状态的同步门。
        /// </summary>
        private readonly object stateSyncRoot = new();

        /// <summary>
        /// 按零基协议地址保存的当前原始寄存器字。
        /// </summary>
        private readonly Dictionary<ushort, ushort> registers = [];

        /// <summary>
        /// 模拟设备普通 Modbus 从站地址。
        /// </summary>
        private byte slaveAddress = 1;

        /// <summary>
        /// 延迟脚本使用的受控等待时长。
        /// </summary>
        private TimeSpan responseDelay = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// 默认请求使用的模拟响应行为。
        /// </summary>
        private SimulatedModbusBehavior behavior = SimulatedModbusBehavior.Normal;

        /// <summary>
        /// 脚本化 Modbus 异常响应使用的异常码。
        /// </summary>
        private byte exceptionCode = 0x02;

        /// <summary>
        /// 初始化一个以指定模拟传输和时间源发送响应的设备。
        /// </summary>
        /// <param name="transport">已经由测试打开的目标模拟传输。</param>
        /// <param name="timeProvider">延迟脚本使用的时间源；传入 <see cref="ManualTimeProvider"/> 可消除真实等待。</param>
        public SimulatedModbusDevice(
            FakeSerialTransport transport,
            TimeProvider timeProvider)
            : this(
                transport,
                (delay, cancellationToken) =>
                    DelayWithTimeProviderAsync(delay, timeProvider, cancellationToken))
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
        }

        /// <summary>
        /// 初始化一个使用调用方确定性延迟策略发送响应的设备。
        /// </summary>
        /// <param name="transport">已经由测试打开的目标模拟传输。</param>
        /// <param name="delayAsync">接收延迟值和取消令牌且不进行真实休眠的等待策略。</param>
        public SimulatedModbusDevice(
            FakeSerialTransport transport,
            Func<TimeSpan, CancellationToken, ValueTask> delayAsync)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(delayAsync);
            this.transport = transport;
            this.delayAsync = delayAsync;

            lock (stateSyncRoot)
            {
                registers[0x0000] = slaveAddress;
            }
        }

        /// <summary>
        /// 获取或设置默认响应脚本。
        /// </summary>
        public SimulatedModbusBehavior Behavior
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return behavior;
                }
            }

            set
            {
                lock (stateSyncRoot)
                {
                    behavior = value;
                }
            }
        }

        /// <summary>
        /// 获取或设置异常响应使用的 Modbus 异常码。
        /// </summary>
        public byte ExceptionCode
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return exceptionCode;
                }
            }

            set
            {
                lock (stateSyncRoot)
                {
                    exceptionCode = value;
                }
            }
        }

        /// <summary>
        /// 获取或设置模拟设备的普通从站地址，允许 1 至 64。
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">设置值不在 1 至 64 时抛出。</exception>
        public byte SlaveAddress
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return slaveAddress;
                }
            }

            set
            {
                ValidateSlaveAddress(value, nameof(value));

                lock (stateSyncRoot)
                {
                    slaveAddress = value;
                    registers[0x0000] = value;
                }
            }
        }

        /// <summary>
        /// 获取或设置延迟脚本在发送响应前等待的非负时长。
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">设置负值时抛出。</exception>
        public TimeSpan ResponseDelay
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return responseDelay;
                }
            }

            set
            {
                if (value < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value,
                        "模拟响应延迟不能为负数。");
                }

                lock (stateSyncRoot)
                {
                    responseDelay = value;
                }
            }
        }

        /// <summary>
        /// 设置一个零基协议寄存器的原始 16 位字。
        /// </summary>
        /// <param name="address">待设置的零基协议地址。</param>
        /// <param name="value">后续 0x03 读取需要返回的原始字。</param>
        public void SetRegister(ushort address, ushort value)
        {
            if (address == 0x0000)
            {
                ValidateSlaveAddress(value, nameof(value));
            }

            lock (stateSyncRoot)
            {
                registers[address] = value;

                if (address == 0x0000)
                {
                    slaveAddress = checked((byte)value);
                }
            }
        }

        /// <summary>
        /// 获取一个零基协议寄存器的当前原始字；尚未设置的地址返回零。
        /// </summary>
        /// <param name="address">待读取的零基协议地址。</param>
        /// <returns>当前保存值或默认零。</returns>
        public ushort GetRegister(ushort address)
        {
            lock (stateSyncRoot)
            {
                return registers.GetValueOrDefault(address);
            }
        }

        /// <summary>
        /// 使用当前 <see cref="Behavior"/> 处理一项已经写入模拟线路的完整请求帧。
        /// </summary>
        /// <param name="requestFrame">包含有效 CRC 的受支持 0x03、0x06、0x10 或 0xFE 请求。</param>
        /// <param name="cancellationToken">取消延迟或响应注入的令牌。</param>
        /// <returns>响应行为完成后的任务。</returns>
        public ValueTask HandleWriteAsync(
            ReadOnlyMemory<byte> requestFrame,
            CancellationToken cancellationToken = default)
        {
            return HandleWriteAsync(requestFrame, Behavior, cancellationToken);
        }

        /// <summary>
        /// 使用明确脚本处理一项已经写入模拟线路的完整请求帧。
        /// </summary>
        /// <param name="requestFrame">包含有效 CRC 的受支持 0x03、0x06、0x10 或 0xFE 请求。</param>
        /// <param name="behavior">本次处理需要制造的响应行为。</param>
        /// <param name="cancellationToken">取消延迟或响应注入的令牌。</param>
        /// <returns>响应行为完成后的任务。</returns>
        /// <exception cref="ArgumentException">请求不是 Core 严格解析器支持的标准帧时抛出。</exception>
        public async ValueTask HandleWriteAsync(
            ReadOnlyMemory<byte> requestFrame,
            SimulatedModbusBehavior behavior,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                ModbusRequest request = ParseRequest(requestFrame);

                if (!IsRequestAddressedToDevice(request))
                {
                    return;
                }

                if (behavior == SimulatedModbusBehavior.Delayed)
                {
                    TimeSpan delay = ResponseDelay;
                    await delayAsync(delay, cancellationToken).ConfigureAwait(false);
                    behavior = SimulatedModbusBehavior.Normal;
                }

                if (behavior == SimulatedModbusBehavior.Disconnect)
                {
                    transport.RemoteDisconnect(new IOException("模拟设备主动断开串口。"));
                    return;
                }

                if (behavior == SimulatedModbusBehavior.ModbusException)
                {
                    byte configuredExceptionCode = ExceptionCode;
                    byte[] scriptedException = BuildExceptionResponse(
                        request,
                        configuredExceptionCode);
                    await InjectResponseAsync(
                        scriptedException,
                        SimulatedModbusBehavior.Normal,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                bool isValidWrite = HasValidAddressWriteValue(request);

                if (behavior == SimulatedModbusBehavior.NoResponse)
                {
                    if (isValidWrite)
                    {
                        ApplySuccessfulWrite(request);
                    }

                    return;
                }

                byte[] response = isValidWrite
                    ? BuildNormalResponse(request)
                    : BuildExceptionResponse(request, 0x03);
                await InjectResponseAsync(response, behavior, cancellationToken)
                    .ConfigureAwait(false);

                if (isValidWrite)
                {
                    ApplySuccessfulWrite(request);
                }
            }
            finally
            {
                requestGate.Release();
            }
        }

        /// <summary>
        /// 通过 Core 严格解析器把完整字节恢复为已验证请求对象。
        /// </summary>
        /// <param name="requestFrame">待解析的完整线路帧。</param>
        /// <returns>解析成功的不可变请求。</returns>
        /// <exception cref="ArgumentException">帧为空或不属于支持的标准请求时抛出。</exception>
        private static ModbusRequest ParseRequest(ReadOnlyMemory<byte> requestFrame)
        {
            if (requestFrame.IsEmpty)
            {
                throw new ArgumentException("模拟设备请求帧不能为空。", nameof(requestFrame));
            }

            ModbusRequestParseResult result = ModbusRequestParser.TryParseSupported(requestFrame.Span);

            if (result.Status != ModbusRequestParseStatus.Succeeded || result.Request is null)
            {
                throw new ArgumentException(
                    result.ErrorMessage ?? "模拟设备无法识别该请求帧。",
                    nameof(requestFrame));
            }

            return result.Request;
        }

        /// <summary>
        /// 根据请求功能生成结构与 CRC 均有效的正常响应，不在响应注入前改变设备状态。
        /// </summary>
        /// <param name="request">经过 Core 严格验证的请求。</param>
        /// <returns>新的完整响应帧。</returns>
        private byte[] BuildNormalResponse(ModbusRequest request)
        {
            return request.FunctionCode switch
            {
                ModbusFunctionCode.ReadHoldingRegisters => BuildReadResponse(request),
                ModbusFunctionCode.WriteSingleRegister => BuildWriteSingleResponse(request),
                ModbusFunctionCode.WriteMultipleRegisters => BuildWriteMultipleResponse(request),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.FunctionCode,
                    "模拟设备仅支持 0x03、0x06 和 0x10。"),
            };
        }

        /// <summary>
        /// 生成 0x03 大端寄存器数据响应；固定 0xFE 查询返回设备真实地址。
        /// </summary>
        /// <param name="request">已验证的 0x03 或固定 0xFE 查询。</param>
        /// <returns>包含有效 CRC 的完整读取响应。</returns>
        private byte[] BuildReadResponse(ModbusRequest request)
        {
            int registerCount = request.Quantity;
            byte[] body = new byte[3 + (registerCount * 2)];
            body[1] = (byte)ModbusFunctionCode.ReadHoldingRegisters;
            body[2] = checked((byte)(registerCount * 2));

            lock (stateSyncRoot)
            {
                byte responseAddress = request.IsUnknownAddressQuery
                    ? slaveAddress
                    : request.SlaveAddress;
                body[0] = responseAddress;

                for (int registerIndex = 0; registerIndex < registerCount; registerIndex++)
                {
                    ushort value = request.IsUnknownAddressQuery
                        ? slaveAddress
                        : registers.GetValueOrDefault(
                            checked((ushort)(request.StartAddress + registerIndex)));
                    WriteBigEndianUInt16(body, 3 + (registerIndex * 2), value);
                }
            }

            return ModbusCrc16.Append(body);
        }

        /// <summary>
        /// 为 0x06 写入返回请求规定的地址、功能、寄存器和值精确回显，不提前应用写入。
        /// </summary>
        /// <param name="request">已验证的 0x06 请求。</param>
        /// <returns>包含有效 CRC 的八字节回显响应。</returns>
        private byte[] BuildWriteSingleResponse(ModbusRequest request)
        {
            return request.RawFrame.ToArray();
        }

        /// <summary>
        /// 为 0x10 连续写入生成起始地址和数量回显，不提前应用写入。
        /// </summary>
        /// <param name="request">已验证的 0x10 请求。</param>
        /// <returns>包含有效 CRC 的八字节写多寄存器响应。</returns>
        private byte[] BuildWriteMultipleResponse(ModbusRequest request)
        {
            Span<byte> body = stackalloc byte[6];
            body[0] = request.SlaveAddress;
            body[1] = (byte)ModbusFunctionCode.WriteMultipleRegisters;
            WriteBigEndianUInt16(body, 2, request.StartAddress);
            WriteBigEndianUInt16(body, 4, request.Quantity);
            return ModbusCrc16.Append(body);
        }

        /// <summary>
        /// 生成与请求地址和功能对应的固定五字节 Modbus 异常响应。
        /// </summary>
        /// <param name="request">触发异常的已验证请求。</param>
        /// <param name="code">响应数据域中需要返回的 Modbus 异常码。</param>
        /// <returns>包含有效 CRC 和指定异常码的异常帧。</returns>
        private byte[] BuildExceptionResponse(ModbusRequest request, byte code)
        {
            Span<byte> body = stackalloc byte[3];

            lock (stateSyncRoot)
            {
                body[0] = request.IsUnknownAddressQuery
                    ? slaveAddress
                    : request.SlaveAddress;
            }

            body[1] = (byte)((byte)request.FunctionCode | 0x80);
            body[2] = code;
            return ModbusCrc16.Append(body);
        }

        /// <summary>
        /// 按模拟行为注入一项已经构建完成的响应；本方法不修改寄存器或从站地址。
        /// </summary>
        /// <param name="response">包含有效 CRC 的基础响应帧。</param>
        /// <param name="behavior">正常、CRC 错误、分段或粘包响应行为。</param>
        /// <param name="cancellationToken">取消接收注入的令牌。</param>
        /// <returns>全部要求的接收块注入完成后的任务。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="behavior"/> 不是可注入响应行为时抛出。</exception>
        private async ValueTask InjectResponseAsync(
            byte[] response,
            SimulatedModbusBehavior behavior,
            CancellationToken cancellationToken)
        {
            switch (behavior)
            {
                case SimulatedModbusBehavior.Normal:
                    await transport
                        .InjectReceiveAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case SimulatedModbusBehavior.CrcError:
                    response[^1] ^= 0xFF;
                    await transport
                        .InjectReceiveAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case SimulatedModbusBehavior.Segmented:
                    int splitIndex = Math.Max(1, response.Length / 2);
                    await transport
                        .InjectReceiveAsync(response.AsMemory(0, splitIndex), cancellationToken)
                        .ConfigureAwait(false);
                    await transport
                        .InjectReceiveAsync(response.AsMemory(splitIndex), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case SimulatedModbusBehavior.StickyFrames:
                    byte[] stickyFrames = new byte[response.Length * 2];
                    response.CopyTo(stickyFrames, 0);
                    response.CopyTo(stickyFrames, response.Length);
                    await transport
                        .InjectReceiveAsync(stickyFrames, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(behavior),
                        behavior,
                        "未识别或不适用于响应注入的模拟行为。");
            }
        }

        /// <summary>
        /// 判断普通请求是否发送给当前设备，固定 0xFE 未知地址查询始终允许处理。
        /// </summary>
        /// <param name="request">经过严格解析的请求。</param>
        /// <returns>请求为固定查询或普通地址等于当前从站地址时为 <see langword="true"/>。</returns>
        private bool IsRequestAddressedToDevice(ModbusRequest request)
        {
            if (request.IsUnknownAddressQuery)
            {
                return true;
            }

            lock (stateSyncRoot)
            {
                return request.SlaveAddress == slaveAddress;
            }
        }

        /// <summary>
        /// 验证写请求若覆盖地址寄存器 0x0000，其候选值必须处于设备允许的 1 至 64。
        /// </summary>
        /// <param name="request">经过严格解析的读或写请求。</param>
        /// <returns>读请求、未覆盖地址寄存器的写请求或地址值有效时为 <see langword="true"/>。</returns>
        private static bool HasValidAddressWriteValue(ModbusRequest request)
        {
            if (request.FunctionCode == ModbusFunctionCode.ReadHoldingRegisters)
            {
                return true;
            }

            int addressValueIndex = 0x0000 - request.StartAddress;

            if (addressValueIndex < 0 || addressValueIndex >= request.Quantity)
            {
                return true;
            }

            ReadOnlySpan<ushort> values = request.Values.Span;
            ushort requestedAddress = values[addressValueIndex];
            return requestedAddress is >= 1 and <= 64;
        }

        /// <summary>
        /// 原子应用一项已经验证的 0x06 或 0x10 写请求，并在全部寄存器更新后切换设备地址。
        /// </summary>
        /// <param name="request">地址过滤和地址值校验均已通过的请求。</param>
        private void ApplySuccessfulWrite(ModbusRequest request)
        {
            if (request.FunctionCode == ModbusFunctionCode.ReadHoldingRegisters)
            {
                return;
            }

            ReadOnlySpan<ushort> values = request.Values.Span;

            lock (stateSyncRoot)
            {
                byte? newSlaveAddress = null;

                for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
                {
                    ushort address = checked((ushort)(request.StartAddress + valueIndex));
                    ushort value = values[valueIndex];
                    registers[address] = value;

                    if (address == 0x0000)
                    {
                        newSlaveAddress = checked((byte)value);
                    }
                }

                if (newSlaveAddress.HasValue)
                {
                    slaveAddress = newSlaveAddress.Value;
                }
            }
        }

        /// <summary>
        /// 校验模拟设备地址或地址寄存器值是否处于 1 至 64。
        /// </summary>
        /// <param name="value">待校验的无符号地址值。</param>
        /// <param name="parameterName">校验失败时写入异常的参数名。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 不在 1 至 64 时抛出。</exception>
        private static void ValidateSlaveAddress(ushort value, string parameterName)
        {
            if (value is < 1 or > 64)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "模拟设备地址必须位于 1 至 64。");
            }
        }

        /// <summary>
        /// 使用指定时间源执行延迟，使手动时间源能够在不真实休眠的情况下推进等待。
        /// </summary>
        /// <param name="delay">需要等待的非负时长。</param>
        /// <param name="timeProvider">负责创建确定性计时器的时间源。</param>
        /// <param name="cancellationToken">取消等待的令牌。</param>
        /// <returns>时间源到期或取消时完成的值任务。</returns>
        private static async ValueTask DelayWithTimeProviderAsync(
            TimeSpan delay,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 按 Modbus 大端线序写入一个 16 位原始寄存器字。
        /// </summary>
        /// <param name="destination">接收高低字节的目标缓冲区。</param>
        /// <param name="offset">高字节的零基写入偏移。</param>
        /// <param name="value">待编码的原始寄存器字。</param>
        private static void WriteBigEndianUInt16(
            Span<byte> destination,
            int offset,
            ushort value)
        {
            destination[offset] = (byte)(value >> 8);
            destination[offset + 1] = (byte)value;
        }
    }
}
