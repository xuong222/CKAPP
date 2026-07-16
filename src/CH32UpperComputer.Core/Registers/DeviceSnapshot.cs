using CH32UpperComputer.Core.Protocol;

using System.Collections.ObjectModel;

namespace CH32UpperComputer.Core.Registers
{
    /// <summary>
    /// 表示一次寄存器响应写入设备快照的不可变结果。
    /// </summary>
    public sealed class SnapshotApplyResult
    {
        /// <summary>
        /// 初始化一次快照写入结果。
        /// </summary>
        /// <param name="isSuccess">指示响应是否通过全部门控并完成原子更新。</param>
        /// <param name="updatedRegisterCount">成功写入的连续寄存器数量。</param>
        /// <param name="errorMessage">拒绝更新的原因；成功时为空。</param>
        private SnapshotApplyResult(
            bool isSuccess,
            int updatedRegisterCount,
            string? errorMessage)
        {
            IsSuccess = isSuccess;
            UpdatedRegisterCount = updatedRegisterCount;
            ErrorMessage = errorMessage;
        }

        /// <summary>
        /// 获取指示响应是否完成原子快照更新的值。
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// 获取本次成功更新的寄存器数量；失败时为零。
        /// </summary>
        public int UpdatedRegisterCount { get; }

        /// <summary>
        /// 获取拒绝更新的原因；成功时为 <see langword="null"/>。
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// 创建一次成功的原子更新结果。
        /// </summary>
        /// <param name="updatedRegisterCount">已经写入快照的寄存器数量。</param>
        /// <returns>不包含错误信息的成功结果。</returns>
        internal static SnapshotApplyResult Success(int updatedRegisterCount)
        {
            return new SnapshotApplyResult(true, updatedRegisterCount, null);
        }

        /// <summary>
        /// 创建一次未改变快照的拒绝结果。
        /// </summary>
        /// <param name="errorMessage">说明失败门控条件的非空错误信息。</param>
        /// <returns>更新数量为零的失败结果。</returns>
        internal static SnapshotApplyResult Failure(string errorMessage)
        {
            return new SnapshotApplyResult(false, 0, errorMessage);
        }
    }

    /// <summary>
    /// 保存最近一次有效 0x03 解析响应提供的寄存器值，不负责串口接收或 CRC 解析。
    /// </summary>
    public sealed class DeviceSnapshot
    {
        /// <summary>
        /// 保护快照值与时间戳一致性的同步对象。
        /// </summary>
        private readonly object synchronization = new();

        /// <summary>
        /// 按零基协议地址保存最近一次有效解释值。
        /// </summary>
        private readonly Dictionary<ushort, RegisterValue> values = [];

        /// <summary>
        /// 最近一次成功原子更新时由调用方提供的时间戳。
        /// </summary>
        private DateTimeOffset? lastUpdatedAt;

        /// <summary>
        /// 获取当前快照的只读副本；后续更新不会修改已经返回的字典。
        /// </summary>
        public IReadOnlyDictionary<ushort, RegisterValue> Values
        {
            get
            {
                lock (synchronization)
                {
                    return new ReadOnlyDictionary<ushort, RegisterValue>(
                        new Dictionary<ushort, RegisterValue>(values));
                }
            }
        }

        /// <summary>
        /// 获取最近一次成功更新时由调用方提供的时间戳；尚无数据时为空。
        /// </summary>
        public DateTimeOffset? LastUpdatedAt
        {
            get
            {
                lock (synchronization)
                {
                    return lastUpdatedAt;
                }
            }
        }

        /// <summary>
        /// 将成功解析且与当前读取请求结构匹配的连续寄存器响应原子写入快照。
        /// </summary>
        /// <param name="request">产生当前响应的标准 0x03 请求及其起始地址、数量签名。</param>
        /// <param name="response">已经通过 <see cref="ModbusResponseParser"/> 校验的响应结果。</param>
        /// <param name="timestamp">由上层统一时钟提供的响应完成时间，不在模型内部读取系统时间。</param>
        /// <returns>成功时包含更新数量；任何门控失败时保留原快照并返回原因。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> 或 <paramref name="response"/> 为空时抛出。</exception>
        public SnapshotApplyResult Apply(
            ModbusRequest request,
            ModbusResponse response,
            DateTimeOffset timestamp)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(response);

            if (!response.IsSuccess)
            {
                return SnapshotApplyResult.Failure(
                    $"仅成功解析的正常响应可以更新快照；当前状态为 {response.Status}。");
            }

            if (!ReferenceEquals(response.MatchedRequest, request))
            {
                return SnapshotApplyResult.Failure(
                    "成功响应必须与实际参与其解析的同一请求对象一起更新快照。");
            }

            if (request.IsUnknownAddressQuery)
            {
                return SnapshotApplyResult.Failure(
                    "0xFE 未知地址查询仅用于发现真实从站地址，不能更新寄存器数据快照。");
            }

            if (request.FunctionCode != ModbusFunctionCode.ReadHoldingRegisters)
            {
                return SnapshotApplyResult.Failure("只有 0x03 读取响应可以更新寄存器快照。");
            }

            ReadOnlyMemory<ushort> responseRegisters = response.Registers;

            if (responseRegisters.Length != request.Quantity)
            {
                return SnapshotApplyResult.Failure(
                    $"响应寄存器数量 {responseRegisters.Length} 与当前请求数量 {request.Quantity} 不匹配。");
            }

            if (!request.IsUnknownAddressQuery && response.SlaveAddress != request.SlaveAddress)
            {
                return SnapshotApplyResult.Failure("响应从站地址与当前请求不匹配。");
            }

            Dictionary<ushort, RegisterValue> stagedValues = [];

            for (int registerIndex = 0; registerIndex < responseRegisters.Length; registerIndex++)
            {
                int candidateAddress = request.StartAddress + registerIndex;

                if (candidateAddress > ushort.MaxValue)
                {
                    return SnapshotApplyResult.Failure("连续读取范围越过 16 位 Modbus 地址上限。");
                }

                ushort protocolAddress = (ushort)candidateAddress;

                if (!DeviceRegisterMap.TryGetByProtocolAddress(protocolAddress, out RegisterDefinition? definition) ||
                    definition is null)
                {
                    return SnapshotApplyResult.Failure(
                        $"协议地址 0x{protocolAddress:X4} 不属于设备寄存器表，快照未发生部分更新。");
                }

                stagedValues.Add(
                    protocolAddress,
                    RegisterValueConverter.Decode(definition, responseRegisters.Span[registerIndex]));
            }

            lock (synchronization)
            {
                foreach ((ushort protocolAddress, RegisterValue value) in stagedValues)
                {
                    values[protocolAddress] = value;
                }

                lastUpdatedAt = timestamp;
            }

            return SnapshotApplyResult.Success(stagedValues.Count);
        }

        /// <summary>
        /// 根据零基协议地址取得快照中最近一次有效解释值。
        /// </summary>
        /// <param name="protocolAddress">Modbus PDU 中的零基寄存器地址。</param>
        /// <returns>最近一次有效响应提供的不可变寄存器值。</returns>
        /// <exception cref="KeyNotFoundException">该寄存器尚未收到有效值时抛出。</exception>
        public RegisterValue GetByProtocolAddress(ushort protocolAddress)
        {
            lock (synchronization)
            {
                return values.TryGetValue(protocolAddress, out RegisterValue? value)
                    ? value
                    : throw new KeyNotFoundException(
                        $"协议地址 0x{protocolAddress:X4} 尚无有效快照值。");
            }
        }
    }
}
