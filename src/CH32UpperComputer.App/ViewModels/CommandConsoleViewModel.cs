using CH32UpperComputer.App.Services;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Infrastructure.Transactions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using System.Collections.ObjectModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 管理紧凑收发区的十六进制输入、标准/原始识别、常用指令和手动开启的定时发送。
    /// </summary>
    public sealed partial class CommandConsoleViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 统一执行事务、更新快照、日志和统计的应用服务。
        /// </summary>
        private readonly ModbusOperationService operationService;

        /// <summary>
        /// 所有手动和定时请求共用的无积压定时发送服务。
        /// </summary>
        private readonly PeriodicSendService periodicSendService;

        /// <summary>
        /// 提供当前地址、超时和连接状态的串口 ViewModel。
        /// </summary>
        private readonly SerialConnectionViewModel serialConnection;

        /// <summary>
        /// 将后台事务和定时事件切换到 WPF 界面线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 指示 ViewModel 已释放并取消后台事件订阅。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 当前十六进制指令输入文本。
        /// </summary>
        [ObservableProperty]
        private string inputText;

        /// <summary>
        /// 指示发送前是否在缺少有效尾随 CRC 时自动补充 CRC16。
        /// </summary>
        [ObservableProperty]
        private bool autoAppendCrc;

        /// <summary>
        /// 当前手动选择的定时发送间隔毫秒数。
        /// </summary>
        [ObservableProperty]
        private int periodicIntervalMilliseconds;

        /// <summary>
        /// 当前定时发送是否由用户本次会话手动开启。
        /// </summary>
        [ObservableProperty]
        private bool isPeriodicRunning;

        /// <summary>
        /// 当前是否存在一个活动事务。
        /// </summary>
        [ObservableProperty]
        private bool isTransactionBusy;

        /// <summary>
        /// 当前输入解析、提交或事务终态说明。
        /// </summary>
        [ObservableProperty]
        private string statusMessage = "连接后默认等待手动发送指令";

        /// <summary>
        /// 当前被接受请求总数。
        /// </summary>
        [ObservableProperty]
        private long sendCount;

        /// <summary>
        /// 当前成功请求总数。
        /// </summary>
        [ObservableProperty]
        private long successCount;

        /// <summary>
        /// 当前失败或超时请求总数。
        /// </summary>
        [ObservableProperty]
        private long failureCount;

        /// <summary>
        /// 最近一个被接受请求的发送时间文本。
        /// </summary>
        [ObservableProperty]
        private string lastSendTimeText = "--";

        /// <summary>
        /// 最近一个被接受请求的响应耗时文本。
        /// </summary>
        [ObservableProperty]
        private string lastResponseDurationText = "--";

        /// <summary>
        /// 初始化收发区并生成当前地址对应的默认读取指令。
        /// </summary>
        /// <param name="operationService">统一执行手动事务并维护日志、统计和快照的服务。</param>
        /// <param name="periodicSendService">默认关闭且无积压的定时发送服务。</param>
        /// <param name="serialConnection">提供当前串口参数、地址和连接状态的 ViewModel。</param>
        /// <param name="dispatcher">负责把后台事件投递到界面线程的调度器。</param>
        /// <param name="settings">提供 CRC 偏好、上次输入和定时间隔的安全设置。</param>
        public CommandConsoleViewModel(
            ModbusOperationService operationService,
            PeriodicSendService periodicSendService,
            SerialConnectionViewModel serialConnection,
            IUiDispatcher dispatcher,
            AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(operationService);
            ArgumentNullException.ThrowIfNull(periodicSendService);
            ArgumentNullException.ThrowIfNull(serialConnection);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(settings);
            this.operationService = operationService;
            this.periodicSendService = periodicSendService;
            this.serialConnection = serialConnection;
            this.dispatcher = dispatcher;
            AppSettings safeSettings = settings.CreateValidatedCopy();
            autoAppendCrc = safeSettings.AutoAppendCrc;
            periodicIntervalMilliseconds = safeSettings.PeriodicIntervalMilliseconds;
            inputText = string.IsNullOrWhiteSpace(safeSettings.LastCommand)
                ? FormatRequest(CreateMonitorReadRequest())
                : safeSettings.LastCommand;
            SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
            StartPeriodicCommand = new RelayCommand(StartPeriodic, CanStartPeriodic);
            StopPeriodicCommand = new RelayCommand(StopPeriodic, CanStopPeriodic);
            CommonCommands = new ObservableCollection<CommonCommandItemViewModel>(CreateCommonCommands());
            operationService.MetricsChanged += HandleMetricsChanged;
            operationService.OperationCompleted += HandleOperationCompleted;
            periodicSendService.RunningChanged += HandlePeriodicRunningChanged;
            periodicSendService.AttemptCompleted += HandlePeriodicAttemptCompleted;
            serialConnection.PropertyChanged += HandleSerialConnectionPropertyChanged;
        }

        /// <summary>
        /// 获取收发区右侧按单列显示的常用指令集合。
        /// </summary>
        public ObservableCollection<CommonCommandItemViewModel> CommonCommands { get; }

        /// <summary>
        /// 获取手动发送当前输入指令的异步命令。
        /// </summary>
        public IAsyncRelayCommand SendCommand { get; }

        /// <summary>
        /// 获取使用当前输入和间隔手动开启定时发送的命令。
        /// </summary>
        public IRelayCommand StartPeriodicCommand { get; }

        /// <summary>
        /// 获取立即停止定时调度且不安排新写入的命令。
        /// </summary>
        public IRelayCommand StopPeriodicCommand { get; }

        /// <summary>
        /// 使用当前串口超时创建一项标准事务请求。
        /// </summary>
        /// <param name="request">由统一工厂构建的严格标准 Modbus 请求。</param>
        /// <returns>携带当前总超时和字节间静默超时的事务请求。</returns>
        public TransactionRequest CreateStandardTransaction(ModbusRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return TransactionRequest.CreateStandard(
                request,
                TimeSpan.FromMilliseconds(serialConnection.ResponseTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(serialConnection.RawInterByteTimeoutMilliseconds));
        }

        /// <summary>
        /// 执行参数页或专家工具已经构建的标准请求，并复用同一日志、统计和快照路径。
        /// </summary>
        /// <param name="request">由统一工厂构建的严格标准请求。</param>
        /// <param name="cancellationToken">取消当前被接受事务的令牌。</param>
        /// <returns>Busy 等拒绝或唯一事务终态。</returns>
        public ValueTask<TransactionExecutionResult> ExecuteStandardAsync(
            ModbusRequest request,
            CancellationToken cancellationToken)
        {
            return operationService.ExecuteAsync(
                CreateStandardTransaction(request),
                cancellationToken);
        }

        /// <summary>
        /// 把专家工具文本强制作为 RawDebug 事务发送，即使它恰好可被识别为标准请求也不触发自动配置切换。
        /// </summary>
        /// <param name="text">待解析的十六进制帧文本。</param>
        /// <param name="cancellationToken">取消当前被接受原始事务的令牌。</param>
        /// <returns>输入失败时抛出格式异常；成功时返回 Busy 等拒绝或唯一原始事务终态。</returns>
        public ValueTask<TransactionExecutionResult> ExecuteRawTextAsync(
            string text,
            CancellationToken cancellationToken)
        {
            HexFrameParseResult parseResult = HexFrameParser.TryParse(
                text,
                TransactionRequest.MaximumRequestFrameBytes);

            if (!parseResult.IsSuccess)
            {
                throw new FormatException(parseResult.ErrorMessage);
            }

            byte[] frame = parseResult.Bytes.ToArray();

            if (AutoAppendCrc && !parseResult.HasValidTrailingCrc)
            {
                if (frame.Length > TransactionRequest.MaximumRequestFrameBytes - 2)
                {
                    throw new FormatException("自动补充 CRC 后发送帧不能超过 256 字节。");
                }

                frame = ModbusCrc16.Append(frame);
            }

            TransactionRequest request = TransactionRequest.CreateRawDebug(
                frame,
                TimeSpan.FromMilliseconds(serialConnection.ResponseTimeoutMilliseconds),
                TimeSpan.FromMilliseconds(serialConnection.RawInterByteTimeoutMilliseconds));
            return operationService.ExecuteAsync(request, cancellationToken);
        }

        /// <summary>
        /// 取消全部后台事件订阅；定时服务本身由应用组合根统一释放。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            operationService.MetricsChanged -= HandleMetricsChanged;
            operationService.OperationCompleted -= HandleOperationCompleted;
            periodicSendService.RunningChanged -= HandlePeriodicRunningChanged;
            periodicSendService.AttemptCompleted -= HandlePeriodicAttemptCompleted;
            serialConnection.PropertyChanged -= HandleSerialConnectionPropertyChanged;
        }

        /// <summary>
        /// 解析当前十六进制输入、自动处理 CRC、识别标准请求并执行一次手动事务。
        /// </summary>
        /// <returns>事务或输入错误处理完成后的任务。</returns>
        private async Task SendAsync()
        {
            if (!TryBuildTransactionFromInput(out TransactionRequest? request, out string? errorMessage))
            {
                StatusMessage = errorMessage!;
                return;
            }

            TransactionExecutionResult result = await operationService.ExecuteAsync(
                request!,
                CancellationToken.None).ConfigureAwait(true);
            StatusMessage = result.Message;
            NotifyCommandStates();
        }

        /// <summary>
        /// 将当前输入固化为定时请求，并由用户显式启动首次完整间隔等待。
        /// </summary>
        private void StartPeriodic()
        {
            if (!TryBuildTransactionFromInput(out TransactionRequest? request, out string? errorMessage))
            {
                StatusMessage = errorMessage!;
                return;
            }

            try
            {
                periodicSendService.Configure(
                    request!,
                    TimeSpan.FromMilliseconds(PeriodicIntervalMilliseconds));
                periodicSendService.Start();
                StatusMessage = $"定时发送已开启；首次发送将在 {PeriodicIntervalMilliseconds} ms 完整间隔后尝试。";
            }
            catch (Exception exception)
            {
                StatusMessage = exception.Message;
            }

            NotifyCommandStates();
        }

        /// <summary>
        /// 立即停止当前定时调度代次，已开始事务可完成但不会安排下一次。
        /// </summary>
        private void StopPeriodic()
        {
            periodicSendService.Stop();
            StatusMessage = "定时发送已关闭；不会自动恢复。";
            NotifyCommandStates();
        }

        /// <summary>
        /// 将当前输入转换为标准事务或无法识别结构时的原始调试事务。
        /// </summary>
        /// <param name="request">成功时接收完整事务请求。</param>
        /// <param name="errorMessage">失败时接收可直接显示的输入错误。</param>
        /// <returns>输入能够形成非空线路帧时返回真。</returns>
        private bool TryBuildTransactionFromInput(
            out TransactionRequest? request,
            out string? errorMessage)
        {
            HexFrameParseResult parseResult = HexFrameParser.TryParse(
                InputText,
                TransactionRequest.MaximumRequestFrameBytes);

            if (!parseResult.IsSuccess)
            {
                request = null;
                errorMessage = parseResult.ErrorMessage;
                return false;
            }

            byte[] frame = parseResult.Bytes.ToArray();

            if (AutoAppendCrc && !parseResult.HasValidTrailingCrc)
            {
                if (frame.Length > TransactionRequest.MaximumRequestFrameBytes - 2)
                {
                    request = null;
                    errorMessage = "自动补充 CRC 后发送帧不能超过 256 字节。";
                    return false;
                }

                frame = ModbusCrc16.Append(frame);
                InputText = HexFrameParser.Format(frame);
            }

            ModbusRequestParseResult standardResult = ModbusRequestParser.TryParseSupported(frame);
            TimeSpan responseTimeout = TimeSpan.FromMilliseconds(serialConnection.ResponseTimeoutMilliseconds);
            TimeSpan rawTimeout = TimeSpan.FromMilliseconds(serialConnection.RawInterByteTimeoutMilliseconds);
            request = standardResult.Status == ModbusRequestParseStatus.Succeeded
                ? TransactionRequest.CreateStandard(
                    standardResult.Request!,
                    responseTimeout,
                    rawTimeout)
                : TransactionRequest.CreateRawDebug(
                    frame,
                    responseTimeout,
                    rawTimeout);
            errorMessage = standardResult.Status == ModbusRequestParseStatus.Succeeded
                ? null
                : $"按原始调试模式发送：{standardResult.ErrorMessage}";
            return true;
        }

        /// <summary>
        /// 创建右侧单列常用指令，每次点击都使用界面当前从站地址重新构建 CRC。
        /// </summary>
        /// <returns>固定业务顺序的常用指令数组。</returns>
        private CommonCommandItemViewModel[] CreateCommonCommands()
        {
            return
            [
                new CommonCommandItemViewModel(
                    "读取监测数据",
                    "40003～40010",
                    () => SetInput(CreateMonitorReadRequest())),
                new CommonCommandItemViewModel(
                    "读取全部寄存器",
                    "40001～40036",
                    () => SetInput(ModbusRequestFactory.CreateReadHoldingRegisters(CurrentSlaveAddress, 0x0000, 36))),
                new CommonCommandItemViewModel(
                    "读取报警参数",
                    "40011～40023",
                    () => SetInput(ModbusRequestFactory.CreateReadHoldingRegisters(CurrentSlaveAddress, 0x000A, 13))),
                new CommonCommandItemViewModel(
                    "读取补偿参数",
                    "40030～40035",
                    () => SetInput(ModbusRequestFactory.CreateReadHoldingRegisters(CurrentSlaveAddress, 0x001D, 6))),
                new CommonCommandItemViewModel(
                    "未知地址查询",
                    "仅限总线上一个设备",
                    () => SetInput(ModbusRequestFactory.CreateUnknownAddressQuery())),
            ];
        }

        /// <summary>
        /// 创建读取六项显示值及两项报警寄存器的标准请求。
        /// </summary>
        /// <returns>读取 40003 至 40010 的 0x03 请求。</returns>
        private ModbusRequest CreateMonitorReadRequest()
        {
            return ModbusRequestFactory.CreateReadHoldingRegisters(CurrentSlaveAddress, 0x0002, 8);
        }

        /// <summary>
        /// 获取当前界面普通从站地址并在构建请求前执行范围校验。
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
        /// 将一个标准请求完整帧格式化并填入收发输入框。
        /// </summary>
        /// <param name="request">由统一工厂构建的标准请求。</param>
        private void SetInput(ModbusRequest request)
        {
            InputText = FormatRequest(request);
            StatusMessage = $"已载入 0x{(byte)request.FunctionCode:X2} 常用指令，等待手动发送。";
        }

        /// <summary>
        /// 把标准请求完整线路帧格式化为大写空格分隔文本。
        /// </summary>
        /// <param name="request">由统一工厂构建的标准请求。</param>
        /// <returns>包含尾随 CRC 的十六进制文本。</returns>
        private static string FormatRequest(ModbusRequest request)
        {
            return HexFrameParser.Format(request.RawFrame.Span);
        }

        /// <summary>
        /// 获取当前状态是否允许发送手动指令。
        /// </summary>
        /// <returns>已连接、无活动事务且输入非空时返回真。</returns>
        private bool CanSend()
        {
            return serialConnection.IsConnected &&
                !serialConnection.IsTransactionBusy &&
                !string.IsNullOrWhiteSpace(InputText);
        }

        /// <summary>
        /// 获取当前状态是否允许手动开启定时发送。
        /// </summary>
        /// <returns>已连接、未运行、无活动事务且间隔合法时返回真。</returns>
        private bool CanStartPeriodic()
        {
            return serialConnection.IsConnected &&
                !serialConnection.IsTransactionBusy &&
                !IsPeriodicRunning &&
                PeriodicIntervalMilliseconds is >= 100 and <= 3_600_000 &&
                !string.IsNullOrWhiteSpace(InputText);
        }

        /// <summary>
        /// 获取当前状态是否允许停止定时发送。
        /// </summary>
        /// <returns>定时发送正在运行时返回真。</returns>
        private bool CanStopPeriodic()
        {
            return IsPeriodicRunning;
        }

        /// <summary>
        /// 接收统一统计快照并在界面线程更新收发区计数。
        /// </summary>
        /// <param name="metrics">操作服务发布的不可变统计快照。</param>
        private void HandleMetricsChanged(OperationMetrics metrics)
        {
            dispatcher.Post(
                () =>
                {
                    SendCount = metrics.SendCount;
                    SuccessCount = metrics.SuccessCount;
                    FailureCount = metrics.FailureCount;
                    LastSendTimeText = metrics.LastSendTime?.ToLocalTime().ToString("HH:mm:ss") ?? "--";
                    LastResponseDurationText = metrics.LastResponseDuration.HasValue
                        ? $"{metrics.LastResponseDuration.Value.TotalMilliseconds:0} ms"
                        : "--";
                });
        }

        /// <summary>
        /// 接收事务拒绝或终态结果并更新提示和命令可执行状态。
        /// </summary>
        /// <param name="result">Busy 等拒绝或唯一事务终态。</param>
        private void HandleOperationCompleted(TransactionExecutionResult result)
        {
            dispatcher.Post(
                () =>
                {
                    StatusMessage = result.Message;
                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 接收定时服务开关变化并同步界面按钮。
        /// </summary>
        /// <param name="isRunning">当前是否存在活动定时调度代次。</param>
        private void HandlePeriodicRunningChanged(bool isRunning)
        {
            dispatcher.Post(
                () =>
                {
                    IsPeriodicRunning = isRunning;
                    NotifyCommandStates();
                });
        }

        /// <summary>
        /// 接收每次定时尝试结果并显示无积压调度状态。
        /// </summary>
        /// <param name="result">Busy 等拒绝或定时事务唯一终态。</param>
        private void HandlePeriodicAttemptCompleted(TransactionExecutionResult result)
        {
            TransactionRequest? scheduledRequest = periodicSendService.ConfiguredRequest;

            if (scheduledRequest is not null)
            {
                operationService.RecordScheduledResult(scheduledRequest, result);
            }

            dispatcher.Post(
                () => StatusMessage = $"定时发送：{result.Message}");
        }

        /// <summary>
        /// 在连接、地址或忙状态变化后刷新收发命令可执行性。
        /// </summary>
        /// <param name="sender">发布属性变化的串口 ViewModel。</param>
        /// <param name="eventArgs">发生变化的属性名称。</param>
        private void HandleSerialConnectionPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName is nameof(SerialConnectionViewModel.IsConnected) or
                nameof(SerialConnectionViewModel.IsTransactionBusy) or
                nameof(SerialConnectionViewModel.SlaveAddress))
            {
                NotifyCommandStates();
            }
        }

        /// <summary>
        /// 通知发送和定时控制命令重新计算可执行性。
        /// </summary>
        private void NotifyCommandStates()
        {
            SendCommand.NotifyCanExecuteChanged();
            StartPeriodicCommand.NotifyCanExecuteChanged();
            StopPeriodicCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 输入改变时刷新发送和定时启动命令。
        /// </summary>
        /// <param name="value">新的十六进制输入文本。</param>
        partial void OnInputTextChanged(string value)
        {
            NotifyCommandStates();
        }

        /// <summary>
        /// 定时间隔改变时刷新启动命令。
        /// </summary>
        /// <param name="value">新的定时间隔毫秒数。</param>
        partial void OnPeriodicIntervalMillisecondsChanged(int value)
        {
            NotifyCommandStates();
        }
    }
}
