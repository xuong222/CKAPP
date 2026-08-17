using System.Net;
using System.Net.Sockets;

using CH32UpperComputer.Core.Iap;
using CH32UpperComputer.Infrastructure.Coordination;

namespace CH32UpperComputer.Infrastructure.Iap
{
    /// <summary>
    /// 保存一次 IAP 升级进度和设备信息的不可变快照。
    /// </summary>
    public sealed class IapUpgradeProgress
    {
        /// <summary>
        /// 初始化一份可直接投影到界面的升级快照。
        /// </summary>
        /// <param name="state">完整升级流程状态。</param>
        /// <param name="runState">独立 RUN 结果状态。</param>
        /// <param name="connectionAttempt">当前连接尝试次数。</param>
        /// <param name="connectionId">当前或最近一次 TCP 连接编号。</param>
        /// <param name="isConnected">当前是否持有可用 TCP 连接。</param>
        /// <param name="upgradeAttempt">当前完整升级次数。</param>
        /// <param name="confirmedBytes">已经被 DATA ACK 确认的字节数。</param>
        /// <param name="totalBytes">固件总字节数。</param>
        /// <param name="bytesPerSecond">最近两秒确认字节速度。</param>
        /// <param name="elapsed">本次升级已经使用的时间。</param>
        /// <param name="estimatedRemaining">按当前速度估算的剩余时间。</param>
        /// <param name="helloInfo">最近一次完整 HELLO 信息。</param>
        /// <param name="message">当前中文状态说明。</param>
        public IapUpgradeProgress(
            IapUpgradeState state,
            IapRunState runState,
            int connectionAttempt,
            long connectionId,
            bool isConnected,
            int upgradeAttempt,
            int confirmedBytes,
            int totalBytes,
            double bytesPerSecond,
            TimeSpan elapsed,
            TimeSpan? estimatedRemaining,
            IapHelloInfo? helloInfo,
            string message)
        {
            State = state;
            RunState = runState;
            ConnectionAttempt = connectionAttempt;
            ConnectionId = connectionId;
            IsConnected = isConnected;
            UpgradeAttempt = upgradeAttempt;
            ConfirmedBytes = confirmedBytes;
            TotalBytes = totalBytes;
            BytesPerSecond = bytesPerSecond;
            Elapsed = elapsed;
            EstimatedRemaining = estimatedRemaining;
            HelloInfo = helloInfo;
            Message = message ?? string.Empty;
        }

        /// <summary>
        /// 获取完整升级状态。
        /// </summary>
        public IapUpgradeState State { get; }

        /// <summary>
        /// 获取独立 RUN 状态。
        /// </summary>
        public IapRunState RunState { get; }

        /// <summary>
        /// 获取连接尝试次数。
        /// </summary>
        public int ConnectionAttempt { get; }

        /// <summary>
        /// 获取当前或最近一次 TCP 连接编号。
        /// </summary>
        public long ConnectionId { get; }

        /// <summary>
        /// 获取当前协议客户端是否仍连接。
        /// </summary>
        public bool IsConnected { get; }

        /// <summary>
        /// 获取完整升级尝试次数。
        /// </summary>
        public int UpgradeAttempt { get; }

        /// <summary>
        /// 获取已经由 DATA ACK 确认的字节数。
        /// </summary>
        public int ConfirmedBytes { get; }

        /// <summary>
        /// 获取固件总字节数。
        /// </summary>
        public int TotalBytes { get; }

        /// <summary>
        /// 获取最近两秒确认速度。
        /// </summary>
        public double BytesPerSecond { get; }

        /// <summary>
        /// 获取本次流程已用时间。
        /// </summary>
        public TimeSpan Elapsed { get; }

        /// <summary>
        /// 获取估计剩余时间；速度不足时为空。
        /// </summary>
        public TimeSpan? EstimatedRemaining { get; }

        /// <summary>
        /// 获取最近一次 HELLO 设备信息。
        /// </summary>
        public IapHelloInfo? HelloInfo { get; }

        /// <summary>
        /// 获取当前中文状态说明。
        /// </summary>
        public string Message { get; }
    }

    /// <summary>
    /// 按固定重试、重连和验证规则协调完整 Ethernet IAP 升级。
    /// </summary>
    public sealed class IapUpgradeCoordinator : IAsyncDisposable
    {
        /// <summary>
        /// 目标设备 App 固定基址。
        /// </summary>
        public const uint ExpectedAppBase = 0x0800C000U;

        /// <summary>
        /// 初始连接总超时。
        /// </summary>
        public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 连接失败后的间隔。
        /// </summary>
        public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// HELLO 响应总超时。
        /// </summary>
        public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// BEGIN 擦除响应总超时。
        /// </summary>
        public static readonly TimeSpan BeginTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// DATA 响应总超时。
        /// </summary>
        public static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// END 响应总超时。
        /// </summary>
        public static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// RUN 响应总超时。
        /// </summary>
        public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// ABORT 尽力确认超时。
        /// </summary>
        public static readonly TimeSpan AbortTimeout = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// 完整 BEGIN/DATA/END 最多尝试次数。
        /// </summary>
        public const int MaximumUpgradeAttempts = 3;

        /// <summary>
        /// DATA CRC 失败后额外重发次数。
        /// </summary>
        public const int MaximumDataCrcRetries = 3;

        /// <summary>
        /// 完整升级重启或验证阶段最多重连次数。
        /// </summary>
        public const int MaximumReconnectAttempts = 3;

        /// <summary>
        /// 序列化升级和 RUN 流程。
        /// </summary>
        private readonly SemaphoreSlim flowGate = new(1, 1);

        /// <summary>
        /// 保护活动取消源和完成信号。
        /// </summary>
        private readonly object activeSync = new();

        /// <summary>
        /// 顺序协议客户端。
        /// </summary>
        private readonly IapProtocolClient protocolClient;

        /// <summary>
        /// 独立 IAP 日志服务。
        /// </summary>
        private readonly IapCommunicationLogService logService;

        /// <summary>
        /// 应用级 Modbus/IAP 互斥门。
        /// </summary>
        private readonly IApplicationOperationGate applicationOperationGate;

        /// <summary>
        /// 提供延时、耗时和速度窗口。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 最近两秒确认字节样本。
        /// </summary>
        private readonly Queue<(DateTimeOffset Timestamp, int Bytes)> speedSamples = new();

        /// <summary>
        /// 当前升级的内部取消源。
        /// </summary>
        private CancellationTokenSource? activeCancellation;

        /// <summary>
        /// 当前升级收敛时完成的信号。
        /// </summary>
        private TaskCompletionSource? activeCompletion;

        /// <summary>
        /// 最近一次完成验证的固件。
        /// </summary>
        private FirmwareImage? verifiedImage;

        /// <summary>
        /// 当前升级状态。
        /// </summary>
        private IapUpgradeState state = IapUpgradeState.Idle;

        /// <summary>
        /// 当前 RUN 状态。
        /// </summary>
        private IapRunState runState = IapRunState.NotRequested;

        /// <summary>
        /// 当前连接尝试次数。
        /// </summary>
        private int connectionAttempt;

        /// <summary>
        /// 当前完整升级尝试次数。
        /// </summary>
        private int upgradeAttempt;

        /// <summary>
        /// 当前已确认字节数。
        /// </summary>
        private int confirmedBytes;

        /// <summary>
        /// 当前固件总字节数。
        /// </summary>
        private int totalBytes;

        /// <summary>
        /// 当前升级开始时间戳。
        /// </summary>
        private long startedTimestamp;

        /// <summary>
        /// 最近一次 HELLO 信息。
        /// </summary>
        private IapHelloInfo? lastHelloInfo;

        /// <summary>
        /// 最近一次状态消息。
        /// </summary>
        private string statusMessage = "等待选择固件";

        /// <summary>
        /// 防止释放后继续工作。
        /// </summary>
        private int isDisposed;

        /// <summary>
        /// 初始化完整 IAP 升级协调器。
        /// </summary>
        /// <param name="protocolClient">负责逐项请求和响应匹配的协议客户端。</param>
        /// <param name="logService">保存 IAP 原始帧和状态诊断的日志服务。</param>
        /// <param name="applicationOperationGate">在 IAP 和 Modbus 之间提供无队列互斥。</param>
        /// <param name="timeProvider">提供可测试时间、延时和速度窗口。</param>
        public IapUpgradeCoordinator(
            IapProtocolClient protocolClient,
            IapCommunicationLogService logService,
            IApplicationOperationGate applicationOperationGate,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(protocolClient);
            ArgumentNullException.ThrowIfNull(logService);
            ArgumentNullException.ThrowIfNull(applicationOperationGate);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.protocolClient = protocolClient;
            this.logService = logService;
            this.applicationOperationGate = applicationOperationGate;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 在升级或 RUN 状态变化后发布完整快照。
        /// </summary>
        public event Action<IapUpgradeProgress>? ProgressChanged;

        /// <summary>
        /// 获取当前完整升级状态。
        /// </summary>
        public IapUpgradeState State => state;

        /// <summary>
        /// 获取当前 RUN 状态。
        /// </summary>
        public IapRunState RunState => runState;

        /// <summary>
        /// 获取当前是否存在未到终态的升级流程。
        /// </summary>
        public bool IsUpgradeActive =>
            state is IapUpgradeState.Connecting or
                IapUpgradeState.BootloaderConnected or
                IapUpgradeState.Erasing or
                IapUpgradeState.Transferring or
                IapUpgradeState.Verifying;

        /// <summary>
        /// 标记固件已经可用但尚未开始连接。
        /// </summary>
        /// <param name="image">已经校验的固件快照。</param>
        public void SetFirmwareReady(FirmwareImage image)
        {
            ArgumentNullException.ThrowIfNull(image);

            if (IsUpgradeActive)
            {
                throw new InvalidOperationException("升级进行中不能替换固件。");
            }

            verifiedImage = null;
            totalBytes = image.Length;
            confirmedBytes = 0;
            SetState(IapUpgradeState.FirmwareReady, "固件已校验，等待开始升级。");
        }

        /// <summary>
        /// 清除当前固件和已经验证的 RUN 资格。
        /// </summary>
        public void ClearFirmware()
        {
            if (IsUpgradeActive)
            {
                throw new InvalidOperationException("升级进行中不能清除固件。");
            }

            verifiedImage = null;
            totalBytes = 0;
            confirmedBytes = 0;
            lastHelloInfo = null;
            runState = IapRunState.NotRequested;
            SetState(IapUpgradeState.Idle, "等待选择固件。");
        }

        /// <summary>
        /// 执行最多三次的完整 BEGIN/DATA/END 与 END 后验证流程。
        /// </summary>
        /// <param name="image">已固定字节和 CRC32 的固件快照。</param>
        /// <param name="address">目标 Bootloader IPv4 地址。</param>
        /// <param name="port">目标 TCP 端口。</param>
        /// <param name="cancellationToken">取消外部调用和升级流程。</param>
        /// <returns>流程到达成功、失败、取消或不确定终态后的任务。</returns>
        public async Task StartUpgradeAsync(
            FirmwareImage image,
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(address);
            ThrowIfDisposed();

            if (!await flowGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("当前已有固件升级或 RUN 操作。");
            }

            IApplicationOperationLease? iapLease =
                applicationOperationGate.TryEnterIap();

            if (iapLease is null)
            {
                flowGate.Release();
                throw new InvalidOperationException("当前存在 Modbus 请求，完成后才能开始固件升级。");
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            lock (activeSync)
            {
                activeCancellation = linkedCancellation;
                activeCompletion = completion;
            }

            try
            {
                ResetForUpgrade(image);
                IapHelloInfo initialHello = await ConnectUntilBootloaderAsync(
                    image,
                    address,
                    port,
                    linkedCancellation.Token).ConfigureAwait(false);
                lastHelloInfo = initialHello;

                for (int attempt = 1; attempt <= MaximumUpgradeAttempts; attempt++)
                {
                    upgradeAttempt = attempt;
                    PublishProgress($"开始完整升级尝试 {attempt}/{MaximumUpgradeAttempts}。");

                    try
                    {
                        await ExecuteTransferAsync(image, linkedCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (EndResultUncertainException exception)
                    {
                        logService.RecordSystem(
                            protocolClient.ConnectionId,
                            $"END 结果不确定：{exception.Message}，只执行 HELLO 验证。");
                        await protocolClient.DisconnectAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is RetryableUpgradeException or
                        TimeoutException or
                        IOException or
                        SocketException)
                    {
                        logService.RecordSystem(
                            protocolClient.ConnectionId,
                            $"第 {attempt} 次完整升级需要重启：{exception.Message}");

                        if (attempt >= MaximumUpgradeAttempts)
                        {
                            SetState(IapUpgradeState.Failed, exception.Message);
                            return;
                        }

                        await protocolClient.DisconnectAsync().ConfigureAwait(false);

                        if (!await ConnectForRestartAsync(
                            image,
                            address,
                            port,
                            linkedCancellation.Token).ConfigureAwait(false))
                        {
                            SetState(
                                IapUpgradeState.Failed,
                                "完整升级重启阶段连续三次无法连接 Bootloader。");
                            return;
                        }

                        confirmedBytes = 0;
                        speedSamples.Clear();
                        continue;
                    }

                    VerificationResult verification = await VerifyAfterEndAsync(
                        image,
                        address,
                        port,
                        linkedCancellation.Token).ConfigureAwait(false);

                    if (verification == VerificationResult.Matched)
                    {
                        verifiedImage = image;
                        SetState(
                            IapUpgradeState.UpgradeSucceeded,
                            "升级成功：App 有效状态、大小和 CRC32 均匹配。");
                        return;
                    }

                    if (verification == VerificationResult.Unavailable)
                    {
                        SetState(
                            IapUpgradeState.Uncertain,
                            "END 后三次验证连接均失败，设备最终结果不确定，未重新擦除 App。");
                        return;
                    }

                    if (attempt >= MaximumUpgradeAttempts)
                    {
                        SetState(
                            IapUpgradeState.Failed,
                            "HELLO 明确显示 App 元数据不匹配，完整升级次数已耗尽。");
                        return;
                    }

                    confirmedBytes = 0;
                    speedSamples.Clear();
                    logService.RecordSystem(
                        protocolClient.ConnectionId,
                        "HELLO 明确显示 App 无效或大小/CRC 不匹配，允许下一次完整升级。");
                    await protocolClient.DisconnectAsync().ConfigureAwait(false);

                    if (!await ConnectForRestartAsync(
                        image,
                        address,
                        port,
                        linkedCancellation.Token).ConfigureAwait(false))
                    {
                        SetState(
                            IapUpgradeState.Failed,
                            "App 验证明确不匹配后，连续三次无法重新连接 Bootloader。");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await TryAbortAndDisconnectAsync().ConfigureAwait(false);
                SetState(IapUpgradeState.Cancelled, "固件升级已取消。");
            }
            catch (IapCompatibilityException exception)
            {
                await protocolClient.DisconnectAsync().ConfigureAwait(false);
                SetState(IapUpgradeState.Failed, exception.Message);
            }
            catch (Exception exception)
            {
                await protocolClient.DisconnectAsync().ConfigureAwait(false);
                SetState(IapUpgradeState.Failed, $"固件升级失败：{exception.Message}");
            }
            finally
            {
                lock (activeSync)
                {
                    activeCancellation = null;
                    activeCompletion = null;
                }

                completion.TrySetResult();
                iapLease.Dispose();
                flowGate.Release();
            }
        }

        /// <summary>
        /// 请求取消当前升级并等待其进入终态。
        /// </summary>
        /// <param name="cancellationToken">取消调用方等待，不撤销已发出的取消请求。</param>
        /// <returns>活动升级已经收敛后的任务。</returns>
        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource? cancellation;
            Task? completion;

            lock (activeSync)
            {
                cancellation = activeCancellation;
                completion = activeCompletion?.Task;
            }

            cancellation?.Cancel();

            if (completion is not null)
            {
                await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 在升级已经验证成功后发送一次 RUN，不自动重发不确定请求。
        /// </summary>
        /// <param name="address">目标 Bootloader IPv4 地址。</param>
        /// <param name="port">目标 TCP 端口。</param>
        /// <param name="cancellationToken">取消 RUN 前的连接或等待。</param>
        /// <returns>RUN 进入 Confirmed、Uncertain 或 Rejected 后的任务。</returns>
        public async Task RunApplicationAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            FirmwareImage image = verifiedImage ??
                throw new InvalidOperationException("只有本次 HELLO 验证成功后才能运行 App。");

            if (!await flowGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("当前已有固件升级或 RUN 操作。");
            }

            IApplicationOperationLease? iapLease =
                applicationOperationGate.TryEnterIap();

            if (iapLease is null)
            {
                flowGate.Release();
                throw new InvalidOperationException("当前存在 Modbus 请求，完成后才能运行 App。");
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bool runRequestStarted = false;

            lock (activeSync)
            {
                activeCancellation = linkedCancellation;
                activeCompletion = completion;
            }

            try
            {
                SetRunState(IapRunState.Starting, "正在发送 RUN。");

                if (!protocolClient.IsConnected)
                {
                    bool verified = await ConnectAndVerifyForRunAsync(
                        image,
                        address,
                        port,
                        linkedCancellation.Token).ConfigureAwait(false);

                    if (!verified)
                    {
                        SetRunState(
                            IapRunState.Rejected,
                            "无法重新连接并确认同一镜像，未发送 RUN。");
                        return;
                    }
                }

                try
                {
                    runRequestStarted = true;
                    IapExchangeResult result = await protocolClient
                        .SendRunAsync(RunTimeout, linkedCancellation.Token)
                        .ConfigureAwait(false);

                    if (result.Response.Status == IapResponseStatus.Ack)
                    {
                        await protocolClient.DisconnectAsync().ConfigureAwait(false);
                        SetRunState(
                            IapRunState.Confirmed,
                            "已收到完整 RUN ACK；随后断开属于设备正常跳转。");
                    }
                    else
                    {
                        await protocolClient.DisconnectAsync().ConfigureAwait(false);
                        SetRunState(
                            IapRunState.Rejected,
                            IapErrorTranslator.Translate(
                                IapCommand.Run,
                                result.Response.Sequence,
                                result.Response.Detail));
                    }
                }
                catch (Exception exception) when (
                    exception is TimeoutException or IOException or
                    SocketException or
                    OperationCanceledException)
                {
                    await protocolClient.DisconnectAsync().ConfigureAwait(false);
                    SetRunState(
                        IapRunState.Uncertain,
                        $"升级已成功，RUN 可能已送达但未取得完整 ACK：{exception.Message}");
                }
            }
            catch (OperationCanceledException) when (!runRequestStarted)
            {
                await protocolClient.DisconnectAsync().ConfigureAwait(false);
                SetRunState(
                    IapRunState.Rejected,
                    "RUN 前连接或镜像复核已取消，未发送 RUN。");
            }
            finally
            {
                lock (activeSync)
                {
                    activeCancellation = null;
                    activeCompletion = null;
                }

                completion.TrySetResult();
                iapLease.Dispose();
                flowGate.Release();
            }
        }

        /// <summary>
        /// 取消活动流程并释放协议客户端。
        /// </summary>
        /// <returns>所有 IAP 资源已经释放后的任务。</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref isDisposed, 1) != 0)
            {
                return;
            }

            await CancelAsync(CancellationToken.None).ConfigureAwait(false);
            await protocolClient.DisposeAsync().ConfigureAwait(false);
            flowGate.Dispose();
        }

        /// <summary>
        /// 重置一次新升级的计数、速度和状态。
        /// </summary>
        /// <param name="image">本次完整固件快照。</param>
        private void ResetForUpgrade(FirmwareImage image)
        {
            verifiedImage = null;
            runState = IapRunState.NotRequested;
            connectionAttempt = 0;
            upgradeAttempt = 0;
            confirmedBytes = 0;
            totalBytes = image.Length;
            lastHelloInfo = null;
            speedSamples.Clear();
            startedTimestamp = timeProvider.GetTimestamp();
            SetState(IapUpgradeState.Connecting, "等待设备复位并进入 Bootloader。");
        }

        /// <summary>
        /// 在初次启动阶段持续连接，直到取得合法 HELLO 或用户取消。
        /// </summary>
        /// <param name="image">待升级固件。</param>
        /// <param name="address">目标 IPv4 地址。</param>
        /// <param name="port">目标端口。</param>
        /// <param name="cancellationToken">取消无限但可中止的初始等待。</param>
        /// <returns>已经通过兼容性校验的 HELLO 信息。</returns>
        private async Task<IapHelloInfo> ConnectUntilBootloaderAsync(
            FirmwareImage image,
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                connectionAttempt = checked(connectionAttempt + 1);
                PublishProgress($"正在连接 Bootloader，第 {connectionAttempt} 次尝试。");

                try
                {
                    await protocolClient
                        .ConnectAsync(address, port, ConnectionTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    IapHelloInfo hello = await SendAndValidateHelloAsync(
                        image,
                        cancellationToken).ConfigureAwait(false);
                    SetState(IapUpgradeState.BootloaderConnected, "Bootloader HELLO 验证成功。");
                    return hello;
                }
                catch (IapCompatibilityException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logService.RecordSystem(
                        protocolClient.ConnectionId,
                        $"连接或 HELLO 失败：{exception.Message}");
                    await protocolClient.DisconnectAsync().ConfigureAwait(false);
                    await Task.Delay(RetryInterval, timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 在完整重启阶段最多连接三次并重新 HELLO。
        /// </summary>
        /// <param name="image">待升级固件。</param>
        /// <param name="address">目标 IPv4 地址。</param>
        /// <param name="port">目标端口。</param>
        /// <param name="cancellationToken">取消重连。</param>
        /// <returns>任一重连和 HELLO 成功时返回真。</returns>
        private async Task<bool> ConnectForRestartAsync(
            FirmwareImage image,
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= MaximumReconnectAttempts; attempt++)
            {
                connectionAttempt = checked(connectionAttempt + 1);

                try
                {
                    await protocolClient
                        .ConnectAsync(address, port, ConnectionTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    lastHelloInfo = await SendAndValidateHelloAsync(
                        image,
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (IapCompatibilityException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logService.RecordSystem(
                        protocolClient.ConnectionId,
                        $"完整重启连接 {attempt}/{MaximumReconnectAttempts} 失败：{exception.Message}");
                    await protocolClient.DisconnectAsync().ConfigureAwait(false);

                    if (attempt < MaximumReconnectAttempts)
                    {
                        await Task.Delay(RetryInterval, timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 在当前连接执行 BEGIN、全部 DATA 和 END。
        /// </summary>
        /// <param name="image">本次固件快照。</param>
        /// <param name="cancellationToken">取消传输。</param>
        /// <returns>END 已 ACK 或结果需要 HELLO 验证后的任务。</returns>
        private async Task ExecuteTransferAsync(
            FirmwareImage image,
            CancellationToken cancellationToken)
        {
            SetState(IapUpgradeState.Erasing, "BEGIN 已发送，正在等待擦除 App 区。");
            IapExchangeResult begin = await protocolClient
                .SendBeginAsync(
                    checked((uint)image.Length),
                    image.Crc32,
                    BeginTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (begin.Response.Status != IapResponseStatus.Ack ||
                begin.Response.Detail != 0U)
            {
                throw new RetryableUpgradeException(
                    begin.Response.Status == IapResponseStatus.Nack
                        ? IapErrorTranslator.Translate(
                            IapCommand.Begin,
                            begin.Response.Sequence,
                            begin.Response.Detail)
                        : $"BEGIN ACK detail 应为 0，实际为 {begin.Response.Detail}。");
            }

            SetState(IapUpgradeState.Transferring, "开始顺序发送 DATA 分片。");
            confirmedBytes = 0;

            while (confirmedBytes < image.Length)
            {
                int length = Math.Min(
                    IapFrameCodec.MaximumDataLength,
                    image.Length - confirmedBytes);
                ReadOnlyMemory<byte> payload = image.Content.Slice(confirmedBytes, length);
                bool acknowledged = false;

                for (int retry = 0; retry <= MaximumDataCrcRetries; retry++)
                {
                    IapExchangeResult data = await protocolClient
                        .SendDataAsync(
                            checked((uint)confirmedBytes),
                            payload,
                            DataTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    uint expectedOffset = checked((uint)(confirmedBytes + length));

                    if (data.Response.Status == IapResponseStatus.Ack &&
                        data.Response.Detail == expectedOffset)
                    {
                        confirmedBytes += length;
                        RecordSpeedSample(length);
                        PublishProgress($"已确认 {confirmedBytes}/{image.Length} 字节。");
                        acknowledged = true;
                        break;
                    }

                    bool crcNack = data.Response.Status == IapResponseStatus.Nack &&
                        data.Response.Detail is 5U or 7U;

                    if (crcNack && retry < MaximumDataCrcRetries)
                    {
                        logService.RecordSystem(
                            protocolClient.ConnectionId,
                            $"DATA offset={confirmedBytes} CRC NACK，额外重发 {retry + 1}/{MaximumDataCrcRetries}。");
                        continue;
                    }

                    string message = data.Response.Status == IapResponseStatus.Nack
                        ? IsExpectedOffsetDetail(data.Response.Detail, image.Length)
                            ? $"DATA sequence={data.Response.Sequence} 返回设备期望 " +
                                $"offset={data.Response.Detail}，当前发送 offset={confirmedBytes}，" +
                                "禁止续传并重新执行完整升级。"
                            : IapErrorTranslator.Translate(
                                IapCommand.Data,
                                data.Response.Sequence,
                                data.Response.Detail)
                        : $"DATA ACK 期望 next offset={expectedOffset}，实际为 {data.Response.Detail}。";
                    throw new RetryableUpgradeException(message);
                }

                if (!acknowledged)
                {
                    throw new RetryableUpgradeException("DATA 分片 CRC 重发次数已耗尽。");
                }
            }

            SetState(IapUpgradeState.Verifying, "正在发送 END 并校验完整镜像。");

            try
            {
                IapExchangeResult end = await protocolClient
                    .SendEndAsync(EndTimeout, cancellationToken)
                    .ConfigureAwait(false);

                if (end.Response.Status == IapResponseStatus.Nack)
                {
                    throw new RetryableUpgradeException(
                        IapErrorTranslator.Translate(
                            IapCommand.End,
                            end.Response.Sequence,
                            end.Response.Detail));
                }

                if (end.Response.Detail != 0U &&
                    end.Response.Detail != checked((uint)image.Length))
                {
                    throw new RetryableUpgradeException(
                        $"END ACK detail 必须为 0 或 {image.Length}，实际为 {end.Response.Detail}。");
                }
            }
            catch (Exception exception) when (
                exception is TimeoutException or IOException or SocketException)
            {
                throw new EndResultUncertainException(exception.Message, exception);
            }
        }

        /// <summary>
        /// END 后优先复用原连接 HELLO，失败时只重连验证。
        /// </summary>
        /// <param name="image">用于对比大小和 CRC32 的固件。</param>
        /// <param name="address">验证重连目标 IPv4。</param>
        /// <param name="port">验证重连目标端口。</param>
        /// <param name="cancellationToken">取消验证。</param>
        /// <returns>匹配、明确不匹配或无法验证结果。</returns>
        private async Task<VerificationResult> VerifyAfterEndAsync(
            FirmwareImage image,
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            SetState(IapUpgradeState.Verifying, "END 后正在重新 HELLO 验证 App。");

            if (protocolClient.IsConnected)
            {
                try
                {
                    IapHelloInfo hello = await SendAndValidateHelloAsync(
                        image,
                        cancellationToken).ConfigureAwait(false);
                    lastHelloInfo = hello;
                    return IsImageMatched(hello, image)
                        ? VerificationResult.Matched
                        : VerificationResult.Mismatched;
                }
                catch (IapCompatibilityException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logService.RecordSystem(
                        protocolClient.ConnectionId,
                        $"原连接上的 END 后 HELLO 失败：{exception.Message}");
                    await protocolClient.DisconnectAsync().ConfigureAwait(false);
                }
            }

            for (int attempt = 1; attempt <= MaximumReconnectAttempts; attempt++)
            {
                connectionAttempt = checked(connectionAttempt + 1);

                try
                {
                    await protocolClient
                        .ConnectAsync(address, port, ConnectionTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    IapHelloInfo hello = await SendAndValidateHelloAsync(
                        image,
                        cancellationToken).ConfigureAwait(false);
                    lastHelloInfo = hello;
                    return IsImageMatched(hello, image)
                        ? VerificationResult.Matched
                        : VerificationResult.Mismatched;
                }
                catch (IapCompatibilityException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logService.RecordSystem(
                        protocolClient.ConnectionId,
                        $"验证重连 {attempt}/{MaximumReconnectAttempts} 失败：{exception.Message}");
                    await protocolClient.DisconnectAsync().ConfigureAwait(false);

                    if (attempt < MaximumReconnectAttempts)
                    {
                        await Task.Delay(RetryInterval, timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            return VerificationResult.Unavailable;
        }

        /// <summary>
        /// 发送 HELLO、要求 ACK 并验证固定分区边界。
        /// </summary>
        /// <param name="image">用于设备容量检查的固件。</param>
        /// <param name="cancellationToken">取消 HELLO。</param>
        /// <returns>通过兼容性检查的 HELLO 信息。</returns>
        private async Task<IapHelloInfo> SendAndValidateHelloAsync(
            FirmwareImage image,
            CancellationToken cancellationToken)
        {
            IapExchangeResult helloResult = await protocolClient
                .SendHelloAsync(HelloTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (helloResult.Response.Status == IapResponseStatus.Nack)
            {
                throw new RetryableUpgradeException(
                    IapErrorTranslator.Translate(
                        IapCommand.Hello,
                        helloResult.Response.Sequence,
                        helloResult.Response.Detail));
            }

            IapHelloInfo hello = helloResult.HelloInfo ??
                throw new InvalidDataException("HELLO ACK 没有完整设备信息。");

            if (hello.AppBase != ExpectedAppBase)
            {
                throw new IapCompatibilityException(
                    $"设备 App 基址为 0x{hello.AppBase:X8}，期望 0x{ExpectedAppBase:X8}，禁止升级。");
            }

            if (hello.AppMaxSize > IapFrameCodec.MaximumImageLength ||
                hello.AppMaxSize < image.Length)
            {
                throw new IapCompatibilityException(
                    $"设备 App 容量 {hello.AppMaxSize} 字节与固件 {image.Length} 字节或上位机上限不兼容。");
            }

            lastHelloInfo = hello;
            PublishProgress("HELLO 信息已更新。");
            return hello;
        }

        /// <summary>
        /// 在 RUN 前最多三次重连并确认仍为同一已升级镜像。
        /// </summary>
        /// <param name="image">已经验证成功的固件。</param>
        /// <param name="address">目标 IPv4 地址。</param>
        /// <param name="port">目标端口。</param>
        /// <param name="cancellationToken">取消重连和 HELLO。</param>
        /// <returns>确认同一镜像时返回真。</returns>
        private async Task<bool> ConnectAndVerifyForRunAsync(
            FirmwareImage image,
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= MaximumReconnectAttempts; attempt++)
            {
                try
                {
                    await protocolClient
                        .ConnectAsync(address, port, ConnectionTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    IapHelloInfo hello = await SendAndValidateHelloAsync(
                        image,
                        cancellationToken).ConfigureAwait(false);
                    return IsImageMatched(hello, image);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logService.RecordSystem(
                        protocolClient.ConnectionId,
                        $"RUN 前验证 {attempt}/{MaximumReconnectAttempts} 失败：{exception.Message}");
                    await protocolClient.DisconnectAsync().ConfigureAwait(false);
                }
            }

            return false;
        }

        /// <summary>
        /// 用户取消后尽力发送 ABORT，随后无条件断开。
        /// </summary>
        /// <returns>ABORT 尝试和断开完成后的任务。</returns>
        private async Task TryAbortAndDisconnectAsync()
        {
            if (protocolClient.IsConnected)
            {
                try
                {
                    using CancellationTokenSource abortCancellation = new();
                    _ = await protocolClient
                        .SendAbortAsync(AbortTimeout, abortCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logService.RecordSystem(
                        protocolClient.ConnectionId,
                        $"取消时 ABORT 未确认：{exception.Message}");
                }
            }

            await protocolClient.DisconnectAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 判断 HELLO 元数据是否与本地固件完全匹配。
        /// </summary>
        /// <param name="hello">Bootloader 返回的 App 信息。</param>
        /// <param name="image">本地固件快照。</param>
        /// <returns>有效标志、大小和 CRC32 全部匹配时返回真。</returns>
        private static bool IsImageMatched(
            IapHelloInfo hello,
            FirmwareImage image)
        {
            return hello.AppValid &&
                hello.AppSize == image.Length &&
                hello.AppCrc32 == image.Crc32;
        }

        /// <summary>
        /// 判断 DATA NACK detail 是否符合当前 Bootloader 返回期望 offset 的形状。
        /// </summary>
        /// <param name="detail">DATA NACK 原始 detail。</param>
        /// <param name="imageLength">本地完整固件长度。</param>
        /// <returns>零、1024 字节分片边界或完整长度时返回真。</returns>
        private static bool IsExpectedOffsetDetail(
            uint detail,
            int imageLength)
        {
            uint length = checked((uint)imageLength);
            return detail <= length &&
                (detail == 0U ||
                    detail == length ||
                    detail % IapFrameCodec.MaximumDataLength == 0U);
        }

        /// <summary>
        /// 记录一项 ACK 字节样本并清理两秒窗口之外的数据。
        /// </summary>
        /// <param name="bytes">本次 DATA ACK 新确认的字节数。</param>
        private void RecordSpeedSample(int bytes)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            speedSamples.Enqueue((now, bytes));
            DateTimeOffset cutoff = now - TimeSpan.FromSeconds(2);

            while (speedSamples.Count > 0 &&
                speedSamples.Peek().Timestamp < cutoff)
            {
                speedSamples.Dequeue();
            }
        }

        /// <summary>
        /// 更新完整升级状态并发布快照和系统日志。
        /// </summary>
        /// <param name="newState">新的合法升级状态。</param>
        /// <param name="message">本次转换原因。</param>
        private void SetState(
            IapUpgradeState newState,
            string message)
        {
            state = newState;
            statusMessage = message;
            logService.RecordSystem(
                protocolClient.ConnectionId,
                $"状态 {newState}：{message}");
            PublishProgress(message);
        }

        /// <summary>
        /// 更新独立 RUN 状态并发布快照。
        /// </summary>
        /// <param name="newState">新的 RUN 状态。</param>
        /// <param name="message">RUN 状态说明。</param>
        private void SetRunState(
            IapRunState newState,
            string message)
        {
            runState = newState;
            statusMessage = message;
            logService.RecordSystem(
                protocolClient.ConnectionId,
                $"RUN {newState}：{message}");
            PublishProgress(message);
        }

        /// <summary>
        /// 创建当前进度快照并逐个发布给观察者。
        /// </summary>
        /// <param name="message">本次发布的最新说明。</param>
        private void PublishProgress(string message)
        {
            TimeSpan elapsed = startedTimestamp == 0
                ? TimeSpan.Zero
                : timeProvider.GetElapsedTime(startedTimestamp);
            double speed = CalculateSpeed();
            TimeSpan? remaining = speed > 0d && confirmedBytes < totalBytes
                ? TimeSpan.FromSeconds((totalBytes - confirmedBytes) / speed)
                : null;
            IapUpgradeProgress progress = new(
                state,
                runState,
                connectionAttempt,
                protocolClient.ConnectionId,
                protocolClient.IsConnected,
                upgradeAttempt,
                confirmedBytes,
                totalBytes,
                speed,
                elapsed,
                remaining,
                lastHelloInfo,
                string.IsNullOrWhiteSpace(message) ? statusMessage : message);
            Action<IapUpgradeProgress>? observers = ProgressChanged;

            if (observers is null)
            {
                return;
            }

            foreach (Action<IapUpgradeProgress> observer in
                observers.GetInvocationList().Cast<Action<IapUpgradeProgress>>())
            {
                try
                {
                    observer(progress);
                }
                catch (Exception)
                {
                    // 界面观察者异常不得破坏升级状态机。
                }
            }
        }

        /// <summary>
        /// 计算最近两秒窗口中的确认字节速度。
        /// </summary>
        /// <returns>每秒确认字节数；样本不足时为零。</returns>
        private double CalculateSpeed()
        {
            if (speedSamples.Count < 2)
            {
                return 0d;
            }

            (DateTimeOffset firstTimestamp, _) = speedSamples.Peek();
            (DateTimeOffset lastTimestamp, _) = speedSamples.Last();
            double seconds = (lastTimestamp - firstTimestamp).TotalSeconds;

            if (seconds <= 0d)
            {
                return 0d;
            }

            int bytes = speedSamples.Sum(sample => sample.Bytes);
            return bytes / seconds;
        }

        /// <summary>
        /// 释放后禁止开始新流程。
        /// </summary>
        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref isDisposed) != 0,
                this);
        }

        /// <summary>
        /// 区分 END 后验证的三种结果。
        /// </summary>
        private enum VerificationResult
        {
            /// <summary>
            /// App 元数据与本地镜像完全匹配。
            /// </summary>
            Matched,

            /// <summary>
            /// HELLO 明确返回不匹配元数据。
            /// </summary>
            Mismatched,

            /// <summary>
            /// 三次验证连接均无法取得完整 HELLO。
            /// </summary>
            Unavailable,
        }

        /// <summary>
        /// 表示可消耗下一次完整升级次数的确定错误。
        /// </summary>
        private sealed class RetryableUpgradeException : Exception
        {
            /// <summary>
            /// 使用明确原因创建重试错误。
            /// </summary>
            /// <param name="message">导致完整重启的原因。</param>
            internal RetryableUpgradeException(string message)
                : base(message)
            {
            }
        }

        /// <summary>
        /// 表示 END 可能已经生效但响应无法确定。
        /// </summary>
        private sealed class EndResultUncertainException : Exception
        {
            /// <summary>
            /// 使用底层网络异常创建 END 不确定错误。
            /// </summary>
            /// <param name="message">END 未确认原因。</param>
            /// <param name="innerException">原始网络或超时异常。</param>
            internal EndResultUncertainException(
                string message,
                Exception innerException)
                : base(message, innerException)
            {
            }
        }

        /// <summary>
        /// 表示 App 基址或容量与上位机固定安全边界不兼容。
        /// </summary>
        private sealed class IapCompatibilityException : Exception
        {
            /// <summary>
            /// 使用不可重试原因创建兼容错误。
            /// </summary>
            /// <param name="message">禁止继续升级的兼容性原因。</param>
            internal IapCompatibilityException(string message)
                : base(message)
            {
            }
        }
    }
}
