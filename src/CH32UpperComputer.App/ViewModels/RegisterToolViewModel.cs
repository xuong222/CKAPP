using CH32UpperComputer.App.Services;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Core.Registers;
using CH32UpperComputer.Infrastructure.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 提供标准 0x03、0x06、0x10、0xFE 和原始十六进制调试能力的专家寄存器工具。
    /// </summary>
    public sealed partial class RegisterToolViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 复用统一标准或原始事务执行路径的收发 ViewModel。
        /// </summary>
        private readonly CommandConsoleViewModel commandConsole;

        /// <summary>
        /// 提供当前地址、连接和共享忙状态的串口 ViewModel。
        /// </summary>
        private readonly SerialConnectionViewModel serialConnection;

        /// <summary>
        /// 执行固定 0xFE 安全查询的专用配置服务。
        /// </summary>
        private readonly SpecialConfigurationService specialConfigurationService;

        /// <summary>
        /// 发布结构化事务结果的统一操作服务。
        /// </summary>
        private readonly ModbusOperationService operationService;

        /// <summary>
        /// 将后台事务完成事件切换到界面线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 当前是否正在等待一次 0xFE 专用流程。
        /// </summary>
        private bool isLocalOperationBusy;

        /// <summary>
        /// 指示 ViewModel 已释放并停止接收事件。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 标准请求使用的四万区起始地址。
        /// </summary>
        [ObservableProperty]
        private int documentAddress = 40003;

        /// <summary>
        /// 0x03 标准读取数量。
        /// </summary>
        [ObservableProperty]
        private int quantity = 8;

        /// <summary>
        /// 0x06 写单寄存器使用的原始值文本。
        /// </summary>
        [ObservableProperty]
        private string singleRawValueText = "0";

        /// <summary>
        /// 0x10 写多个寄存器使用的原始字列表。
        /// </summary>
        [ObservableProperty]
        private string multipleRawValuesText = "0, 0";

        /// <summary>
        /// 原始调试模式直接发送的十六进制帧文本。
        /// </summary>
        [ObservableProperty]
        private string rawFrameText = string.Empty;

        /// <summary>
        /// 专家工具最近一次构建、校验或事务结果说明。
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "标准地址按 40001 起算，协议地址由工具自动转换。";

        /// <summary>
        /// 初始化专家寄存器工具和固定 0xFE 风险说明。
        /// </summary>
        /// <param name="commandConsole">提供统一事务执行和原始输入路径的收发 ViewModel。</param>
        /// <param name="serialConnection">提供当前连接、地址和共享忙状态的串口 ViewModel。</param>
        /// <param name="specialConfigurationService">执行固定 0xFE 单设备查询的服务。</param>
        /// <param name="operationService">发布结构化事务结果的统一操作服务。</param>
        /// <param name="dispatcher">负责把后台事件投递到界面线程的调度器。</param>
        public RegisterToolViewModel(
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
            ResultRows = new ObservableCollection<RegisterResultRowViewModel>();
            ReadCommand = new AsyncRelayCommand(ReadAsync, CanExecuteStandard);
            WriteSingleCommand = new AsyncRelayCommand(WriteSingleAsync, CanExecuteStandard);
            WriteMultipleCommand = new AsyncRelayCommand(WriteMultipleAsync, CanExecuteStandard);
            SendRawCommand = new AsyncRelayCommand(SendRawAsync, CanExecuteRaw);
            DiscoverUnknownAddressCommand = new AsyncRelayCommand(DiscoverUnknownAddressAsync, CanExecuteStandard);
            operationService.OperationRecorded += HandleOperationRecorded;
            serialConnection.PropertyChanged += HandleSerialConnectionPropertyChanged;
        }

        /// <summary>
        /// 获取标准读取结果的 Signed、Unsigned、Hex 和语义解释行。
        /// </summary>
        public ObservableCollection<RegisterResultRowViewModel> ResultRows { get; }

        /// <summary>
        /// 获取必须完整显示在 0xFE 按钮附近和确认区的风险文案。
        /// </summary>
        public string UnknownAddressWarning => SpecialConfigurationService.UnknownAddressRiskWarning;

        /// <summary>
        /// 获取构建并发送标准 0x03 请求的命令。
        /// </summary>
        public IAsyncRelayCommand ReadCommand { get; }

        /// <summary>
        /// 获取构建并发送标准 0x06 请求的命令。
        /// </summary>
        public IAsyncRelayCommand WriteSingleCommand { get; }

        /// <summary>
        /// 获取构建并发送标准 0x10 请求的命令。
        /// </summary>
        public IAsyncRelayCommand WriteMultipleCommand { get; }

        /// <summary>
        /// 获取按原始调试规则发送十六进制帧的命令。
        /// </summary>
        public IAsyncRelayCommand SendRawCommand { get; }

        /// <summary>
        /// 获取固定 0xFE 单设备查询命令。
        /// </summary>
        public IAsyncRelayCommand DiscoverUnknownAddressCommand { get; }

        /// <summary>
        /// 取消事务和串口状态事件订阅。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            operationService.OperationRecorded -= HandleOperationRecorded;
            serialConnection.PropertyChanged -= HandleSerialConnectionPropertyChanged;
        }

        /// <summary>
        /// 构建文档地址到协议地址转换正确的标准 0x03 请求。
        /// </summary>
        /// <returns>读取事务完成后的任务。</returns>
        private async Task ReadAsync()
        {
            try
            {
                ushort startAddress = ConvertDocumentAddress(DocumentAddress);
                ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(
                    CurrentSlaveAddress,
                    startAddress,
                    checked((ushort)Quantity));
                TransactionExecutionResult result = await commandConsole.ExecuteStandardAsync(
                    request,
                    CancellationToken.None).ConfigureAwait(true);
                StatusMessage = result.Message;
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
        }

        /// <summary>
        /// 构建以原始 16 位字为数据的标准 0x06 请求。
        /// </summary>
        /// <returns>写单寄存器事务完成后的任务。</returns>
        private async Task WriteSingleAsync()
        {
            try
            {
                ushort rawWord = ParseRawWord(SingleRawValueText);
                ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                    CurrentSlaveAddress,
                    ConvertDocumentAddress(DocumentAddress),
                    rawWord);
                TransactionExecutionResult result = await commandConsole.ExecuteStandardAsync(
                    request,
                    CancellationToken.None).ConfigureAwait(true);
                StatusMessage = result.Message;
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
        }

        /// <summary>
        /// 构建以逗号或空白分隔原始字为数据的标准 0x10 请求。
        /// </summary>
        /// <returns>写多个连续寄存器事务完成后的任务。</returns>
        private async Task WriteMultipleAsync()
        {
            try
            {
                ushort[] rawWords = ParseRawWords(MultipleRawValuesText);
                ModbusRequest request = ModbusRequestFactory.CreateWriteMultipleRegisters(
                    CurrentSlaveAddress,
                    ConvertDocumentAddress(DocumentAddress),
                    rawWords);
                TransactionExecutionResult result = await commandConsole.ExecuteStandardAsync(
                    request,
                    CancellationToken.None).ConfigureAwait(true);
                StatusMessage = result.Message;
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
        }

        /// <summary>
        /// 把原始文本交给收发框同一解析与 RawDebug 路径，不触发地址或波特率自动切换。
        /// </summary>
        /// <returns>原始调试事务完成后的任务。</returns>
        private async Task SendRawAsync()
        {
            try
            {
                TransactionExecutionResult result = await commandConsole.ExecuteRawTextAsync(
                    RawFrameText,
                    CancellationToken.None).ConfigureAwait(true);
                StatusMessage = result.Message;
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }
        }

        /// <summary>
        /// 显示固定风险并通过专用流程执行 0xFE 查询。
        /// </summary>
        /// <returns>地址查询完成后的任务。</returns>
        private async Task DiscoverUnknownAddressAsync()
        {
            isLocalOperationBusy = true;
            NotifyCommandStates();

            try
            {
                specialConfigurationService.SynchronizeLocalConfiguration(
                    CurrentSlaveAddress,
                    serialConnection.CreateSerialSettings());
                ConfigurationChangeResult result = await specialConfigurationService
                    .DiscoverUnknownAddressAsync(CancellationToken.None).ConfigureAwait(true);
                StatusMessage = result.Message;

                if (result.Status == ConfigurationChangeStatus.Succeeded && result.DiscoveredSlaveAddress.HasValue)
                {
                    serialConnection.SlaveAddress = result.DiscoveredSlaveAddress.Value;
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
        /// 将一次成功 0x03 响应转换为固定三栏原始视图和地址独立语义解释。
        /// </summary>
        /// <param name="transactionRequest">统一操作服务记录的原始标准或原始事务请求。</param>
        /// <param name="result">统一操作服务发布的拒绝或终态结果。</param>
        private void HandleOperationRecorded(
            TransactionRequest transactionRequest,
            TransactionExecutionResult result)
        {
            if (!result.IsAccepted ||
                result.Outcome?.State != TransactionCompletionState.Succeeded ||
                result.Outcome.Response is null ||
                transactionRequest.StandardRequest?.FunctionCode != ModbusFunctionCode.ReadHoldingRegisters)
            {
                return;
            }

            ModbusRequest request = transactionRequest.StandardRequest;
            ushort[] words = result.Outcome.Response.Registers.ToArray();
            dispatcher.Post(
                () =>
                {
                    ResultRows.Clear();

                    for (int index = 0; index < words.Length; index++)
                    {
                        ushort protocolAddress = checked((ushort)(request.StartAddress + index));
                        int documentAddress = 40001 + protocolAddress;
                        string semanticValue = string.Empty;

                        if (DeviceRegisterMap.TryGetByProtocolAddress(protocolAddress, out RegisterDefinition? definition) &&
                            definition is not null)
                        {
                            RegisterValue value = RegisterValueConverter.Decode(definition, words[index]);
                            semanticValue = $"{definition.Name}：{value.FormattedValue} {definition.Unit}".Trim();
                        }

                        ResultRows.Add(
                            new RegisterResultRowViewModel(
                                documentAddress,
                                protocolAddress,
                                words[index],
                                semanticValue));
                    }
                });
        }

        /// <summary>
        /// 将 40001 起算的文档地址转换为 Modbus PDU 零基地址。
        /// </summary>
        /// <param name="documentAddress">用户输入的四万区地址。</param>
        /// <returns>零基 16 位协议地址。</returns>
        private static ushort ConvertDocumentAddress(int documentAddress)
        {
            int protocolAddress = documentAddress - 40001;

            if (protocolAddress is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(documentAddress),
                    documentAddress,
                    "文档地址必须从 40001 起算且不能超过协议地址上限。");
            }

            return checked((ushort)protocolAddress);
        }

        /// <summary>
        /// 解析一个十进制或 0x 前缀十六进制原始 16 位字。
        /// </summary>
        /// <param name="text">用户输入的原始值文本。</param>
        /// <returns>范围为 0 至 65535 的原始线路字。</returns>
        private static ushort ParseRawWord(string text)
        {
            string normalized = text?.Trim() ?? string.Empty;
            bool isHex = normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            ReadOnlySpan<char> digits = isHex ? normalized.AsSpan(2) : normalized.AsSpan();
            NumberStyles styles = isHex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;

            if (!ushort.TryParse(digits, styles, CultureInfo.InvariantCulture, out ushort value))
            {
                throw new FormatException("原始值必须是 0 至 65535，或 0x0000 至 0xFFFF。");
            }

            return value;
        }

        /// <summary>
        /// 解析逗号、分号或空白分隔的一至一百二十三个原始线路字。
        /// </summary>
        /// <param name="text">包含多个十进制或十六进制原始字的文本。</param>
        /// <returns>保持用户输入顺序的新原始字数组。</returns>
        private static ushort[] ParseRawWords(string text)
        {
            string[] tokens = (text ?? string.Empty).Split(
                [',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (tokens.Length is < 1 or > 123)
            {
                throw new FormatException("0x10 必须输入 1 至 123 个原始寄存器值。");
            }

            return tokens.Select(ParseRawWord).ToArray();
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
        /// 获取当前是否允许执行标准或 0xFE 请求。
        /// </summary>
        /// <returns>已连接、共享活动门和本页活动门均空闲时返回真。</returns>
        private bool CanExecuteStandard()
        {
            return serialConnection.IsConnected &&
                !serialConnection.IsTransactionBusy &&
                !isLocalOperationBusy;
        }

        /// <summary>
        /// 获取当前是否允许发送非空原始十六进制输入。
        /// </summary>
        /// <returns>标准请求可执行且原始输入非空时返回真。</returns>
        private bool CanExecuteRaw()
        {
            return CanExecuteStandard() && !string.IsNullOrWhiteSpace(RawFrameText);
        }

        /// <summary>
        /// 在连接或事务忙状态变化后刷新专家工具命令。
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
        /// 通知专家工具全部命令重新计算可执行性。
        /// </summary>
        private void NotifyCommandStates()
        {
            ReadCommand.NotifyCanExecuteChanged();
            WriteSingleCommand.NotifyCanExecuteChanged();
            WriteMultipleCommand.NotifyCanExecuteChanged();
            SendRawCommand.NotifyCanExecuteChanged();
            DiscoverUnknownAddressCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 原始输入改变时刷新原始发送命令。
        /// </summary>
        /// <param name="value">新的原始帧文本。</param>
        partial void OnRawFrameTextChanged(string value)
        {
            SendRawCommand.NotifyCanExecuteChanged();
        }
    }
}
