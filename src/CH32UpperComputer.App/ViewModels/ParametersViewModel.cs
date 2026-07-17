using CH32UpperComputer.App.Services;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Core.Registers;
using CH32UpperComputer.Infrastructure.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System.Collections.ObjectModel;
using System.ComponentModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 管理普通可写参数组、手动读取和地址/波特率/恢复出厂独立安全流程。
    /// </summary>
    public sealed partial class ParametersViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 复用标准请求、日志、统计和快照路径的收发 ViewModel。
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
        /// 提供成功 0x03 更新通知和设备快照的统一操作服务。
        /// </summary>
        private readonly ModbusOperationService operationService;

        /// <summary>
        /// 将后台事务完成事件切换到界面线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 当前页面自身是否正在等待一次专用配置流程。
        /// </summary>
        private bool isLocalOperationBusy;

        /// <summary>
        /// 指示 ViewModel 已释放并停止接收事件。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 当前选择的连续参数组。
        /// </summary>
        [ObservableProperty]
        private ParameterGroupViewModel? selectedGroup;

        /// <summary>
        /// 参数页最近一次校验或事务结果说明。
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "进入页面不会自动读取，请手动选择读取范围。";

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
        private string unknownAddressWarning = SpecialConfigurationService.UnknownAddressRiskWarning;

        /// <summary>
        /// 初始化参数组及全部手动读取、保存和特殊配置命令。
        /// </summary>
        /// <param name="commandConsole">复用标准事务执行路径的收发 ViewModel。</param>
        /// <param name="serialConnection">提供当前连接参数和共享忙状态的串口 ViewModel。</param>
        /// <param name="specialConfigurationService">执行独立安全配置流程的服务。</param>
        /// <param name="operationService">提供设备快照和成功更新通知的服务。</param>
        /// <param name="dispatcher">负责把后台事件投递到界面线程的调度器。</param>
        public ParametersViewModel(
            CommandConsoleViewModel commandConsole,
            SerialConnectionViewModel serialConnection,
            SpecialConfigurationService specialConfigurationService,
            ModbusOperationService operationService,
            IUiDispatcher dispatcher)
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
            ParameterGroups = new ObservableCollection<ParameterGroupViewModel>(CreateParameterGroups());
            selectedGroup = ParameterGroups.FirstOrDefault();
            targetSlaveAddress = serialConnection.SlaveAddress;
            targetBaudRate = serialConnection.BaudRate;
            ReadAllCommand = new AsyncRelayCommand(ReadAllAsync, CanExecuteStandardOperation);
            ReadSelectedGroupCommand = new AsyncRelayCommand(ReadSelectedGroupAsync, CanExecuteSelectedGroupOperation);
            SaveSelectedGroupCommand = new AsyncRelayCommand(SaveSelectedGroupAsync, CanExecuteSelectedGroupOperation);
            ChangeAddressCommand = new AsyncRelayCommand(ChangeAddressAsync, CanExecuteSpecialOperation);
            ChangeBaudRateCommand = new AsyncRelayCommand(ChangeBaudRateAsync, CanExecuteSpecialOperation);
            RestoreFactoryCommand = new AsyncRelayCommand(RestoreFactoryAsync, CanRestoreFactory);
            DiscoverAddressCommand = new AsyncRelayCommand(DiscoverAddressAsync, CanExecuteSpecialOperation);
            operationService.SnapshotUpdated += HandleSnapshotUpdated;
            serialConnection.PropertyChanged += HandleSerialConnectionPropertyChanged;
            RefreshFromSnapshot();
        }

        /// <summary>
        /// 获取两个互不排队的连续普通参数组。
        /// </summary>
        public ObservableCollection<ParameterGroupViewModel> ParameterGroups { get; }

        /// <summary>
        /// 获取手动读取全部 40001 至 40036 的命令。
        /// </summary>
        public IAsyncRelayCommand ReadAllCommand { get; }

        /// <summary>
        /// 获取手动读取当前连续参数组的命令。
        /// </summary>
        public IAsyncRelayCommand ReadSelectedGroupCommand { get; }

        /// <summary>
        /// 获取完整预校验当前组后以单个 0x10 写入的命令。
        /// </summary>
        public IAsyncRelayCommand SaveSelectedGroupCommand { get; }

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
        /// 取消快照和串口状态事件订阅。
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
        }

        /// <summary>
        /// 手动读取完整设备寄存器表，不在进入页面时自动执行。
        /// </summary>
        /// <returns>标准 0x03 事务完成后的任务。</returns>
        private async Task ReadAllAsync()
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                CurrentSlaveAddress,
                0x0000,
                36);
            TransactionExecutionResult result = await commandConsole.ExecuteStandardAsync(
                request,
                CancellationToken.None).ConfigureAwait(true);
            StatusMessage = result.Message;
        }

        /// <summary>
        /// 手动读取当前选择的连续参数组。
        /// </summary>
        /// <returns>标准 0x03 事务完成后的任务。</returns>
        private async Task ReadSelectedGroupAsync()
        {
            ParameterGroupViewModel group = SelectedGroup!;
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                CurrentSlaveAddress,
                group.StartAddress,
                group.Quantity);
            TransactionExecutionResult result = await commandConsole.ExecuteStandardAsync(
                request,
                CancellationToken.None).ConfigureAwait(true);
            StatusMessage = result.Message;
        }

        /// <summary>
        /// 完整预校验当前组全部值，任一无效时保持零写入，全部合法才构建一个 0x10。
        /// </summary>
        /// <returns>校验失败或单个标准写事务完成后的任务。</returns>
        private async Task SaveSelectedGroupAsync()
        {
            ParameterGroupViewModel group = SelectedGroup!;
            ushort[] rawWords = new ushort[group.Items.Count];
            List<string> errors = new();

            for (int index = 0; index < group.Items.Count; index++)
            {
                if (!group.Items[index].TryGetRawWord(out rawWords[index]))
                {
                    errors.Add($"{group.Items[index].Definition.DocumentAddress}：{group.Items[index].ValidationMessage}");
                }
            }

            if (errors.Count > 0)
            {
                StatusMessage = $"整组预校验失败，未写入任何数据：{string.Join("；", errors)}";
                return;
            }

            ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                CurrentSlaveAddress,
                group.StartAddress,
                rawWords);
            TransactionExecutionResult result = await commandConsole.ExecuteStandardAsync(
                request,
                CancellationToken.None).ConfigureAwait(true);
            StatusMessage = result.Message;
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
                    serialConnection.BaudRate = result.LocalConfiguration.SerialSettings.BaudRate;
                    TargetSlaveAddress = result.LocalConfiguration.SlaveAddress;
                    TargetBaudRate = result.LocalConfiguration.SerialSettings.BaudRate;

                    if (result.Kind is ConfigurationChangeKind.BaudRate or ConfigurationChangeKind.FactoryReset)
                    {
                        serialConnection.IsConnected = true;
                        serialConnection.ConnectionStatus = "已连接 · 等待手动指令";
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
        /// 把当前明确选择且已连接的本地设置同步给专用配置服务，不产生线路写入。
        /// </summary>
        private void SynchronizeSpecialServiceConfiguration()
        {
            specialConfigurationService.SynchronizeLocalConfiguration(
                CurrentSlaveAddress,
                serialConnection.CreateSerialSettings());
        }

        /// <summary>
        /// 使用当前设备快照刷新参数页已读取值，不改变无快照参数的用户输入。
        /// </summary>
        private void RefreshFromSnapshot()
        {
            IReadOnlyDictionary<ushort, RegisterValue> values = operationService.DeviceSnapshot.Values;

            foreach (ParameterItemViewModel item in ParameterGroups.SelectMany(group => group.Items))
            {
                if (values.TryGetValue(item.Definition.ProtocolAddress, out RegisterValue? value))
                {
                    item.ApplySnapshotValue(value);
                }
            }
        }

        /// <summary>
        /// 创建报警阈值/回差/使能和补偿两个连续普通写组。
        /// </summary>
        /// <returns>不包含地址、波特率或恢复出厂的固定参数组数组。</returns>
        private static ParameterGroupViewModel[] CreateParameterGroups()
        {
            ParameterItemViewModel[] alarmItems = DeviceRegisterMap.All
                .Where(definition => definition.DocumentAddress is >= 40011 and <= 40023)
                .Select(definition => new ParameterItemViewModel(definition))
                .ToArray();
            ParameterItemViewModel[] compensationItems = DeviceRegisterMap.All
                .Where(definition => definition.DocumentAddress is >= 40030 and <= 40035)
                .Select(definition => new ParameterItemViewModel(definition))
                .ToArray();
            return
            [
                new ParameterGroupViewModel("报警阈值、回差与使能", alarmItems),
                new ParameterGroupViewModel("传感器补偿", compensationItems),
            ];
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
                    throw new InvalidOperationException("普通 Modbus 从站地址必须位于 1 至 64。");
                }

                return checked((byte)serialConnection.SlaveAddress);
            }
        }

        /// <summary>
        /// 获取是否允许执行普通读取或写入。
        /// </summary>
        /// <returns>已连接且共享事务门和本页专用门均空闲时返回真。</returns>
        private bool CanExecuteStandardOperation()
        {
            return serialConnection.IsConnected &&
                !serialConnection.IsTransactionBusy &&
                !isLocalOperationBusy;
        }

        /// <summary>
        /// 获取是否允许对当前选择的连续参数组执行操作。
        /// </summary>
        /// <returns>已选择参数组且普通操作可执行时返回真。</returns>
        private bool CanExecuteSelectedGroupOperation()
        {
            return SelectedGroup is not null && CanExecuteStandardOperation();
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
        /// 接收快照更新并在界面线程刷新参数当前值。
        /// </summary>
        private void HandleSnapshotUpdated()
        {
            dispatcher.Post(RefreshFromSnapshot);
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
        /// 通知参数页全部命令重新计算可执行性。
        /// </summary>
        private void NotifyCommandStates()
        {
            ReadAllCommand.NotifyCanExecuteChanged();
            ReadSelectedGroupCommand.NotifyCanExecuteChanged();
            SaveSelectedGroupCommand.NotifyCanExecuteChanged();
            ChangeAddressCommand.NotifyCanExecuteChanged();
            ChangeBaudRateCommand.NotifyCanExecuteChanged();
            RestoreFactoryCommand.NotifyCanExecuteChanged();
            DiscoverAddressCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 在参数组选择变化后刷新组操作命令。
        /// </summary>
        /// <param name="value">新选择的连续参数组。</param>
        partial void OnSelectedGroupChanged(ParameterGroupViewModel? value)
        {
            NotifyCommandStates();
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
