using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 通过共享无队列协调器执行地址、波特率、恢复出厂和 0xFE 专用安全流程。
    /// </summary>
    public sealed class SpecialConfigurationService
    {
        /// <summary>
        /// 0xFE 未知地址查询必须完整展示的固定风险文案。
        /// </summary>
        public const string UnknownAddressRiskWarning =
            "未知地址查询仅适用于总线上只有一个设备的情况；多设备同时应答会产生总线冲突。";

        /// <summary>
        /// 从站地址寄存器 40001 的零基协议地址。
        /// </summary>
        private const ushort SlaveAddressRegister = 0x0000;

        /// <summary>
        /// 波特率代码寄存器 40002 的零基协议地址。
        /// </summary>
        private const ushort BaudRateCodeRegister = 0x0001;

        /// <summary>
        /// 恢复出厂命令寄存器 40036 的零基协议地址。
        /// </summary>
        private const ushort FactoryResetRegister = 0x0023;

        /// <summary>
        /// 恢复出厂命令唯一允许写入的原始值。
        /// </summary>
        private const ushort FactoryResetCommand = 1;

        /// <summary>
        /// 保护当前本地连接配置。
        /// </summary>
        private readonly object configurationSyncRoot = new();

        /// <summary>
        /// 所有专用写入与普通请求共用的无队列事务协调器。
        /// </summary>
        private readonly ModbusTransactionCoordinator coordinator;

        /// <summary>
        /// 专用流程开始时必须停止且不得自动恢复的定时发送服务。
        /// </summary>
        private readonly PeriodicSendService periodicSendService;

        /// <summary>
        /// 波特率或默认配置切换时负责关闭和重新打开物理串口的抽象传输。
        /// </summary>
        private readonly ISerialTransport transport;

        /// <summary>
        /// 软件当前保留的不可变设备连接配置。
        /// </summary>
        private DeviceConnectionConfiguration currentConfiguration;

        /// <summary>
        /// 三类写配置和 0xFE 查询共用的原子活动门；不建立等待队列。
        /// </summary>
        private int activeOperationFlag;

        /// <summary>
        /// 初始化专用安全配置服务及其明确的初始设备连接配置。
        /// </summary>
        /// <param name="coordinator">共享无队列事务协调器。</param>
        /// <param name="periodicSendService">任一专用流程开始前必须停止的定时服务。</param>
        /// <param name="transport">波特率和默认配置切换时重新打开的串口传输。</param>
        /// <param name="initialSlaveAddress">当前真实从站地址，允许 1 至 64。</param>
        /// <param name="initialSerialSettings">当前完整串口参数。</param>
        /// <exception cref="ArgumentNullException">任一服务或串口设置为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">初始地址不在 1 至 64 时抛出。</exception>
        public SpecialConfigurationService(
            ModbusTransactionCoordinator coordinator,
            PeriodicSendService periodicSendService,
            ISerialTransport transport,
            byte initialSlaveAddress,
            SerialSettings initialSerialSettings)
        {
            ArgumentNullException.ThrowIfNull(coordinator);
            ArgumentNullException.ThrowIfNull(periodicSendService);
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(initialSerialSettings);
            this.coordinator = coordinator;
            this.periodicSendService = periodicSendService;
            this.transport = transport;
            currentConfiguration = new DeviceConnectionConfiguration(
                initialSlaveAddress,
                initialSerialSettings);
        }

        /// <summary>
        /// 获取是否已有一个专用流程占用原子活动门。
        /// </summary>
        public bool IsOperationBusy => Volatile.Read(ref activeOperationFlag) != 0;

        /// <summary>
        /// 获取软件当前保留的不可变本地连接配置。
        /// </summary>
        public DeviceConnectionConfiguration CurrentConfiguration
        {
            get
            {
                lock (configurationSyncRoot)
                {
                    return currentConfiguration;
                }
            }
        }

        /// <summary>
        /// 在没有专用流程活动时同步上位机已经选择或成功打开的本地地址与串口参数。
        /// 本方法只更新软件配置，不写串口、不启动定时发送，也不代表设备参数已经被修改。
        /// </summary>
        /// <param name="slaveAddress">当前由用户明确选择或连接成功确认的普通从站地址。</param>
        /// <param name="serialSettings">当前由用户明确选择或连接成功确认的完整串口参数。</param>
        /// <exception cref="ArgumentNullException"><paramref name="serialSettings"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="slaveAddress"/> 不在 1 至 64 时抛出。</exception>
        /// <exception cref="InvalidOperationException">已有专用配置流程活动时抛出。</exception>
        public void SynchronizeLocalConfiguration(
            byte slaveAddress,
            SerialSettings serialSettings)
        {
            ArgumentNullException.ThrowIfNull(serialSettings);

            if (slaveAddress is < 1 or > 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slaveAddress),
                    slaveAddress,
                    "普通 Modbus 从站地址必须位于 1 至 64。");
            }

            if (IsOperationBusy)
            {
                throw new InvalidOperationException("专用配置流程活动时不能覆盖本地连接配置。");
            }

            UpdateCurrentConfiguration(
                new DeviceConnectionConfiguration(slaveAddress, serialSettings));
        }

        /// <summary>
        /// 在旧地址下写入 40001；只有 0x06 完整回显成功后才更新本地地址。
        /// </summary>
        /// <param name="newSlaveAddress">目标真实地址，允许 1 至 64，且不得与当前地址相同。</param>
        /// <param name="cancellationToken">取消尚未完成的配置事务；定时发送仍保持关闭。</param>
        /// <returns>包含旧新地址恢复选项和事务证据的配置结果。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="newSlaveAddress"/> 不在 1 至 64 时抛出。</exception>
        /// <exception cref="ArgumentException"><paramref name="newSlaveAddress"/> 与当前地址相同时抛出。</exception>
        public async ValueTask<ConfigurationChangeResult> ChangeSlaveAddressAsync(
            byte newSlaveAddress,
            CancellationToken cancellationToken)
        {
            if (newSlaveAddress is < 1 or > 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(newSlaveAddress),
                    newSlaveAddress,
                    "目标从站地址必须位于 1 至 64。");
            }

            DeviceConnectionConfiguration previous = CurrentConfiguration;

            if (newSlaveAddress == previous.SlaveAddress)
            {
                throw new ArgumentException("目标从站地址与当前地址相同。", nameof(newSlaveAddress));
            }

            DeviceConnectionConfiguration proposed = new(
                newSlaveAddress,
                previous.SerialSettings);

            if (!TryBeginOperation())
            {
                return CreateServiceBusyResult(
                    ConfigurationChangeKind.SlaveAddress,
                    previous,
                    proposed);
            }

            try
            {
                await periodicSendService.StopAsync(CancellationToken.None).ConfigureAwait(false);
                ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                    previous.SlaveAddress,
                    SlaveAddressRegister,
                    newSlaveAddress);
                TransactionExecutionResult transactionResult = await coordinator.TryExecuteAsync(
                    CreateStandardTransaction(request, previous.SerialSettings),
                    cancellationToken).ConfigureAwait(false);

                if (IsSucceeded(transactionResult))
                {
                    UpdateCurrentConfiguration(proposed);
                    return new ConfigurationChangeResult(
                        ConfigurationChangeKind.SlaveAddress,
                        ConfigurationChangeStatus.Succeeded,
                        $"设备已完整回显地址修改，本地从站地址已更新为 {newSlaveAddress}。",
                        previous,
                        proposed,
                        proposed,
                        transactionResult);
                }

                return CreateWriteFailureResult(
                    ConfigurationChangeKind.SlaveAddress,
                    previous,
                    proposed,
                    transactionResult,
                    "地址修改没有获得完整成功回显");
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>
        /// 在旧波特率下写入 40002；完整回显后关闭旧口并使用新波特率重新打开。
        /// </summary>
        /// <param name="newBaudRate">目标波特率，必须属于设备代码 0 至 5 对应的固定集合。</param>
        /// <param name="cancellationToken">取消尚未完成的配置事务；成功回显后的重连不被中途取消。</param>
        /// <returns>包含旧新串口恢复选项、事务和重连结果的配置结果。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="newBaudRate"/> 不受固件支持时抛出。</exception>
        /// <exception cref="ArgumentException"><paramref name="newBaudRate"/> 与当前波特率相同时抛出。</exception>
        public async ValueTask<ConfigurationChangeResult> ChangeBaudRateAsync(
            int newBaudRate,
            CancellationToken cancellationToken)
        {
            ushort baudRateCode = GetBaudRateCode(newBaudRate);
            DeviceConnectionConfiguration previous = CurrentConfiguration;

            if (newBaudRate == previous.SerialSettings.BaudRate)
            {
                throw new ArgumentException("目标波特率与当前波特率相同。", nameof(newBaudRate));
            }

            SerialSettings proposedSettings = CopySerialSettingsWithBaudRate(
                previous.SerialSettings,
                newBaudRate);
            DeviceConnectionConfiguration proposed = new(
                previous.SlaveAddress,
                proposedSettings);

            if (!TryBeginOperation())
            {
                return CreateServiceBusyResult(
                    ConfigurationChangeKind.BaudRate,
                    previous,
                    proposed);
            }

            try
            {
                await periodicSendService.StopAsync(CancellationToken.None).ConfigureAwait(false);
                ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                    previous.SlaveAddress,
                    BaudRateCodeRegister,
                    baudRateCode);
                TransactionExecutionResult transactionResult = await coordinator.TryExecuteAsync(
                    CreateStandardTransaction(request, previous.SerialSettings),
                    cancellationToken).ConfigureAwait(false);

                if (!IsSucceeded(transactionResult))
                {
                    return CreateWriteFailureResult(
                        ConfigurationChangeKind.BaudRate,
                        previous,
                        proposed,
                        transactionResult,
                        "波特率修改没有获得完整成功回显");
                }

                return await ReconnectAfterConfirmedChangeAsync(
                    ConfigurationChangeKind.BaudRate,
                    previous,
                    proposed,
                    transactionResult,
                    $"设备已完整回显波特率修改，并已使用 {newBaudRate} 重新打开串口。")
                    .ConfigureAwait(false);
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>
        /// 二次确认后在旧配置下写入 40036=1；完整回显后切换到地址 1 和 9600-8-N-1。
        /// </summary>
        /// <param name="confirmationAccepted">界面已经完成危险操作二次确认时必须为真。</param>
        /// <param name="cancellationToken">取消尚未完成的恢复事务；成功回显后的重连不被中途取消。</param>
        /// <returns>包含旧配置和默认配置恢复选项的配置结果。</returns>
        /// <exception cref="InvalidOperationException"><paramref name="confirmationAccepted"/> 为假时抛出。</exception>
        public async ValueTask<ConfigurationChangeResult> RestoreFactoryDefaultsAsync(
            bool confirmationAccepted,
            CancellationToken cancellationToken)
        {
            if (!confirmationAccepted)
            {
                throw new InvalidOperationException("恢复出厂前必须完成二次确认。");
            }

            DeviceConnectionConfiguration previous = CurrentConfiguration;
            DeviceConnectionConfiguration proposed = new(
                1,
                SerialSettings.CreateDefault(previous.SerialSettings.PortName));

            if (!TryBeginOperation())
            {
                return CreateServiceBusyResult(
                    ConfigurationChangeKind.FactoryReset,
                    previous,
                    proposed);
            }

            try
            {
                await periodicSendService.StopAsync(CancellationToken.None).ConfigureAwait(false);
                ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                    previous.SlaveAddress,
                    FactoryResetRegister,
                    FactoryResetCommand);
                TransactionExecutionResult transactionResult = await coordinator.TryExecuteAsync(
                    CreateStandardTransaction(request, previous.SerialSettings),
                    cancellationToken).ConfigureAwait(false);

                if (!IsSucceeded(transactionResult))
                {
                    return CreateWriteFailureResult(
                        ConfigurationChangeKind.FactoryReset,
                        previous,
                        proposed,
                        transactionResult,
                        "恢复出厂命令没有获得完整成功回显");
                }

                return await ReconnectAfterConfirmedChangeAsync(
                    ConfigurationChangeKind.FactoryReset,
                    previous,
                    proposed,
                    transactionResult,
                    "设备已完整回显恢复出厂命令，并已使用地址 1、9600-8-N-1 重新打开串口。")
                    .ConfigureAwait(false);
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>
        /// 在定时发送关闭后执行固定 0xFE 查询，只接受真实地址和值一致的单设备响应。
        /// </summary>
        /// <param name="cancellationToken">取消未知地址查询事务的令牌。</param>
        /// <returns>成功时携带 1 至 64 的真实发现地址，失败时保留固定风险文案。</returns>
        public async ValueTask<ConfigurationChangeResult> DiscoverUnknownAddressAsync(
            CancellationToken cancellationToken)
        {
            DeviceConnectionConfiguration previous = CurrentConfiguration;

            if (!TryBeginOperation())
            {
                return CreateServiceBusyResult(
                    ConfigurationChangeKind.UnknownAddressDiscovery,
                    previous,
                    null,
                    UnknownAddressRiskWarning);
            }

            try
            {
                await periodicSendService.StopAsync(CancellationToken.None).ConfigureAwait(false);
                ModbusRequest request = ModbusRequestFactory.CreateUnknownAddressQuery();
                TransactionExecutionResult transactionResult = await coordinator.TryExecuteAsync(
                    CreateStandardTransaction(request, previous.SerialSettings),
                    cancellationToken).ConfigureAwait(false);
                byte? discoveredAddress = GetStrictDiscoveredAddress(transactionResult);

                if (!discoveredAddress.HasValue)
                {
                    ConfigurationChangeStatus status =
                        transactionResult.Rejection == TransactionRejected.Busy
                            ? ConfigurationChangeStatus.Busy
                            : ConfigurationChangeStatus.Failed;
                    return new ConfigurationChangeResult(
                        ConfigurationChangeKind.UnknownAddressDiscovery,
                        status,
                        $"{UnknownAddressRiskWarning} 未获得符合真实地址和值一致规则的响应。",
                        previous,
                        null,
                        previous,
                        transactionResult);
                }

                DeviceConnectionConfiguration discoveredConfiguration = new(
                    discoveredAddress.Value,
                    previous.SerialSettings);
                UpdateCurrentConfiguration(discoveredConfiguration);
                return new ConfigurationChangeResult(
                    ConfigurationChangeKind.UnknownAddressDiscovery,
                    ConfigurationChangeStatus.Succeeded,
                    $"{UnknownAddressRiskWarning} 已发现真实从站地址 {discoveredAddress.Value}。",
                    previous,
                    discoveredConfiguration,
                    discoveredConfiguration,
                    transactionResult,
                    discoveredAddress);
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>
        /// 在设备配置已由完整回显确认后关闭旧会话并按新配置重新打开。
        /// </summary>
        /// <param name="kind">波特率修改或恢复出厂操作类型。</param>
        /// <param name="previous">旧地址和旧串口参数。</param>
        /// <param name="proposed">已经由设备回显确认的新配置。</param>
        /// <param name="transactionResult">成功写事务证据。</param>
        /// <param name="successMessage">重连成功时的明确说明。</param>
        /// <returns>重连成功结果，或设备配置已知但本地重连失败的结果。</returns>
        private async ValueTask<ConfigurationChangeResult> ReconnectAfterConfirmedChangeAsync(
            ConfigurationChangeKind kind,
            DeviceConnectionConfiguration previous,
            DeviceConnectionConfiguration proposed,
            TransactionExecutionResult transactionResult,
            string successMessage)
        {
            UpdateCurrentConfiguration(proposed);

            try
            {
                await coordinator.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                await transport.OpenAsync(
                    proposed.SerialSettings,
                    CancellationToken.None).ConfigureAwait(false);
                await coordinator.StartAsync(CancellationToken.None).ConfigureAwait(false);
                return new ConfigurationChangeResult(
                    kind,
                    ConfigurationChangeStatus.Succeeded,
                    successMessage,
                    previous,
                    proposed,
                    proposed,
                    transactionResult);
            }
            catch (Exception exception)
            {
                await EnsureDisconnectedAfterReconnectFailureAsync().ConfigureAwait(false);
                return new ConfigurationChangeResult(
                    kind,
                    ConfigurationChangeStatus.Failed,
                    $"设备已确认新配置，但本地串口重新打开失败：{exception.Message}",
                    previous,
                    proposed,
                    proposed,
                    transactionResult,
                    exception: exception);
            }
        }

        /// <summary>
        /// 重连失败后尽力关闭可能只打开一半的串口会话，并吞并二次清理异常。
        /// </summary>
        /// <returns>清理尝试结束后的任务。</returns>
        private async ValueTask EnsureDisconnectedAfterReconnectFailureAsync()
        {
            try
            {
                await coordinator.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try
                {
                    await transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 原始重连异常由结果保留；二次幂等清理不得覆盖首个故障证据。
                }
            }
        }

        /// <summary>
        /// 根据非成功写事务区分无写入 Busy、确定 Modbus 拒绝和配置不确定。
        /// </summary>
        /// <param name="kind">专用写流程类型。</param>
        /// <param name="previous">旧连接配置。</param>
        /// <param name="proposed">新连接或默认配置恢复选项。</param>
        /// <param name="transactionResult">协调器提交结果。</param>
        /// <param name="operationDescription">用于结果消息的操作说明。</param>
        /// <returns>Busy、确定失败或 ConfigurationUncertain 结果。</returns>
        private static ConfigurationChangeResult CreateWriteFailureResult(
            ConfigurationChangeKind kind,
            DeviceConnectionConfiguration previous,
            DeviceConnectionConfiguration proposed,
            TransactionExecutionResult transactionResult,
            string operationDescription)
        {
            if (!transactionResult.IsAccepted)
            {
                ConfigurationChangeStatus rejectionStatus =
                    transactionResult.Rejection == TransactionRejected.Busy
                        ? ConfigurationChangeStatus.Busy
                        : ConfigurationChangeStatus.Failed;
                return new ConfigurationChangeResult(
                    kind,
                    rejectionStatus,
                    $"{operationDescription}；请求未写入：{transactionResult.Message}",
                    previous,
                    proposed,
                    previous,
                    transactionResult);
            }

            if (transactionResult.Outcome!.State == TransactionCompletionState.ModbusException)
            {
                return new ConfigurationChangeResult(
                    kind,
                    ConfigurationChangeStatus.Failed,
                    $"{operationDescription}；设备返回明确 Modbus 异常，旧配置保持不变。",
                    previous,
                    proposed,
                    previous,
                    transactionResult);
            }

            return new ConfigurationChangeResult(
                kind,
                ConfigurationChangeStatus.ConfigurationUncertain,
                $"{operationDescription}；写请求可能已生效但响应丢失，不能猜测设备实际配置。请尝试旧配置或新配置。",
                previous,
                proposed,
                previous,
                transactionResult);
        }

        /// <summary>
        /// 创建服务自身活动门或共享协调器 Busy 的无排队结果。
        /// </summary>
        /// <param name="kind">被拒绝的专用流程类型。</param>
        /// <param name="previous">当前旧配置。</param>
        /// <param name="proposed">可选目标配置。</param>
        /// <param name="prefix">可选固定风险说明。</param>
        /// <returns>没有事务编号和线路写入的 Busy 结果。</returns>
        private static ConfigurationChangeResult CreateServiceBusyResult(
            ConfigurationChangeKind kind,
            DeviceConnectionConfiguration previous,
            DeviceConnectionConfiguration? proposed,
            string? prefix = null)
        {
            string message = string.IsNullOrWhiteSpace(prefix)
                ? "当前已有专用配置流程等待处理，本操作不会排队。"
                : $"{prefix} 当前已有专用配置流程等待处理，本查询不会排队。";
            return new ConfigurationChangeResult(
                kind,
                ConfigurationChangeStatus.Busy,
                message,
                previous,
                proposed,
                previous);
        }

        /// <summary>
        /// 创建使用当前串口超时参数的标准事务请求。
        /// </summary>
        /// <param name="request">由统一工厂创建的标准 Modbus 请求。</param>
        /// <param name="settings">当前串口会话的响应和原始静默超时。</param>
        /// <returns>严格标准匹配事务。</returns>
        private static TransactionRequest CreateStandardTransaction(
            ModbusRequest request,
            SerialSettings settings)
        {
            return TransactionRequest.CreateStandard(
                request,
                settings.ResponseTimeout,
                settings.RawInterByteTimeout);
        }

        /// <summary>
        /// 判断协调器结果是否为完整标准成功响应。
        /// </summary>
        /// <param name="result">待判断事务提交结果。</param>
        /// <returns>已接受且唯一终态为 Succeeded 时返回真。</returns>
        private static bool IsSucceeded(TransactionExecutionResult result)
        {
            return result.IsAccepted &&
                result.Outcome!.State == TransactionCompletionState.Succeeded;
        }

        /// <summary>
        /// 从 0xFE 成功结果中再次核对真实地址、单寄存器和值一致规则。
        /// </summary>
        /// <param name="result">固定未知地址查询事务结果。</param>
        /// <returns>满足全部规则的 1 至 64 真实地址，否则为空。</returns>
        private static byte? GetStrictDiscoveredAddress(TransactionExecutionResult result)
        {
            if (!IsSucceeded(result))
            {
                return null;
            }

            ModbusResponse? response = result.Outcome!.Response;
            byte? discoveredAddress = response?.DiscoveredSlaveAddress;
            ReadOnlyMemory<ushort> registers = response?.Registers ?? ReadOnlyMemory<ushort>.Empty;

            if (!discoveredAddress.HasValue ||
                discoveredAddress.Value is < 1 or > 64 ||
                registers.Length != 1 ||
                registers.Span[0] != discoveredAddress.Value)
            {
                return null;
            }

            return discoveredAddress;
        }

        /// <summary>
        /// 把受支持目标波特率映射为固件寄存器代码 0 至 5。
        /// </summary>
        /// <param name="baudRate">目标线路波特率。</param>
        /// <returns>40002 需要写入的无符号代码。</returns>
        /// <exception cref="ArgumentOutOfRangeException">波特率不受设备支持时抛出。</exception>
        private static ushort GetBaudRateCode(int baudRate)
        {
            return baudRate switch
            {
                2400 => 0,
                4800 => 1,
                9600 => 2,
                19200 => 3,
                38400 => 4,
                57600 => 5,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(baudRate),
                    baudRate,
                    "目标波特率必须为 2400、4800、9600、19200、38400 或 57600。"),
            };
        }

        /// <summary>
        /// 复制当前完整串口参数并只替换波特率。
        /// </summary>
        /// <param name="settings">当前完整串口参数。</param>
        /// <param name="newBaudRate">目标受支持波特率。</param>
        /// <returns>保留端口名、格式和超时的新设置。</returns>
        private static SerialSettings CopySerialSettingsWithBaudRate(
            SerialSettings settings,
            int newBaudRate)
        {
            return new SerialSettings(
                settings.PortName,
                newBaudRate,
                settings.DataBits,
                settings.Parity,
                settings.StopBits,
                settings.ResponseTimeout,
                settings.RawInterByteTimeout);
        }

        /// <summary>
        /// 原子占用专用流程活动门，不等待也不排队。
        /// </summary>
        /// <returns>本次调用首次占用活动门时返回真。</returns>
        private bool TryBeginOperation()
        {
            return Interlocked.CompareExchange(ref activeOperationFlag, 1, 0) == 0;
        }

        /// <summary>
        /// 在专用流程 owner 的 finally 中恰好释放一次活动门。
        /// </summary>
        private void EndOperation()
        {
            Interlocked.Exchange(ref activeOperationFlag, 0);
        }

        /// <summary>
        /// 更新软件保留的本地配置；调用方必须已经获得确定成功证据。
        /// </summary>
        /// <param name="configuration">已经由完整回显确认的真实配置。</param>
        private void UpdateCurrentConfiguration(DeviceConnectionConfiguration configuration)
        {
            lock (configurationSyncRoot)
            {
                currentConfiguration = configuration;
            }
        }
    }
}
