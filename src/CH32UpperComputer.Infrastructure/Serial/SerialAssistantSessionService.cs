using System.IO;
using System.Threading.Channels;

namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 管理普通串口助手独立传输、唯一接收循环、有界原始缓存和无重叠定时发送。
    /// </summary>
    public sealed class SerialAssistantSessionService : IAsyncDisposable
    {
        /// <summary>
        /// 单次手动或定时发送允许的最大线路负载。
        /// </summary>
        public const int MaximumPayloadBytes = 64 * 1024;

        /// <summary>
        /// 每个界面接收批次允许合并的最大原始字节数。
        /// </summary>
        public const int MaximumReceiveBatchBytes = 4096;

        /// <summary>
        /// 用于切换显示模式时重新渲染的原始接收缓存总容量。
        /// </summary>
        public const int ReceiveCacheCapacityBytes = 512 * 1024;

        /// <summary>
        /// 小流量接收在发布给界面前允许等待的最大合并窗口。
        /// </summary>
        public static readonly TimeSpan ReceiveBatchWindow = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// 保护连接状态、接收任务和定时任务身份的同步门。
        /// </summary>
        private readonly object stateSyncRoot = new();

        /// <summary>
        /// 保护原始缓存、暂停开关与全部统计计数的同步门。
        /// </summary>
        private readonly object cacheSyncRoot = new();

        /// <summary>
        /// 串行化连接、断开和永久释放操作。
        /// </summary>
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);

        /// <summary>
        /// 串行化助手手动和定时写入，并允许断开等待当前唯一写入完全返回。
        /// </summary>
        private readonly SemaphoreSlim sendGate = new(1, 1);

        /// <summary>
        /// 本服务独占、不得与 Modbus 协调器共享的第二套串口传输。
        /// </summary>
        private readonly ISerialTransport transport;

        /// <summary>
        /// 驱动接收批次窗口和定时发送间隔的统一时间源。
        /// </summary>
        private readonly TimeProvider timeProvider;

        /// <summary>
        /// 按到达顺序保存、总量不超过 512 KiB 的原始接收批次。
        /// </summary>
        private readonly LinkedList<SerialAssistantReceiveBatch> receiveCache = [];

        /// <summary>
        /// 当前原始接收缓存占用的字节数。
        /// </summary>
        private int receiveCacheBytes;

        /// <summary>
        /// 按实际发生顺序保存、总量不超过 512 KiB 的统一 TX/RX 画布记录。
        /// </summary>
        private readonly LinkedList<SerialAssistantTrafficBatch> trafficCache = [];

        /// <summary>
        /// 当前统一 TX/RX 画布缓存占用的原始字节数。
        /// </summary>
        private int trafficCacheBytes;

        /// <summary>
        /// 成功完成的发送操作次数。
        /// </summary>
        private long transmitOperationCount;

        /// <summary>
        /// 成功完成发送的线路字节数。
        /// </summary>
        private long transmitBytes;

        /// <summary>
        /// 从驱动接收并排空的线路字节数。
        /// </summary>
        private long receiveBytes;

        /// <summary>
        /// 因暂停或缓存淘汰而未保留的总字节数。
        /// </summary>
        private long discardedBytes;

        /// <summary>
        /// 暂停期间读取但未保留的字节数。
        /// </summary>
        private long pausedDiscardedBytes;

        /// <summary>
        /// 每次清空递增，用于阻止清空前尚未发布的合并批次重新进入缓存。
        /// </summary>
        private long clearVersion;

        /// <summary>
        /// 每次缓存写入或清空递增，用于界面识别重复、迟到和跳号更新。
        /// </summary>
        private long cacheRevision;

        /// <summary>
        /// 每次统一 TX/RX 缓存写入或清空递增，用于画布拒绝重复和迟到更新。
        /// </summary>
        private long trafficRevision;

        /// <summary>
        /// 每次任一统计计数改变时递增，用于界面拒绝并发发布产生的迟到统计。
        /// </summary>
        private long statisticsRevision;

        /// <summary>
        /// 当前是否暂停保存和显示新接收数据。
        /// </summary>
        private bool isPaused;

        /// <summary>
        /// 当前连接生命周期状态。
        /// </summary>
        private SerialAssistantSessionState state = SerialAssistantSessionState.Disconnected;

        /// <summary>
        /// 活动会话的唯一接收循环取消源。
        /// </summary>
        private CancellationTokenSource? receiveCancellation;

        /// <summary>
        /// 活动会话的唯一接收循环任务。
        /// </summary>
        private Task receiveTask = Task.CompletedTask;

        /// <summary>
        /// 活动定时发送循环的取消源；为空表示未运行。
        /// </summary>
        private CancellationTokenSource? periodicCancellation;

        /// <summary>
        /// 活动定时发送循环任务。
        /// </summary>
        private Task periodicTask = Task.CompletedTask;

        /// <summary>
        /// 指示当前接收循环退出是由用户主动断开触发。
        /// </summary>
        private bool isDisconnectRequested;

        /// <summary>
        /// 指示服务是否已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 初始化一个独占指定串口传输的普通串口助手会话。
        /// </summary>
        /// <param name="transport">只供本服务读取和写入的第二套串口传输。</param>
        /// <param name="timeProvider">驱动接收合批和定时发送的统一时间源。</param>
        public SerialAssistantSessionService(
            ISerialTransport transport,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(transport);
            ArgumentNullException.ThrowIfNull(timeProvider);
            this.transport = transport;
            this.timeProvider = timeProvider;
        }

        /// <summary>
        /// 在连接状态变化时发布不可变状态和中文说明。
        /// </summary>
        public event Action<SerialAssistantSessionStateChange>? StateChanged;

        /// <summary>
        /// 在新接收批次写入原始缓存后发布增量或完整重绘提示。
        /// </summary>
        public event Action<SerialAssistantReceiveUpdate>? ReceiveUpdated;

        /// <summary>
        /// 在成功发送或新接收批次进入统一画布缓存后发布增量刷新提示。
        /// </summary>
        public event Action<SerialAssistantTrafficUpdate>? TrafficUpdated;

        /// <summary>
        /// 在收发或丢弃计数变化时发布最新统计快照。
        /// </summary>
        public event Action<SerialAssistantStatistics>? StatisticsChanged;

        /// <summary>
        /// 在定时发送循环启动或完全停止时发布运行状态。
        /// </summary>
        public event Action<bool>? PeriodicSendingChanged;

        /// <summary>
        /// 获取当前连接生命周期状态。
        /// </summary>
        public SerialAssistantSessionState State
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return state;
                }
            }
        }

        /// <summary>
        /// 获取当前是否暂停保存新接收数据。
        /// </summary>
        public bool IsPaused
        {
            get
            {
                lock (cacheSyncRoot)
                {
                    return isPaused;
                }
            }
        }

        /// <summary>
        /// 获取定时发送循环是否仍在运行或收敛。
        /// </summary>
        public bool IsPeriodicSending
        {
            get
            {
                lock (stateSyncRoot)
                {
                    return periodicCancellation is not null;
                }
            }
        }

        /// <summary>
        /// 获取从最近一次清空开始的收发统计快照。
        /// </summary>
        public SerialAssistantStatistics Statistics
        {
            get
            {
                lock (cacheSyncRoot)
                {
                    return CreateStatisticsUnderLock();
                }
            }
        }

        /// <summary>
        /// 使用通用线路参数打开第二套传输并启动唯一接收循环。
        /// </summary>
        /// <param name="settings">经过验证的普通串口线路参数。</param>
        /// <param name="cancellationToken">取消尚未完成的连接操作。</param>
        /// <returns>端口打开且唯一接收循环建立后的任务。</returns>
        public async Task ConnectAsync(
            SerialLineSettings settings,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                lock (stateSyncRoot)
                {
                    ThrowIfDisposedUnderLock();

                    if (state is SerialAssistantSessionState.Connecting or
                        SerialAssistantSessionState.Connected or
                        SerialAssistantSessionState.Disconnecting)
                    {
                        throw new InvalidOperationException("串口助手已有连接操作正在进行。");
                    }

                    state = SerialAssistantSessionState.Connecting;
                    isDisconnectRequested = false;
                }

                PublishState(SerialAssistantSessionState.Connecting, $"正在打开 {settings.PortName}…");

                try
                {
                    await transport.OpenAsync(settings, cancellationToken).ConfigureAwait(false);
                    CancellationTokenSource cancellation = new();
                    int portGeneration = transport.PortGeneration;
                    TaskCompletionSource receiveStart = new(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    Task sessionReceiveTask = RunReceiveLoopAfterStartAsync(
                        receiveStart.Task,
                        portGeneration,
                        cancellation);

                    lock (stateSyncRoot)
                    {
                        ThrowIfDisposedUnderLock();
                        receiveCancellation = cancellation;
                        receiveTask = sessionReceiveTask;
                        state = SerialAssistantSessionState.Connected;
                    }

                    lock (cacheSyncRoot)
                    {
                        isPaused = false;
                    }

                    PublishState(
                        SerialAssistantSessionState.Connected,
                        $"串口助手已连接 {settings.PortName}。");
                    receiveStart.TrySetResult();
                }
                catch (Exception exception)
                {
                    try
                    {
                        await transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 打开失败后的清理异常不能覆盖更有诊断价值的首个打开异常。
                    }

                    string message = TranslateSerialException(exception, settings.PortName, "打开");

                    lock (stateSyncRoot)
                    {
                        state = SerialAssistantSessionState.Faulted;
                    }

                    PublishState(SerialAssistantSessionState.Faulted, message);
                    throw;
                }
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 停止定时任务、关闭传输并等待唯一接收循环完全退出。
        /// </summary>
        /// <param name="cancellationToken">取消前台关闭等待；底层安全清理仍继续收敛。</param>
        /// <returns>助手会话已退出或转入底层后台清理后的任务。</returns>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                CancellationTokenSource? cancellation;
                Task sessionReceiveTask;

                lock (stateSyncRoot)
                {
                    if (state == SerialAssistantSessionState.Disconnected && !transport.IsOpen)
                    {
                        return;
                    }

                    state = SerialAssistantSessionState.Disconnecting;
                    isDisconnectRequested = true;
                    cancellation = receiveCancellation;
                    sessionReceiveTask = receiveTask;
                }

                PublishState(SerialAssistantSessionState.Disconnecting, "正在断开串口助手…");

                await StopPeriodicSendingAsync().ConfigureAwait(false);
                await sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

                try
                {
                    await transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    cancellation?.Cancel();
                    sendGate.Release();
                }

                try
                {
                    await sessionReceiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 主动断开已明确取消唯一接收循环，属于正常收敛。
                }

                cancellation?.Dispose();

                lock (stateSyncRoot)
                {
                    receiveCancellation = null;
                    receiveTask = Task.CompletedTask;
                    state = SerialAssistantSessionState.Disconnected;
                }

                lock (cacheSyncRoot)
                {
                    isPaused = false;
                }

                PublishState(SerialAssistantSessionState.Disconnected, "串口助手已断开。");
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// 发送一项完整原始负载，不创建 Modbus 事务且不等待任何响应。
        /// </summary>
        /// <param name="payload">长度为 1 至 65536 字节的完整线路负载。</param>
        /// <param name="cancellationToken">取消写门等待或尚未完成的物理写入。</param>
        /// <returns>全部字节成功写入底层线路后的任务。</returns>
        public Task SendAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            return SendCoreAsync(payload, false, cancellationToken);
        }

        /// <summary>
        /// 冻结当前负载并启动先等待完整间隔、写完再等待下一周期的定时循环。
        /// </summary>
        /// <param name="payload">启动时复制并冻结的 1 至 65536 字节线路负载。</param>
        /// <param name="interval">100 毫秒至 1 小时的完整发送间隔。</param>
        public void StartPeriodicSending(
            ReadOnlyMemory<byte> payload,
            TimeSpan interval)
        {
            ValidatePayload(payload);

            if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromHours(1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(interval),
                    interval,
                    "定时发送间隔必须位于 100 毫秒至 1 小时。");
            }

            byte[] frozenPayload = payload.ToArray();
            CancellationTokenSource cancellation = new();

            lock (stateSyncRoot)
            {
                ThrowIfDisposedUnderLock();

                if (state != SerialAssistantSessionState.Connected)
                {
                    cancellation.Dispose();
                    throw new InvalidOperationException("串口助手尚未连接，不能启动定时发送。");
                }

                if (periodicCancellation is not null)
                {
                    cancellation.Dispose();
                    throw new InvalidOperationException("定时发送已经在运行。");
                }

                periodicCancellation = cancellation;
                periodicTask = RunPeriodicSendingAsync(
                    frozenPayload,
                    interval,
                    cancellation);
            }

            PublishPeriodicSendingChanged(true);
        }

        /// <summary>
        /// 取消定时发送并等待正在进行的唯一写入返回，确保停止后不再开始新写入。
        /// </summary>
        /// <returns>定时循环已经完全退出后的任务。</returns>
        public async Task StopPeriodicSendingAsync()
        {
            CancellationTokenSource? cancellation;
            Task task;

            lock (stateSyncRoot)
            {
                cancellation = periodicCancellation;
                task = periodicTask;
            }

            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 定时循环可能刚在另一线程完成并释放取消源，任务本身已经是终止状态。
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消间隔等待或物理写入是停止定时循环的预期路径。
            }
        }

        /// <summary>
        /// 切换暂停状态；暂停时继续排空线路，但不再保存随后到达的字节。
        /// </summary>
        /// <param name="paused">为真时暂停保存，为假时仅从恢复后的新数据继续保存。</param>
        public void SetPaused(bool paused)
        {
            lock (cacheSyncRoot)
            {
                isPaused = paused;
            }
        }

        /// <summary>
        /// 清除原始缓存以及全部收发、缓存淘汰和暂停丢弃统计。
        /// </summary>
        /// <returns>清空后原子取得的空缓存、清空代次和修订号快照。</returns>
        public SerialAssistantReceiveSnapshot ClearReceiveData()
        {
            SerialAssistantStatistics statistics;
            SerialAssistantReceiveSnapshot snapshot;

            lock (cacheSyncRoot)
            {
                receiveCache.Clear();
                receiveCacheBytes = 0;
                trafficCache.Clear();
                trafficCacheBytes = 0;
                transmitOperationCount = 0;
                transmitBytes = 0;
                receiveBytes = 0;
                discardedBytes = 0;
                pausedDiscardedBytes = 0;
                clearVersion = checked(clearVersion + 1);
                cacheRevision = checked(cacheRevision + 1);
                trafficRevision = checked(trafficRevision + 1);
                statisticsRevision = checked(statisticsRevision + 1);
                statistics = CreateStatisticsUnderLock();
                snapshot = CreateReceiveSnapshotUnderLock();
            }

            PublishStatisticsChanged(statistics);
            return snapshot;
        }

        /// <summary>
        /// 创建用于切换 UTF-8、HEX 或时间戳时完整重绘的独立缓存快照。
        /// </summary>
        /// <returns>按到达顺序排列且不与内部缓存共享可变数组的批次。</returns>
        public IReadOnlyList<SerialAssistantReceiveBatch> CreateReceiveSnapshot()
        {
            return CreateReceiveSnapshotState().Batches;
        }

        /// <summary>
        /// 原子创建用于完整重绘和界面事件排序的缓存、清空代次及修订号快照。
        /// </summary>
        /// <returns>批次、清空代次和修订号均来自同一缓存锁临界区的不可变快照。</returns>
        public SerialAssistantReceiveSnapshot CreateReceiveSnapshotState()
        {
            lock (cacheSyncRoot)
            {
                return CreateReceiveSnapshotUnderLock();
            }
        }

        /// <summary>
        /// 原子创建用于统一串口画布完整重绘的 TX/RX 缓存及排序版本快照。
        /// </summary>
        /// <returns>记录、清空代次和修订号均来自同一缓存锁临界区的不可变快照。</returns>
        public SerialAssistantTrafficSnapshot CreateTrafficSnapshotState()
        {
            lock (cacheSyncRoot)
            {
                return CreateTrafficSnapshotUnderLock();
            }
        }

        /// <summary>
        /// 停止全部后台任务、断开并永久释放本服务独占的第二套传输。
        /// </summary>
        /// <returns>全部接收、发送和物理资源清理已收敛后的值任务。</returns>
        public async ValueTask DisposeAsync()
        {
            lock (stateSyncRoot)
            {
                if (isDisposed)
                {
                    return;
                }

                isDisposed = true;
            }

            await StopPeriodicSendingAsync().ConfigureAwait(false);

            try
            {
                await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try
                {
                    await transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 永久释放继续执行传输 Dispose；关闭阶段的驱动异常不能泄漏第二套传输资源。
                }
            }

            await transport.DisposeAsync().ConfigureAwait(false);
            sendGate.Dispose();
            lifecycleGate.Dispose();
        }

        /// <summary>
        /// 运行本传输唯一的异步消费循环，并以 4096 字节或 50 毫秒先到为准发布批次。
        /// </summary>
        /// <param name="portGeneration">启动循环时捕获的串口会话代次。</param>
        /// <param name="receiveLoopCancellation">活动接收循环独占、故障或主动断开时需要释放的取消源。</param>
        /// <returns>底层序列结束、故障或取消后的任务。</returns>
        private async Task RunReceiveLoopAsync(
            int portGeneration,
            CancellationTokenSource receiveLoopCancellation)
        {
            ArgumentNullException.ThrowIfNull(receiveLoopCancellation);
            CancellationToken cancellationToken = receiveLoopCancellation.Token;
            byte[] buffer = new byte[MaximumReceiveBatchBytes];
            int bufferedBytes = 0;
            DateTimeOffset batchArrivedAtUtc = default;
            long batchStartedTimestamp = 0;
            long observedClearVersion = GetClearVersion();
            Task? batchDelay = null;
            Exception? terminalException = null;

            try
            {
                await using IAsyncEnumerator<SerialReceiveChunk> enumerator = transport
                    .ReadAllAsync(cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                Task<bool> moveNextTask = enumerator.MoveNextAsync().AsTask();

                while (true)
                {
                    if (bufferedBytes == 0)
                    {
                        observedClearVersion = GetClearVersion();
                        bool hasNext = await moveNextTask.ConfigureAwait(false);

                        if (!hasNext)
                        {
                            break;
                        }

                        ProcessReceivedChunk(
                            enumerator.Current,
                            portGeneration,
                            buffer,
                            ref bufferedBytes,
                            ref batchArrivedAtUtc,
                            ref batchStartedTimestamp,
                            ref observedClearVersion,
                            out SerialAssistantStatistics initialChunkStatistics);
                        moveNextTask = enumerator.MoveNextAsync().AsTask();

                        if (bufferedBytes == MaximumReceiveBatchBytes)
                        {
                            PublishBufferedBatch(
                                buffer,
                                ref bufferedBytes,
                                batchArrivedAtUtc,
                                portGeneration,
                                observedClearVersion);
                        }
                        else if (bufferedBytes > 0)
                        {
                            batchDelay = CreateReceiveBatchDelay(
                                batchStartedTimestamp,
                                cancellationToken);
                        }

                        PublishStatisticsChanged(initialChunkStatistics);

                        continue;
                    }

                    Task completedTask = await Task
                        .WhenAny(moveNextTask, batchDelay!)
                        .ConfigureAwait(false);

                    if (ReferenceEquals(completedTask, batchDelay))
                    {
                        await batchDelay.ConfigureAwait(false);
                        PublishBufferedBatch(
                            buffer,
                            ref bufferedBytes,
                            batchArrivedAtUtc,
                            portGeneration,
                            observedClearVersion);
                        batchDelay = null;
                        continue;
                    }

                    bool hasFollowingChunk = await moveNextTask.ConfigureAwait(false);

                    if (!hasFollowingChunk)
                    {
                        PublishBufferedBatch(
                            buffer,
                            ref bufferedBytes,
                            batchArrivedAtUtc,
                            portGeneration,
                            observedClearVersion);
                        break;
                    }

                    long previousClearVersion = observedClearVersion;
                    ProcessReceivedChunk(
                        enumerator.Current,
                        portGeneration,
                        buffer,
                        ref bufferedBytes,
                        ref batchArrivedAtUtc,
                        ref batchStartedTimestamp,
                        ref observedClearVersion,
                        out SerialAssistantStatistics followingChunkStatistics);
                    moveNextTask = enumerator.MoveNextAsync().AsTask();

                    if (observedClearVersion != previousClearVersion && bufferedBytes > 0)
                    {
                        batchDelay = CreateReceiveBatchDelay(
                            batchStartedTimestamp,
                            cancellationToken);
                    }

                    if (bufferedBytes == MaximumReceiveBatchBytes)
                    {
                        PublishBufferedBatch(
                            buffer,
                            ref bufferedBytes,
                            batchArrivedAtUtc,
                            portGeneration,
                            observedClearVersion);
                        batchDelay = null;
                    }

                    PublishStatisticsChanged(followingChunkStatistics);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 主动断开取消唯一消费循环，最终状态由 DisconnectAsync 统一发布。
            }
            catch (ChannelClosedException exception)
            {
                terminalException = exception.InnerException ?? exception;
            }
            catch (Exception exception)
            {
                terminalException = exception;
            }
            finally
            {
                bool requestedDisconnect;
                bool disposeReceiveLoopCancellation = false;

                lock (stateSyncRoot)
                {
                    requestedDisconnect = isDisconnectRequested;

                    if (!requestedDisconnect)
                    {
                        state = SerialAssistantSessionState.Disconnecting;
                        periodicCancellation?.Cancel();
                    }
                }

                if (!requestedDisconnect)
                {
                    try
                    {
                        await transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 接收故障已经决定会话结果，清理异常不覆盖原始原因。
                    }

                    lock (stateSyncRoot)
                    {
                        requestedDisconnect = isDisconnectRequested;

                        if (!requestedDisconnect)
                        {
                            state = SerialAssistantSessionState.Faulted;

                            if (ReferenceEquals(
                                receiveCancellation,
                                receiveLoopCancellation))
                            {
                                receiveCancellation = null;
                                disposeReceiveLoopCancellation = true;
                            }
                        }
                    }

                    if (!requestedDisconnect)
                    {
                        string message = terminalException is null
                            ? "串口设备已断开，接收会话已经结束。"
                            : TranslateSerialException(terminalException, string.Empty, "接收");
                        PublishState(SerialAssistantSessionState.Faulted, message);
                    }
                }

                if (disposeReceiveLoopCancellation)
                {
                    receiveLoopCancellation.Dispose();
                }
            }
        }

        /// <summary>
        /// 等待连接状态、取消源和任务身份全部提交后再进入唯一接收循环，隔离打开即 EOF 的竞态。
        /// </summary>
        /// <param name="startSignal">组合根已经原子提交活动会话状态时完成的启动信号。</param>
        /// <param name="portGeneration">启动循环时捕获的串口会话代次。</param>
        /// <param name="receiveLoopCancellation">活动接收循环独占、故障收敛时需要释放的取消源。</param>
        /// <returns>启动信号到达并运行完整接收循环后的任务。</returns>
        private async Task RunReceiveLoopAfterStartAsync(
            Task startSignal,
            int portGeneration,
            CancellationTokenSource receiveLoopCancellation)
        {
            ArgumentNullException.ThrowIfNull(startSignal);
            ArgumentNullException.ThrowIfNull(receiveLoopCancellation);
            await startSignal.ConfigureAwait(false);
            await RunReceiveLoopAsync(
                portGeneration,
                receiveLoopCancellation).ConfigureAwait(false);
        }

        /// <summary>
        /// 统计并把一个底层接收块按暂停和 4096 字节边界送入当前合并缓冲。
        /// </summary>
        /// <param name="chunk">底层唯一读取循环产生的不可变接收块。</param>
        /// <param name="portGeneration">当前服务接收循环的会话代次。</param>
        /// <param name="buffer">当前接收合并缓冲。</param>
        /// <param name="bufferedBytes">缓冲中已经写入的字节数。</param>
        /// <param name="batchArrivedAtUtc">当前批次首字节的 UTC 到达时刻。</param>
        /// <param name="batchStartedTimestamp">当前批次首字节被服务观察到的单调时间戳。</param>
        /// <param name="observedClearVersion">当前合并缓冲对应的清空代次。</param>
        /// <param name="statistics">本接收块计数完成后的不可变统计快照。</param>
        private void ProcessReceivedChunk(
            SerialReceiveChunk chunk,
            int portGeneration,
            byte[] buffer,
            ref int bufferedBytes,
            ref DateTimeOffset batchArrivedAtUtc,
            ref long batchStartedTimestamp,
            ref long observedClearVersion,
            out SerialAssistantStatistics statistics)
        {
            if (chunk.PortGeneration != portGeneration)
            {
                throw new InvalidOperationException("接收循环读取到不属于当前串口会话代次的数据块。");
            }

            ReadOnlyMemory<byte> data = chunk.Data;
            bool paused;
            long currentClearVersion;
            long chunkObservedTimestamp = timeProvider.GetTimestamp();

            lock (cacheSyncRoot)
            {
                receiveBytes = checked(receiveBytes + data.Length);
                paused = isPaused;
                currentClearVersion = clearVersion;

                if (paused)
                {
                    discardedBytes = checked(discardedBytes + data.Length);
                    pausedDiscardedBytes = checked(pausedDiscardedBytes + data.Length);
                }

                statisticsRevision = checked(statisticsRevision + 1);
                statistics = CreateStatisticsUnderLock();
            }

            if (paused)
            {
                return;
            }

            if (currentClearVersion != observedClearVersion)
            {
                bufferedBytes = 0;
                observedClearVersion = currentClearVersion;
            }

            ReadOnlySpan<byte> remaining = data.Span;

            while (!remaining.IsEmpty)
            {
                if (bufferedBytes == 0)
                {
                    batchArrivedAtUtc = chunk.ArrivedAtUtc;
                    batchStartedTimestamp = chunkObservedTimestamp;
                }

                int copyCount = Math.Min(
                    MaximumReceiveBatchBytes - bufferedBytes,
                    remaining.Length);
                remaining[..copyCount].CopyTo(buffer.AsSpan(bufferedBytes));
                bufferedBytes += copyCount;
                remaining = remaining[copyCount..];

                if (bufferedBytes == MaximumReceiveBatchBytes && !remaining.IsEmpty)
                {
                    PublishBufferedBatch(
                        buffer,
                        ref bufferedBytes,
                        batchArrivedAtUtc,
                        portGeneration,
                        observedClearVersion);
                }
            }
        }

        /// <summary>
        /// 按首字节到达后的绝对截止时刻创建合批延迟，避免后台调度滞后把 50 毫秒窗口重复后移。
        /// </summary>
        /// <param name="batchStartedTimestamp">当前批次首字节被服务观察到的单调时间戳。</param>
        /// <param name="cancellationToken">主动断开或永久释放时取消延迟的令牌。</param>
        /// <returns>在原始 50 毫秒截止点完成的延迟任务；截止点已过时立即完成。</returns>
        private Task CreateReceiveBatchDelay(
            long batchStartedTimestamp,
            CancellationToken cancellationToken)
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(
                batchStartedTimestamp,
                timeProvider.GetTimestamp());
            TimeSpan remaining = ReceiveBatchWindow - elapsed;

            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            return Task.Delay(
                remaining,
                timeProvider,
                cancellationToken);
        }

        /// <summary>
        /// 把当前非空合并缓冲写入有界缓存，并在必要时淘汰最旧完整批次。
        /// </summary>
        /// <param name="buffer">保存待发布字节的固定 4096 字节缓冲。</param>
        /// <param name="bufferedBytes">实际待发布字节数；发布后重置为零。</param>
        /// <param name="arrivedAtUtc">批次首字节的 UTC 到达时刻。</param>
        /// <param name="portGeneration">产生批次的串口会话代次。</param>
        /// <param name="batchClearVersion">批次开始时捕获的清空代次。</param>
        private void PublishBufferedBatch(
            byte[] buffer,
            ref int bufferedBytes,
            DateTimeOffset arrivedAtUtc,
            int portGeneration,
            long batchClearVersion)
        {
            if (bufferedBytes == 0)
            {
                return;
            }

            SerialAssistantReceiveBatch batch = new(
                buffer.AsSpan(0, bufferedBytes),
                arrivedAtUtc,
                portGeneration);
            SerialAssistantTrafficBatch trafficBatch = new(
                SerialAssistantTrafficDirection.Receive,
                buffer.AsSpan(0, bufferedBytes),
                arrivedAtUtc,
                portGeneration);
            bufferedBytes = 0;
            bool evicted = false;
            bool trafficEvicted;
            SerialAssistantStatistics? statistics = null;
            long publishedCacheRevision;
            long publishedTrafficRevision;

            lock (cacheSyncRoot)
            {
                if (batchClearVersion != clearVersion)
                {
                    return;
                }

                while (receiveCacheBytes + batch.ByteCount > ReceiveCacheCapacityBytes)
                {
                    SerialAssistantReceiveBatch oldest = receiveCache.First!.Value;
                    receiveCache.RemoveFirst();
                    receiveCacheBytes -= oldest.ByteCount;
                    discardedBytes = checked(discardedBytes + oldest.ByteCount);
                    evicted = true;
                }

                receiveCache.AddLast(batch);
                receiveCacheBytes += batch.ByteCount;
                cacheRevision = checked(cacheRevision + 1);
                publishedCacheRevision = cacheRevision;
                CacheTrafficBatchUnderLock(
                    trafficBatch,
                    out trafficEvicted,
                    out publishedTrafficRevision);

                if (evicted)
                {
                    statisticsRevision = checked(statisticsRevision + 1);
                    statistics = CreateStatisticsUnderLock();
                }
            }

            PublishReceiveUpdated(
                new SerialAssistantReceiveUpdate(
                    batch,
                    evicted,
                    batchClearVersion,
                    publishedCacheRevision));
            PublishTrafficUpdated(
                new SerialAssistantTrafficUpdate(
                    trafficBatch,
                    trafficEvicted,
                    batchClearVersion,
                    publishedTrafficRevision));

            if (statistics is not null)
            {
                PublishStatisticsChanged(statistics);
            }
        }

        /// <summary>
        /// 执行手动或定时原始写入，并仅在授权写入成功后更新统计。
        /// </summary>
        /// <param name="payload">待发送的完整线路负载。</param>
        /// <param name="isPeriodicWrite">为真时允许当前定时循环调用；手动调用在定时期间被拒绝。</param>
        /// <param name="cancellationToken">取消写门等待或物理写入的令牌。</param>
        /// <returns>写入成功并更新统计后的任务。</returns>
        private async Task SendCoreAsync(
            ReadOnlyMemory<byte> payload,
            bool isPeriodicWrite,
            CancellationToken cancellationToken)
        {
            ValidatePayload(payload);
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                int expectedGeneration;

                lock (stateSyncRoot)
                {
                    ThrowIfDisposedUnderLock();

                    if (state != SerialAssistantSessionState.Connected)
                    {
                        throw new InvalidOperationException("串口助手尚未连接，不能发送数据。");
                    }

                    if (!isPeriodicWrite && periodicCancellation is not null)
                    {
                        throw new InvalidOperationException("定时发送运行期间不能手动发送。");
                    }

                    expectedGeneration = transport.PortGeneration;
                }

                bool writeAuthorized = false;
                await transport.WriteAsync(
                    payload,
                    () =>
                    {
                        lock (stateSyncRoot)
                        {
                            writeAuthorized =
                                state == SerialAssistantSessionState.Connected &&
                                transport.PortGeneration == expectedGeneration &&
                                (isPeriodicWrite || periodicCancellation is null);
                            return writeAuthorized;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!writeAuthorized)
                {
                    throw new InvalidOperationException("发送前串口会话已经失效，未写入任何数据。");
                }

                SerialAssistantTrafficBatch trafficBatch = new(
                    SerialAssistantTrafficDirection.Transmit,
                    payload.Span,
                    timeProvider.GetUtcNow(),
                    expectedGeneration);
                SerialAssistantStatistics statistics;
                bool trafficEvicted;
                long trafficClearVersion;
                long publishedTrafficRevision;

                lock (cacheSyncRoot)
                {
                    transmitOperationCount = checked(transmitOperationCount + 1);
                    transmitBytes = checked(transmitBytes + payload.Length);
                    statisticsRevision = checked(statisticsRevision + 1);
                    statistics = CreateStatisticsUnderLock();
                    trafficClearVersion = clearVersion;
                    CacheTrafficBatchUnderLock(
                        trafficBatch,
                        out trafficEvicted,
                        out publishedTrafficRevision);
                }

                PublishStatisticsChanged(statistics);
                PublishTrafficUpdated(
                    new SerialAssistantTrafficUpdate(
                        trafficBatch,
                        trafficEvicted,
                        trafficClearVersion,
                        publishedTrafficRevision));
            }
            finally
            {
                sendGate.Release();
            }
        }

        /// <summary>
        /// 运行先等待完整间隔、每次写完再等待下一周期的无补发定时循环。
        /// </summary>
        /// <param name="frozenPayload">启动时复制并冻结的线路负载。</param>
        /// <param name="interval">每次写入开始前必须完整等待的间隔。</param>
        /// <param name="cancellation">本轮定时循环的唯一取消源身份。</param>
        /// <returns>取消、断开或写故障后的收敛任务。</returns>
        private async Task RunPeriodicSendingAsync(
            byte[] frozenPayload,
            TimeSpan interval,
            CancellationTokenSource cancellation)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(
                        interval,
                        timeProvider,
                        cancellation.Token).ConfigureAwait(false);
                    await SendCoreAsync(
                        frozenPayload,
                        true,
                        cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // 用户停止或连接故障取消定时循环，禁止开始后续写入。
            }
            catch (Exception exception)
            {
                PublishState(
                    State,
                    TranslateSerialException(exception, string.Empty, "定时发送"));
            }
            finally
            {
                bool wasCurrentRun;

                lock (stateSyncRoot)
                {
                    wasCurrentRun = ReferenceEquals(periodicCancellation, cancellation);

                    if (wasCurrentRun)
                    {
                        periodicCancellation = null;
                        periodicTask = Task.CompletedTask;
                    }
                }

                cancellation.Dispose();

                if (wasCurrentRun)
                {
                    PublishPeriodicSendingChanged(false);
                }
            }
        }

        /// <summary>
        /// 创建调用方已持有缓存锁时的一致统计快照。
        /// </summary>
        /// <returns>包含全部当前计数的不可变快照。</returns>
        private SerialAssistantStatistics CreateStatisticsUnderLock()
        {
            return new SerialAssistantStatistics(
                transmitOperationCount,
                transmitBytes,
                receiveBytes,
                discardedBytes,
                pausedDiscardedBytes,
                clearVersion,
                statisticsRevision);
        }

        /// <summary>
        /// 在已持有缓存锁时复制全部批次，并附带同一时刻的清空代次和缓存修订号。
        /// </summary>
        /// <returns>不与内部缓存共享可变数组的原子快照。</returns>
        private SerialAssistantReceiveSnapshot CreateReceiveSnapshotUnderLock()
        {
            SerialAssistantReceiveBatch[] batches = receiveCache
                .Select(
                    batch => new SerialAssistantReceiveBatch(
                        batch.Data.Span,
                        batch.ArrivedAtUtc,
                        batch.PortGeneration))
                .ToArray();
            return new SerialAssistantReceiveSnapshot(
                batches,
                clearVersion,
                cacheRevision);
        }

        /// <summary>
        /// 在调用方持有缓存锁时写入一项统一收发记录，并按最旧完整记录执行容量淘汰。
        /// </summary>
        /// <param name="batch">需要追加到统一画布缓存的非空 TX 或 RX 记录。</param>
        /// <param name="evicted">返回本次写入是否淘汰了至少一项旧记录。</param>
        /// <param name="publishedTrafficRevision">返回本次写入后的统一缓存修订号。</param>
        private void CacheTrafficBatchUnderLock(
            SerialAssistantTrafficBatch batch,
            out bool evicted,
            out long publishedTrafficRevision)
        {
            ArgumentNullException.ThrowIfNull(batch);
            evicted = false;

            while (trafficCacheBytes + batch.ByteCount > ReceiveCacheCapacityBytes)
            {
                SerialAssistantTrafficBatch oldest = trafficCache.First!.Value;
                trafficCache.RemoveFirst();
                trafficCacheBytes -= oldest.ByteCount;
                evicted = true;
            }

            trafficCache.AddLast(batch);
            trafficCacheBytes += batch.ByteCount;
            trafficRevision = checked(trafficRevision + 1);
            publishedTrafficRevision = trafficRevision;
        }

        /// <summary>
        /// 在调用方持有缓存锁时复制统一 TX/RX 缓存和同一时刻的排序版本。
        /// </summary>
        /// <returns>不与内部缓存共享数组的统一串口画布快照。</returns>
        private SerialAssistantTrafficSnapshot CreateTrafficSnapshotUnderLock()
        {
            SerialAssistantTrafficBatch[] batches = trafficCache
                .Select(
                    batch => new SerialAssistantTrafficBatch(
                        batch.Direction,
                        batch.Data.Span,
                        batch.RecordedAtUtc,
                        batch.PortGeneration))
                .ToArray();
            return new SerialAssistantTrafficSnapshot(
                batches,
                clearVersion,
                trafficRevision);
        }

        /// <summary>
        /// 获取当前清空代次以隔离清空前的尚未发布合并缓冲。
        /// </summary>
        /// <returns>当前单调递增的清空代次。</returns>
        private long GetClearVersion()
        {
            lock (cacheSyncRoot)
            {
                return clearVersion;
            }
        }

        /// <summary>
        /// 校验单次发送负载的非空和 64 KiB 上限。
        /// </summary>
        /// <param name="payload">待校验的完整线路负载。</param>
        private static void ValidatePayload(ReadOnlyMemory<byte> payload)
        {
            if (payload.IsEmpty)
            {
                throw new ArgumentException("发送负载不能为空。", nameof(payload));
            }

            if (payload.Length > MaximumPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payload),
                    payload.Length,
                    $"单次发送负载不能超过 {MaximumPayloadBytes} 字节。");
            }
        }

        /// <summary>
        /// 把端口冲突、外部占用、拔线和驱动异常转换为明确中文消息。
        /// </summary>
        /// <param name="exception">底层打开、读取或写入异常。</param>
        /// <param name="portName">已知目标端口名称；未知时为空。</param>
        /// <param name="operationName">发生异常的中文操作名称。</param>
        /// <returns>可直接显示给用户且保留操作上下文的中文消息。</returns>
        public static string TranslateSerialException(
            Exception exception,
            string portName,
            string operationName)
        {
            ArgumentNullException.ThrowIfNull(exception);
            string portText = string.IsNullOrWhiteSpace(portName)
                ? "串口"
                : $"串口 {portName.Trim()}";

            return exception switch
            {
                SerialPortInUseException portInUseException => portInUseException.Message,
                UnauthorizedAccessException =>
                    $"{portText} 无法{operationName}：端口可能已被外部程序占用或当前账户没有访问权限。",
                IOException =>
                    $"{portText}{operationName}失败：设备可能已拔出，或串口驱动发生 I/O 异常。",
                ObjectDisposedException =>
                    $"{portText}{operationName}失败：串口驱动句柄已经失效。",
                InvalidOperationException invalidOperationException =>
                    $"{portText}{operationName}失败：{invalidOperationException.Message}",
                _ => $"{portText}{operationName}失败：{exception.Message}",
            };
        }

        /// <summary>
        /// 在已持有状态锁时拒绝永久释放后的操作。
        /// </summary>
        private void ThrowIfDisposedUnderLock()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
        }

        /// <summary>
        /// 隔离观察者异常后发布连接状态变化。
        /// </summary>
        /// <param name="newState">变化后的连接状态。</param>
        /// <param name="message">面向用户显示的中文状态说明。</param>
        private void PublishState(
            SerialAssistantSessionState newState,
            string message)
        {
            PublishObservers(
                StateChanged,
                new SerialAssistantSessionStateChange(newState, message));
        }

        /// <summary>
        /// 隔离观察者异常后发布接收缓存更新。
        /// </summary>
        /// <param name="update">增量批次和完整重绘标记。</param>
        private void PublishReceiveUpdated(SerialAssistantReceiveUpdate update)
        {
            PublishObservers(ReceiveUpdated, update);
        }

        /// <summary>
        /// 隔离观察者异常后发布统一 TX/RX 画布缓存更新。
        /// </summary>
        /// <param name="update">新收发记录、淘汰标记及排序版本。</param>
        private void PublishTrafficUpdated(SerialAssistantTrafficUpdate update)
        {
            PublishObservers(TrafficUpdated, update);
        }

        /// <summary>
        /// 隔离观察者异常后发布统计快照。
        /// </summary>
        /// <param name="statistics">最新不可变统计快照。</param>
        private void PublishStatisticsChanged(SerialAssistantStatistics statistics)
        {
            PublishObservers(StatisticsChanged, statistics);
        }

        /// <summary>
        /// 隔离观察者异常后发布定时发送运行开关。
        /// </summary>
        /// <param name="isRunning">定时循环正在运行或收敛时为真。</param>
        private void PublishPeriodicSendingChanged(bool isRunning)
        {
            PublishObservers(PeriodicSendingChanged, isRunning);
        }

        /// <summary>
        /// 逐一调用事件观察者，并阻止界面观察者异常破坏串口后台生命周期。
        /// </summary>
        /// <typeparam name="T">事件不可变负载类型。</typeparam>
        /// <param name="observers">当前事件观察者委托。</param>
        /// <param name="value">传递给每个观察者的不可变值。</param>
        private static void PublishObservers<T>(
            Action<T>? observers,
            T value)
        {
            if (observers is null)
            {
                return;
            }

            foreach (Action<T> observer in observers
                .GetInvocationList()
                .Cast<Action<T>>())
            {
                try
                {
                    observer(value);
                }
                catch (Exception)
                {
                    // 界面或测试观察者故障不能中止唯一串口接收与清理循环。
                }
            }
        }
    }
}
