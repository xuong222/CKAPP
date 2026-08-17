using CH32UpperComputer.App.Services;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Core.Registers;
using CH32UpperComputer.Infrastructure.Coordination;
using CH32UpperComputer.Infrastructure.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System.Collections.ObjectModel;
using System.ComponentModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 管理 40001 至 40036 统一寄存器表、普通读写和三项独立安全配置流程。
    /// </summary>
    public sealed partial class ParametersViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 复用标准请求构建、当前超时和普通事务入口的收发 ViewModel。
        /// </summary>
        private readonly CommandConsoleViewModel commandConsole;

        /// <summary>
        /// 提供当前连接、地址、波特率和共享忙状态的串口 ViewModel。
        /// </summary>
        private readonly SerialConnectionViewModel serialConnection;

        /// <summary>
        /// 地址、波特率、恢复出厂和 0xFE 使用的独立安全配置服务。
        /// </summary>
        private readonly SpecialConfigurationService specialConfigurationService;

        /// <summary>
        /// 提供成功 0x03 快照、统一日志统计和独占事务序列的操作服务。
        /// </summary>
        private readonly ModbusOperationService operationService;

        /// <summary>
        /// 将后台事务完成事件切换到界面线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 在 IAP 独占期间禁用参数读写和专用流程。
        /// </summary>
        private readonly IApplicationOperationGate applicationOperationGate;

        /// <summary>
        /// 当前页面是否正在执行一次读取、写入或专用配置操作。
        /// </summary>
        private bool isLocalOperationBusy;

        /// <summary>
        /// 指示名称点击引发的批量选择同步正在进行，避免发布中间状态。
        /// </summary>
        private bool isSynchronizingSelection;

        /// <summary>
        /// 指示 ViewModel 已释放并停止接收事件。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 当前选择数量、读取能力和修改限制的即时说明。
        /// </summary>
        [ObservableProperty]
        private string selectionSummary =
            "未选择寄存器；勾选后可读取，普通可写项可修改。";

        /// <summary>
        /// 参数页最近一次校验或事务结果说明。
        /// </summary>
        [ObservableProperty]
        private string statusMessage =
            "进入页面不会自动读取；可读取全部、读取选中或使用行内读取。";

        /// <summary>
        /// 用户准备写入的目标从站地址。
        /// </summary>
        [ObservableProperty]
        private int targetSlaveAddress;

        /// <summary>
        /// 用户准备切换到的目标波特率。
        /// </summary>
        [ObservableProperty]
        private int targetBaudRate;

        /// <summary>
        /// 用户是否已经明确勾选恢复出厂二次确认。
        /// </summary>
        [ObservableProperty]
        private bool factoryResetConfirmed;

        /// <summary>
        /// 0xFE 查询必须始终显示的固定风险文案。
        /// </summary>
        [ObservableProperty]
        private string unknownAddressWarning =
            SpecialConfigurationService.UnknownAddressRiskWarning;

        /// <summary>
        /// 初始化统一寄存器表及全部读取、修改和特殊配置命令。
        /// </summary>
        /// <param name="commandConsole">复用标准事务构建和执行路径的收发 ViewModel。</param>
        /// <param name="serialConnection">提供当前连接参数和共享忙状态的串口 ViewModel。</param>
        /// <param name="specialConfigurationService">执行独立安全配置流程的服务。</param>
        /// <param name="operationService">提供设备快照、统一记录和独占序列的服务。</param>
        /// <param name="dispatcher">负责把后台事件投递到界面线程的调度器。</param>
        /// <param name="applicationOperationGate">
        /// 可选应用级通信门；为空时创建独立门保持旧构造兼容。
        /// </param>
        public ParametersViewModel(
            CommandConsoleViewModel commandConsole,
            SerialConnectionViewModel serialConnection,
            SpecialConfigurationService specialConfigurationService,
            ModbusOperationService operationService,
            IUiDispatcher dispatcher,
            IApplicationOperationGate? applicationOperationGate = null)
        {
            ArgumentNullException.ThrowIfNull(commandConsole);
            ArgumentNullException.ThrowIfNull(serialConnection);
            ArgumentNullException.ThrowIfNull(specialConfigurationService);
            ArgumentNullException.ThrowIfNull(operationService);
            ArgumentNullException.ThrowIfNull(dispatcher);
            this.commandConsole = commandConsole;
            this.serialConnection = serialConnection;
            this.specialConfigurationService = specialConfigurationService;
            this.operationService = operationService;
            this.dispatcher = dispatcher;
            this.applicationOperationGate =
                applicationOperationGate ?? new ApplicationOperationGate();
            Registers = new ObservableCollection<ParameterItemViewModel>(
                DeviceRegisterMap.All.Select(
                    definition => new ParameterItemViewModel(definition)));
            targetSlaveAddress = serialConnection.SlaveAddress;
            targetBaudRate = serialConnection.BaudRate;
            ReadAllCommand = new AsyncRelayCommand(
                ReadAllAsync,
                CanExecuteStandardOperation);
            ReadSelectedCommand = new AsyncRelayCommand(
                ReadSelectedAsync,
                CanExecuteReadSelected);
            ModifySelectedCommand = new AsyncRelayCommand(
                ModifySelectedAsync,
                CanExecuteModifySelected);
            ReadItemCommand = new AsyncRelayCommand<ParameterItemViewModel>(
                ReadItemAsync,
                CanReadItem);
            ModifyItemCommand = new AsyncRelayCommand<ParameterItemViewModel>(
                ModifyItemAsync,
                CanModifyItem);
            SelectItemCommand = new RelayCommand<ParameterItemViewModel>(
                SelectItem);
            ChangeAddressCommand = new AsyncRelayCommand(
                ChangeAddressAsync,
                CanExecuteSpecialOperation);
            ChangeBaudRateCommand = new AsyncRelayCommand(
                ChangeBaudRateAsync,
                CanExecuteSpecialOperation);
            RestoreFactoryCommand = new AsyncRelayCommand(
                RestoreFactoryAsync,
                CanRestoreFactory);
            DiscoverAddressCommand = new AsyncRelayCommand(
                DiscoverAddressAsync,
                CanExecuteSpecialOperation);

            foreach (ParameterItemViewModel item in Registers)
            {
                item.PropertyChanged += HandleRegisterItemPropertyChanged;
            }

            operationService.SnapshotUpdated += HandleSnapshotUpdated;
            serialConnection.PropertyChanged += HandleSerialConnectionPropertyChanged;
            this.applicationOperationGate.IapActivityChanged +=
                HandleIapActivityChanged;
            RefreshFromSnapshot();
        }

        /// <summary>
        /// 获取按协议地址升序排列的完整 40001 至 40036 统一寄存器表。
        /// </summary>
        public ObservableCollection<ParameterItemViewModel> Registers { get; }

        /// <summary>
        /// 获取手动读取全部 40001 至 40036 的命令。
        /// </summary>
        public IAsyncRelayCommand ReadAllCommand { get; }

        /// <summary>
        /// 获取读取任意选中寄存器最小至最大地址跨度的命令。
        /// </summary>
        public IAsyncRelayCommand ReadSelectedCommand { get; }

        /// <summary>
        /// 获取对同一普通可写组中的选中项执行单项或批量修改的命令。
        /// </summary>
        public IAsyncRelayCommand ModifySelectedCommand { get; }

        /// <summary>
        /// 获取读取单个参数行的命令。
        /// </summary>
        public IAsyncRelayCommand<ParameterItemViewModel> ReadItemCommand { get; }

        /// <summary>
        /// 获取使用 0x06 修改单个普通可写参数行的命令。
        /// </summary>
        public IAsyncRelayCommand<ParameterItemViewModel> ModifyItemCommand { get; }

        /// <summary>
        /// 获取点击寄存器名称时选中当前行的命令。
        /// </summary>
        public IRelayCommand<ParameterItemViewModel> SelectItemCommand { get; }

        /// <summary>
        /// 获取调用独立地址修改流程的命令。
        /// </summary>
        public IAsyncRelayCommand ChangeAddressCommand { get; }

        /// <summary>
        /// 获取调用独立波特率修改和重连流程的命令。
        /// </summary>
        public IAsyncRelayCommand ChangeBaudRateCommand { get; }

        /// <summary>
        /// 获取二次确认后调用恢复出厂独立流程的命令。
        /// </summary>
        public IAsyncRelayCommand RestoreFactoryCommand { get; }

        /// <summary>
        /// 获取显示固定风险后执行 0xFE 单设备地址查询的命令。
        /// </summary>
        public IAsyncRelayCommand DiscoverAddressCommand { get; }

        /// <summary>
        /// 取消快照、寄存器行和串口状态事件订阅。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            operationService.SnapshotUpdated -= HandleSnapshotUpdated;
            serialConnection.PropertyChanged -= HandleSerialConnectionPropertyChanged;
            applicationOperationGate.IapActivityChanged -=
                HandleIapActivityChanged;

            foreach (ParameterItemViewModel item in Registers)
            {
                item.PropertyChanged -= HandleRegisterItemPropertyChanged;
                item.Dispose();
            }
        }

        /// <summary>
        /// 手动读取完整设备寄存器表，不在进入页面时自动执行。
        /// </summary>
        /// <returns>标准 0x03 事务完成后的任务。</returns>
        private Task ReadAllAsync()
        {
            return ExecutePageOperationAsync(
                async () =>
                {
                    TransactionExecutionResult result = await ReadRangeCoreAsync(
                        0x0000,
                        36).ConfigureAwait(true);
                    StatusMessage = result.Message;
                });
        }

        /// <summary>
        /// 手动读取全部选中项的最小至最大地址跨度。
        /// </summary>
        /// <returns>选择校验或标准 0x03 事务完成后的任务。</returns>
        private Task ReadSelectedAsync()
        {
            return ExecutePageOperationAsync(
                async () =>
                {
                    ParameterItemViewModel[] selected = GetSelectedItems();

                    if (selected.Length == 0)
                    {
                        StatusMessage = "请先勾选至少一个需要读取的寄存器。";
                        return;
                    }

                    ushort startAddress = selected[0].Definition.ProtocolAddress;
                    ushort endAddress = selected[^1].Definition.ProtocolAddress;
                    ushort quantity = checked((ushort)(endAddress - startAddress + 1));
                    TransactionExecutionResult result = await ReadRangeCoreAsync(
                        startAddress,
                        quantity).ConfigureAwait(true);
                    StatusMessage = result.Message;
                });
        }

        /// <summary>
        /// 手动读取指定单个寄存器。
        /// </summary>
        /// <param name="item">需要读取的统一寄存器表行。</param>
        /// <returns>标准 0x03 单寄存器事务完成后的任务。</returns>
        private Task ReadItemAsync(ParameterItemViewModel? item)
        {
            return ExecutePageOperationAsync(
                async () =>
                {
                    if (item is null)
                    {
                        StatusMessage = "未指定需要读取的寄存器。";
                        return;
                    }

                    TransactionExecutionResult result = await ReadRangeCoreAsync(
                        item.Definition.ProtocolAddress,
                        1).ConfigureAwait(true);
                    StatusMessage = result.Message;
                });
        }

        /// <summary>
        /// 使用 0x06 修改指定单个普通可写寄存器。
        /// </summary>
        /// <param name="item">需要修改且已经填写输入的参数行。</param>
        /// <returns>输入校验或标准 0x06 事务完成后的任务。</returns>
        private Task ModifyItemAsync(ParameterItemViewModel? item)
        {
            return ExecutePageOperationAsync(
                async () =>
                {
                    if (item is null || !item.IsOrdinaryWritable)
                    {
                        StatusMessage = "该寄存器为只读项或必须使用专用安全流程。";
                        return;
                    }

                    if (!item.TryGetRawWord(out ushort rawWord))
                    {
                        StatusMessage =
                            $"{item.Definition.DocumentAddress}：{item.ValidationMessage}";
                        return;
                    }

                    TransactionExecutionResult result = await WriteSingleCoreAsync(
                        item,
                        rawWord).ConfigureAwait(true);
                    StatusMessage = result.Message;
                });
        }

        /// <summary>
        /// 根据选中数量执行一项 0x06、连续 0x10 或非连续回读合并 0x10。
        /// </summary>
        /// <returns>预校验、回读或最终写事务完成后的任务。</returns>
        private Task ModifySelectedAsync()
        {
            return ExecutePageOperationAsync(ModifySelectedCoreAsync);
        }

        /// <summary>
        /// 完整校验选中项并执行对应单项或批量写入策略。
        /// </summary>
        /// <returns>校验失败或写入事务完成后的任务。</returns>
        private async Task ModifySelectedCoreAsync()
        {
            ParameterItemViewModel[] selected = GetSelectedItems();

            if (selected.Length == 0)
            {
                StatusMessage = "请先勾选并填写至少一个需要修改的普通参数。";
                return;
            }

            if (selected.Any(item => !item.IsOrdinaryWritable))
            {
                StatusMessage = "批量修改只能包含 40011～40023 或 40030～40035 的普通可写参数。";
                return;
            }

            bool usesAlarmGroup = selected[0].Definition.DocumentAddress <= 40023;

            if (selected.Any(
                item => (item.Definition.DocumentAddress <= 40023) != usesAlarmGroup))
            {
                StatusMessage = "一次批量修改不能跨越报警参数组和补偿参数组。";
                return;
            }

            Dictionary<ushort, ushort> editedWords = [];
            List<string> errors = [];

            foreach (ParameterItemViewModel item in selected)
            {
                if (item.TryGetRawWord(out ushort rawWord))
                {
                    editedWords.Add(item.Definition.ProtocolAddress, rawWord);
                }
                else
                {
                    errors.Add(
                        $"{item.Definition.DocumentAddress}：{item.ValidationMessage}");
                }
            }

            if (errors.Count > 0)
            {
                StatusMessage =
                    $"参数预校验失败，未写入任何数据：{string.Join("；", errors)}";
                return;
            }

            if (selected.Length == 1)
            {
                TransactionExecutionResult singleResult = await WriteSingleCoreAsync(
                    selected[0],
                    editedWords[selected[0].Definition.ProtocolAddress])
                    .ConfigureAwait(true);
                StatusMessage = singleResult.Message;
                return;
            }

            ushort startAddress = selected[0].Definition.ProtocolAddress;
            ushort endAddress = selected[^1].Definition.ProtocolAddress;
            int spanLength = endAddress - startAddress + 1;

            if (spanLength == selected.Length)
            {
                ushort[] contiguousWords = Enumerable
                    .Range(startAddress, spanLength)
                    .Select(address => editedWords[checked((ushort)address)])
                    .ToArray();
                TransactionExecutionResult writeResult = await WriteMultipleCoreAsync(
                    startAddress,
                    contiguousWords).ConfigureAwait(true);

                if (IsSuccessful(writeResult))
                {
                    ApplySuccessfulWriteValues(editedWords);
                }

                StatusMessage = writeResult.Message;
                return;
            }

            await ExecuteNonContiguousWriteAsync(
                startAddress,
                endAddress,
                editedWords).ConfigureAwait(true);
        }

        /// <summary>
        /// 在独占序列中回读非连续选择跨度、合并未修改原值并发送一帧 0x10。
        /// </summary>
        /// <param name="startAddress">选中项最小零基协议地址。</param>
        /// <param name="endAddress">选中项最大零基协议地址。</param>
        /// <param name="editedWords">用户明确修改的协议地址和原始字。</param>
        /// <returns>租约拒绝、回读失败或最终写入完成后的任务。</returns>
        private async Task ExecuteNonContiguousWriteAsync(
            ushort startAddress,
            ushort endAddress,
            IReadOnlyDictionary<ushort, ushort> editedWords)
        {
            ModbusOperationSequence? sequence = operationService.TryBeginSequence();

            if (sequence is null)
            {
                StatusMessage = "当前已有请求等待处理，无法开始回读后写入。";
                return;
            }

            await using (sequence)
            {
                ushort quantity = checked((ushort)(endAddress - startAddress + 1));
                ModbusRequest readRequest =
                    ModbusRequestFactory.CreateReadHoldingRegisters(
                        CurrentSlaveAddress,
                        startAddress,
                        quantity);
                TransactionExecutionResult readResult = await sequence.ExecuteAsync(
                    commandConsole.CreateStandardTransaction(readRequest),
                    CancellationToken.None).ConfigureAwait(true);

                if (!IsSuccessful(readResult))
                {
                    StatusMessage = $"批量修改前回读失败，未发送写帧：{readResult.Message}";
                    return;
                }

                IReadOnlyDictionary<ushort, RegisterValue> snapshot =
                    operationService.DeviceSnapshot.Values;
                ushort[] mergedWords = new ushort[quantity];

                for (int offset = 0; offset < quantity; offset++)
                {
                    ushort address = checked((ushort)(startAddress + offset));

                    if (editedWords.TryGetValue(address, out ushort editedWord))
                    {
                        mergedWords[offset] = editedWord;
                    }
                    else if (snapshot.TryGetValue(address, out RegisterValue? currentValue))
                    {
                        mergedWords[offset] = currentValue.RawWord;
                    }
                    else
                    {
                        StatusMessage =
                            $"回读后缺少协议地址 0x{address:X4} 的原始值，未发送写帧。";
                        return;
                    }
                }

                ModbusRequest writeRequest =
                    ModbusRequestFactory.CreateWriteMultipleRegisters(
                        CurrentSlaveAddress,
                        startAddress,
                        mergedWords);
                TransactionExecutionResult writeResult = await sequence.ExecuteAsync(
                    commandConsole.CreateStandardTransaction(writeRequest),
                    CancellationToken.None).ConfigureAwait(true);

                if (IsSuccessful(writeResult))
                {
                    ApplySuccessfulWriteValues(editedWords);
                }

                StatusMessage = writeResult.Message;
            }
        }

        /// <summary>
        /// 使用当前从站地址执行一项连续 0x03 读取。
        /// </summary>
        /// <param name="startAddress">零基协议起始地址。</param>
        /// <param name="quantity">需要读取的连续寄存器数量。</param>
        /// <returns>标准事务唯一终态或立即拒绝结果。</returns>
        private ValueTask<TransactionExecutionResult> ReadRangeCoreAsync(
            ushort startAddress,
            ushort quantity)
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                CurrentSlaveAddress,
                startAddress,
                quantity);
            return commandConsole.ExecuteStandardAsync(
                request,
                CancellationToken.None);
        }

        /// <summary>
        /// 使用当前从站地址执行一项 0x06 普通参数写入。
        /// </summary>
        /// <param name="item">需要修改的普通参数行。</param>
        /// <param name="rawWord">已经由该行寄存器定义验证的原始字。</param>
        /// <returns>标准事务唯一终态或立即拒绝结果。</returns>
        private async ValueTask<TransactionExecutionResult> WriteSingleCoreAsync(
            ParameterItemViewModel item,
            ushort rawWord)
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                CurrentSlaveAddress,
                item.Definition.ProtocolAddress,
                rawWord);
            TransactionExecutionResult result = await commandConsole.ExecuteStandardAsync(
                request,
                CancellationToken.None).ConfigureAwait(true);

            if (IsSuccessful(result))
            {
                item.ApplySnapshotValue(
                    RegisterValueConverter.Decode(item.Definition, rawWord));
            }

            return result;
        }

        /// <summary>
        /// 使用当前从站地址执行一项 0x10 连续普通参数写入。
        /// </summary>
        /// <param name="startAddress">零基协议起始地址。</param>
        /// <param name="rawWords">按地址升序排列的非空原始字。</param>
        /// <returns>标准事务唯一终态或立即拒绝结果。</returns>
        private ValueTask<TransactionExecutionResult> WriteMultipleCoreAsync(
            ushort startAddress,
            ushort[] rawWords)
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                CurrentSlaveAddress,
                startAddress,
                rawWords);
            return commandConsole.ExecuteStandardAsync(
                request,
                CancellationToken.None);
        }

        /// <summary>
        /// 执行地址寄存器独立流程，并仅在完整回显成功后更新本地地址。
        /// </summary>
        /// <returns>配置流程完成后的任务。</returns>
        private async Task ChangeAddressAsync()
        {
            await ExecuteSpecialOperationAsync(
                async () => await specialConfigurationService.ChangeSlaveAddressAsync(
                    checked((byte)TargetSlaveAddress),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(true);
        }

        /// <summary>
        /// 执行波特率寄存器独立流程，并在完整回显后使用新波特率重开串口。
        /// </summary>
        /// <returns>配置和重连流程完成后的任务。</returns>
        private async Task ChangeBaudRateAsync()
        {
            await ExecuteSpecialOperationAsync(
                async () => await specialConfigurationService.ChangeBaudRateAsync(
                    TargetBaudRate,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(true);
        }

        /// <summary>
        /// 在明确二次确认后执行恢复出厂独立流程。
        /// </summary>
        /// <returns>配置和重连流程完成后的任务。</returns>
        private async Task RestoreFactoryAsync()
        {
            await ExecuteSpecialOperationAsync(
                async () => await specialConfigurationService.RestoreFactoryDefaultsAsync(
                    FactoryResetConfirmed,
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(true);
            FactoryResetConfirmed = false;
        }

        /// <summary>
        /// 执行固定 0xFE 查询并在严格匹配成功后采用发现的真实地址。
        /// </summary>
        /// <returns>单设备地址查询完成后的任务。</returns>
        private async Task DiscoverAddressAsync()
        {
            await ExecuteSpecialOperationAsync(
                async () => await specialConfigurationService.DiscoverUnknownAddressAsync(
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(true);
        }

        /// <summary>
        /// 为四类专用流程提供统一忙门、异常显示和本地配置同步。
        /// </summary>
        /// <param name="operation">返回不可变配置结果的异步专用流程。</param>
        /// <returns>专用流程和界面状态更新完成后的任务。</returns>
        private async Task ExecuteSpecialOperationAsync(
            Func<Task<ConfigurationChangeResult>> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            isLocalOperationBusy = true;
            NotifyCommandStates();

            try
            {
                SynchronizeSpecialServiceConfiguration();
                ConfigurationChangeResult result = await operation().ConfigureAwait(true);
                StatusMessage = result.Message;

                if (result.Status == ConfigurationChangeStatus.Succeeded)
                {
                    serialConnection.SlaveAddress = result.LocalConfiguration.SlaveAddress;
                    serialConnection.BaudRate =
                        result.LocalConfiguration.SerialSettings.BaudRate;
                    TargetSlaveAddress = result.LocalConfiguration.SlaveAddress;
                    TargetBaudRate =
                        result.LocalConfiguration.SerialSettings.BaudRate;

                    if (result.Kind is ConfigurationChangeKind.BaudRate or
                        ConfigurationChangeKind.FactoryReset)
                    {
                        serialConnection.IsConnected = true;
                        serialConnection.ConnectionStatus =
                            "已连接 · 等待手动指令";
                    }
                }
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
            finally
            {
                isLocalOperationBusy = false;
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 使用页面级即时忙门执行一项普通读取或写入，防止快速重复点击。
        /// </summary>
        /// <param name="operation">完整校验、发送和状态更新操作。</param>
        /// <returns>操作完成并释放页面忙门后的任务。</returns>
        private async Task ExecutePageOperationAsync(Func<Task> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (isLocalOperationBusy)
            {
                StatusMessage = "当前已有参数操作等待处理。";
                return;
            }

            isLocalOperationBusy = true;
            NotifyCommandStates();

            try
            {
                await operation().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
            finally
            {
                isLocalOperationBusy = false;
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 把当前明确选择且已连接的本地设置同步给专用配置服务，不产生线路写入。
        /// </summary>
        private void SynchronizeSpecialServiceConfiguration()
        {
            specialConfigurationService.SynchronizeLocalConfiguration(
                CurrentSlaveAddress,
                serialConnection.CreateSerialSettings());
        }

        /// <summary>
        /// 使用当前设备快照刷新统一寄存器表，不改变没有快照项的用户输入。
        /// </summary>
        private void RefreshFromSnapshot()
        {
            IReadOnlyDictionary<ushort, RegisterValue> values =
                operationService.DeviceSnapshot.Values;

            foreach (ParameterItemViewModel item in Registers)
            {
                if (values.TryGetValue(
                    item.Definition.ProtocolAddress,
                    out RegisterValue? value))
                {
                    item.ApplySnapshotValue(value);
                }
            }
        }

        /// <summary>
        /// 将成功写入的用户编辑值应用到当前表格显示，不伪造未确认的快照范围。
        /// </summary>
        /// <param name="editedWords">已经被成功响应确认的协议地址和原始字。</param>
        private void ApplySuccessfulWriteValues(
            IReadOnlyDictionary<ushort, ushort> editedWords)
        {
            foreach (ParameterItemViewModel item in Registers)
            {
                if (editedWords.TryGetValue(
                    item.Definition.ProtocolAddress,
                    out ushort rawWord))
                {
                    item.ApplySnapshotValue(
                        RegisterValueConverter.Decode(item.Definition, rawWord));
                }
            }
        }

        /// <summary>
        /// 获取当前按协议地址升序排列的选中寄存器行。
        /// </summary>
        /// <returns>新的选中项数组；没有选择时为空。</returns>
        private ParameterItemViewModel[] GetSelectedItems()
        {
            return Registers
                .Where(item => item.IsSelected)
                .OrderBy(item => item.Definition.ProtocolAddress)
                .ToArray();
        }

        /// <summary>
        /// 判断事务是否被接受并以严格成功标准响应结束。
        /// </summary>
        /// <param name="result">协调器返回的提交结果。</param>
        /// <returns>存在成功终态时返回真。</returns>
        private static bool IsSuccessful(TransactionExecutionResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            return result.Outcome?.State == TransactionCompletionState.Succeeded;
        }

        /// <summary>
        /// 获取当前普通从站地址并执行 1 至 64 范围校验。
        /// </summary>
        private byte CurrentSlaveAddress
        {
            get
            {
                if (serialConnection.SlaveAddress is < 1 or > 64)
                {
                    throw new InvalidOperationException(
                        "普通 Modbus 从站地址必须位于 1 至 64。");
                }

                return checked((byte)serialConnection.SlaveAddress);
            }
        }

        /// <summary>
        /// 获取是否允许执行普通读取或写入。
        /// </summary>
        /// <returns>已连接且共享事务门和本页操作门均空闲时返回真。</returns>
        private bool CanExecuteStandardOperation()
        {
            return serialConnection.IsConnected &&
                !applicationOperationGate.IsIapActive &&
                !serialConnection.IsTransactionBusy &&
                !isLocalOperationBusy;
        }

        /// <summary>
        /// 获取是否存在至少一个选中项且允许执行批量读取。
        /// </summary>
        /// <returns>存在任意选中项且普通操作可执行时返回真。</returns>
        private bool CanExecuteReadSelected()
        {
            return Registers.Any(item => item.IsSelected) &&
                CanExecuteStandardOperation();
        }

        /// <summary>
        /// 获取当前选择是否允许执行普通寄存器修改。
        /// </summary>
        /// <returns>所有选中项属于同一普通可写组且普通操作可执行时返回真。</returns>
        private bool CanExecuteModifySelected()
        {
            ParameterItemViewModel[] selected = GetSelectedItems();
            return IsValidWritableSelection(selected) &&
                CanExecuteStandardOperation();
        }

        /// <summary>
        /// 判断一组选中项是否全部属于同一个普通可写寄存器组。
        /// </summary>
        /// <param name="selected">按协议地址升序排列的选中寄存器行。</param>
        /// <returns>选择非空、全部普通可写且没有跨组时返回真。</returns>
        private static bool IsValidWritableSelection(
            IReadOnlyList<ParameterItemViewModel> selected)
        {
            ArgumentNullException.ThrowIfNull(selected);

            if (selected.Count == 0 ||
                selected.Any(item => !item.IsOrdinaryWritable))
            {
                return false;
            }

            bool usesAlarmGroup =
                selected[0].Definition.DocumentAddress <= 40023;
            return selected.All(
                item =>
                    (item.Definition.DocumentAddress <= 40023) ==
                    usesAlarmGroup);
        }

        /// <summary>
        /// 获取是否允许读取指定单个寄存器。
        /// </summary>
        /// <param name="item">命令参数中的统一寄存器表行。</param>
        /// <returns>行存在且普通操作可执行时返回真。</returns>
        private bool CanReadItem(ParameterItemViewModel? item)
        {
            return item is not null && CanExecuteStandardOperation();
        }

        /// <summary>
        /// 获取是否允许修改指定单个普通参数。
        /// </summary>
        /// <param name="item">命令参数中的统一寄存器表行。</param>
        /// <returns>行属于普通可写组且普通操作可执行时返回真。</returns>
        private bool CanModifyItem(ParameterItemViewModel? item)
        {
            return item is not null &&
                item.IsOrdinaryWritable &&
                CanExecuteStandardOperation();
        }

        /// <summary>
        /// 获取是否允许启动地址、波特率或 0xFE 专用流程。
        /// </summary>
        /// <returns>普通操作可执行且专用服务自身未忙时返回真。</returns>
        private bool CanExecuteSpecialOperation()
        {
            return CanExecuteStandardOperation() &&
                !specialConfigurationService.IsOperationBusy;
        }

        /// <summary>
        /// 获取是否已完成二次确认且允许恢复出厂。
        /// </summary>
        /// <returns>专用流程可执行且确认框已勾选时返回真。</returns>
        private bool CanRestoreFactory()
        {
            return FactoryResetConfirmed && CanExecuteSpecialOperation();
        }

        /// <summary>
        /// 点击寄存器名称时清除旧选择并仅选中当前行。
        /// </summary>
        /// <param name="item">用户点击名称的统一寄存器表行。</param>
        private void SelectItem(ParameterItemViewModel? item)
        {
            if (item is null)
            {
                return;
            }

            isSynchronizingSelection = true;

            try
            {
                foreach (ParameterItemViewModel register in Registers)
                {
                    register.IsSelected = ReferenceEquals(register, item);
                }
            }
            finally
            {
                isSynchronizingSelection = false;
            }

            UpdateSelectionSummary();
            NotifyCommandStates();
        }

        /// <summary>
        /// 根据当前真实勾选项刷新数量和修改资格说明。
        /// </summary>
        private void UpdateSelectionSummary()
        {
            ParameterItemViewModel[] selected = GetSelectedItems();

            if (selected.Length == 0)
            {
                SelectionSummary =
                    "未选择寄存器；勾选后可读取，普通可写项可修改。";
                return;
            }

            if (selected.Any(item => !item.IsOrdinaryWritable))
            {
                SelectionSummary =
                    $"已选择 {selected.Length} 项 · 可读取；包含只读或专用项，不能修改。";
                return;
            }

            if (!IsValidWritableSelection(selected))
            {
                SelectionSummary =
                    $"已选择 {selected.Length} 项 · 可读取；修改不能跨报警参数组与补偿参数组。";
                return;
            }

            SelectionSummary =
                $"已选择 {selected.Length} 项 · 可读取、可修改。";
        }

        /// <summary>
        /// 接收快照更新并在界面线程刷新参数当前值。
        /// </summary>
        private void HandleSnapshotUpdated()
        {
            dispatcher.Post(RefreshFromSnapshot);
        }

        /// <summary>
        /// 接收寄存器选择变化并刷新批量读取和修改命令。
        /// </summary>
        /// <param name="sender">发生属性变化的统一寄存器表行。</param>
        /// <param name="eventArgs">发生变化的属性名称。</param>
        private void HandleRegisterItemPropertyChanged(
            object? sender,
            PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(ParameterItemViewModel.IsSelected))
            {
                if (isSynchronizingSelection)
                {
                    return;
                }

                UpdateSelectionSummary();
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 在连接或事务忙状态变化后刷新全部命令。
        /// </summary>
        /// <param name="sender">发布属性变化的串口 ViewModel。</param>
        /// <param name="eventArgs">发生变化的属性名称。</param>
        private void HandleSerialConnectionPropertyChanged(
            object? sender,
            PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName is nameof(SerialConnectionViewModel.IsConnected) or
                nameof(SerialConnectionViewModel.IsTransactionBusy))
            {
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 在 IAP 取得或释放应用操作门后刷新参数命令和暂停提示。
        /// </summary>
        /// <param name="isActive">固件升级是否正在独占通信操作。</param>
        private void HandleIapActivityChanged(bool isActive)
        {
            dispatcher.Post(
                () =>
                {
                    if (isActive)
                    {
                        StatusMessage = "固件升级期间参数读取、修改和专用流程已暂停。";
                    }

                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 通知参数页全部命令重新计算可执行性。
        /// </summary>
        private void NotifyCommandStates()
        {
            ReadAllCommand.NotifyCanExecuteChanged();
            ReadSelectedCommand.NotifyCanExecuteChanged();
            ModifySelectedCommand.NotifyCanExecuteChanged();
            ReadItemCommand.NotifyCanExecuteChanged();
            ModifyItemCommand.NotifyCanExecuteChanged();
            ChangeAddressCommand.NotifyCanExecuteChanged();
            ChangeBaudRateCommand.NotifyCanExecuteChanged();
            RestoreFactoryCommand.NotifyCanExecuteChanged();
            DiscoverAddressCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 在恢复出厂确认状态变化后刷新危险操作命令。
        /// </summary>
        /// <param name="value">新的二次确认状态。</param>
        partial void OnFactoryResetConfirmedChanged(bool value)
        {
            NotifyCommandStates();
        }
    }
}
