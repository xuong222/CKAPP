namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 定义 Modbus 响应经过 CRC、结构和请求签名校验后的分类。
    /// </summary>
    public enum ModbusResponseStatus
    {
        /// <summary>
        /// 响应完整匹配请求的正常响应签名。
        /// </summary>
        Succeeded,

        /// <summary>
        /// 响应是地址及异常功能码均匹配请求的标准 Modbus 异常帧。
        /// </summary>
        ModbusException,

        /// <summary>
        /// 响应的长度、CRC、地址、功能或完整回显签名不符合当前请求。
        /// </summary>
        ProtocolError,
    }

    /// <summary>
    /// 表示一项不可变的 Modbus RTU 响应解析结果，不执行寄存器缓存或界面更新。
    /// </summary>
    public sealed class ModbusResponse
    {
        /// <summary>
        /// 响应对象独占持有的原始候选帧副本。
        /// </summary>
        private readonly byte[] rawFrame;

        /// <summary>
        /// 成功读响应对象独占持有的大端解码寄存器字。
        /// </summary>
        private readonly ushort[] registers;

        /// <summary>
        /// 初始化一项已经分类的不可变响应解析结果。
        /// </summary>
        /// <param name="status">响应解析分类。</param>
        /// <param name="slaveAddress">候选响应中的从站地址；空帧协议错误时为 <see langword="null"/>。</param>
        /// <param name="rawFrame">调用方提供的完整响应候选帧。</param>
        /// <param name="registers">成功读响应解码出的连续寄存器字。</param>
        /// <param name="matchedRequest">仅成功响应保留的、实际参与完整签名校验的请求对象；其他分类为 <see langword="null"/>。</param>
        /// <param name="exceptionCode">标准异常响应码；其他分类为 <see langword="null"/>。</param>
        /// <param name="exceptionMeaning">异常码的中文含义；其他分类为 <see langword="null"/>。</param>
        /// <param name="discoveredSlaveAddress">0xFE 专用匹配发现的真实地址；其他分类为 <see langword="null"/>。</param>
        /// <param name="errorMessage">协议错误原因；其他分类为 <see langword="null"/>。</param>
        private ModbusResponse(
            ModbusResponseStatus status,
            byte? slaveAddress,
            ReadOnlySpan<byte> rawFrame,
            ReadOnlySpan<ushort> registers,
            ModbusRequest? matchedRequest,
            byte? exceptionCode,
            string? exceptionMeaning,
            byte? discoveredSlaveAddress,
            string? errorMessage)
        {
            Status = status;
            SlaveAddress = slaveAddress;
            this.rawFrame = rawFrame.ToArray();
            this.registers = registers.ToArray();
            MatchedRequest = matchedRequest;
            ExceptionCode = exceptionCode;
            ExceptionMeaning = exceptionMeaning;
            DiscoveredSlaveAddress = discoveredSlaveAddress;
            ErrorMessage = errorMessage;
        }

        /// <summary>
        /// 获取响应的结构化解析分类。
        /// </summary>
        public ModbusResponseStatus Status { get; }

        /// <summary>
        /// 获取指示响应是否完整匹配正常成功签名的值。
        /// </summary>
        public bool IsSuccess => Status == ModbusResponseStatus.Succeeded;

        /// <summary>
        /// 获取候选响应中的从站地址；空帧协议错误时为 <see langword="null"/>。
        /// </summary>
        public byte? SlaveAddress { get; }

        /// <summary>
        /// 获取原始候选帧的防御性副本。
        /// 每次访问均返回独立数组，调用方无法修改响应对象保存的证据。
        /// </summary>
        public ReadOnlyMemory<byte> RawFrame => (byte[])rawFrame.Clone();

        /// <summary>
        /// 获取成功读响应寄存器字的防御性副本；写响应、异常或协议错误时为空。
        /// </summary>
        public ReadOnlyMemory<ushort> Registers => (ushort[])registers.Clone();

        /// <summary>
        /// 获取实际参与成功响应解析的请求对象；异常和协议错误结果为空。
        /// 此关联供同一核心程序集内的状态模型阻止成功响应脱离其解析上下文后被错误套用。
        /// </summary>
        internal ModbusRequest? MatchedRequest { get; }

        /// <summary>
        /// 获取标准 Modbus 异常码；正常响应或协议错误时为 <see langword="null"/>。
        /// </summary>
        public byte? ExceptionCode { get; }

        /// <summary>
        /// 获取标准异常码对应的中文含义；正常响应或协议错误时为 <see langword="null"/>。
        /// </summary>
        public string? ExceptionMeaning { get; }

        /// <summary>
        /// 获取 0xFE 查询严格发现的 1 至 64 真实从站地址；其他结果为 <see langword="null"/>。
        /// </summary>
        public byte? DiscoveredSlaveAddress { get; }

        /// <summary>
        /// 获取协议错误的清晰原因；成功或标准异常响应时为 <see langword="null"/>。
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// 创建已经完整匹配正常响应签名的成功结果。
        /// </summary>
        /// <param name="matchedRequest">实际参与当前响应签名验证的不可变请求对象。</param>
        /// <param name="slaveAddress">响应实际使用的从站地址。</param>
        /// <param name="rawFrame">CRC 和签名均有效的完整响应帧。</param>
        /// <param name="registers">0x03 响应解码寄存器字；写响应传入空序列。</param>
        /// <param name="discoveredSlaveAddress">0xFE 查询发现的真实地址；普通请求传入 <see langword="null"/>。</param>
        /// <returns>不包含错误或异常信息的成功响应。</returns>
        internal static ModbusResponse CreateSuccess(
            ModbusRequest matchedRequest,
            byte slaveAddress,
            ReadOnlySpan<byte> rawFrame,
            ReadOnlySpan<ushort> registers,
            byte? discoveredSlaveAddress = null)
        {
            ArgumentNullException.ThrowIfNull(matchedRequest);

            return new ModbusResponse(
                ModbusResponseStatus.Succeeded,
                slaveAddress,
                rawFrame,
                registers,
                matchedRequest,
                null,
                null,
                discoveredSlaveAddress,
                null);
        }

        /// <summary>
        /// 创建地址、异常功能码和固定长度均匹配请求的标准异常结果。
        /// </summary>
        /// <param name="slaveAddress">响应中的普通从站地址。</param>
        /// <param name="rawFrame">CRC 有效的五字节标准异常帧。</param>
        /// <param name="exceptionCode">设备返回的 Modbus 异常码。</param>
        /// <param name="exceptionMeaning">异常码对应的中文含义。</param>
        /// <returns>保留原始帧和异常信息的标准异常响应。</returns>
        internal static ModbusResponse CreateModbusException(
            byte slaveAddress,
            ReadOnlySpan<byte> rawFrame,
            byte exceptionCode,
            string exceptionMeaning)
        {
            return new ModbusResponse(
                ModbusResponseStatus.ModbusException,
                slaveAddress,
                rawFrame,
                ReadOnlySpan<ushort>.Empty,
                null,
                exceptionCode,
                exceptionMeaning,
                null,
                null);
        }

        /// <summary>
        /// 创建未通过当前请求 CRC、结构或完整签名校验的协议错误结果。
        /// </summary>
        /// <param name="rawFrame">未通过校验的原始候选帧。</param>
        /// <param name="errorMessage">准确说明首个失败协议条件的错误信息。</param>
        /// <returns>保留原始帧且不包含寄存器或发现地址的协议错误响应。</returns>
        internal static ModbusResponse CreateProtocolError(
            ReadOnlySpan<byte> rawFrame,
            string errorMessage)
        {
            byte? slaveAddress = rawFrame.IsEmpty ? null : rawFrame[0];

            return new ModbusResponse(
                ModbusResponseStatus.ProtocolError,
                slaveAddress,
                rawFrame,
                ReadOnlySpan<ushort>.Empty,
                null,
                null,
                null,
                null,
                errorMessage);
        }
    }
}
