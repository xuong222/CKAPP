namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 指定普通串口助手会话的连接生命周期状态。
    /// </summary>
    public enum SerialAssistantSessionState
    {
        /// <summary>
        /// 当前没有活动串口会话。
        /// </summary>
        Disconnected = 0,

        /// <summary>
        /// 正在打开目标串口。
        /// </summary>
        Connecting = 1,

        /// <summary>
        /// 串口已经打开且唯一接收循环正在运行。
        /// </summary>
        Connected = 2,

        /// <summary>
        /// 正在停止定时任务并收敛接收循环。
        /// </summary>
        Disconnecting = 3,

        /// <summary>
        /// 打开、读取或驱动发生故障，等待用户处理或重连。
        /// </summary>
        Faulted = 4,
    }

    /// <summary>
    /// 表示串口助手连接状态的一次不可变变化通知。
    /// </summary>
    public sealed class SerialAssistantSessionStateChange
    {
        /// <summary>
        /// 初始化一项连接状态变化。
        /// </summary>
        /// <param name="state">变化后的会话状态。</param>
        /// <param name="message">可直接显示给用户的中文状态说明。</param>
        public SerialAssistantSessionStateChange(
            SerialAssistantSessionState state,
            string message)
        {
            State = state;
            Message = message ?? string.Empty;
        }

        /// <summary>
        /// 获取变化后的会话状态。
        /// </summary>
        public SerialAssistantSessionState State { get; }

        /// <summary>
        /// 获取可直接显示给用户的中文状态说明。
        /// </summary>
        public string Message { get; }
    }

    /// <summary>
    /// 表示一个不超过 4096 字节、带到达时刻的不可变接收显示批次。
    /// </summary>
    public sealed class SerialAssistantReceiveBatch
    {
        /// <summary>
        /// 当前实例独占持有的原始线路字节。
        /// </summary>
        private readonly byte[] data;

        /// <summary>
        /// 初始化一个独占原始字节副本的接收批次。
        /// </summary>
        /// <param name="data">本批次非空原始线路字节。</param>
        /// <param name="arrivedAtUtc">本批次首字节进入传输层的 UTC 时刻。</param>
        /// <param name="portGeneration">产生本批次的串口会话代次。</param>
        public SerialAssistantReceiveBatch(
            ReadOnlySpan<byte> data,
            DateTimeOffset arrivedAtUtc,
            int portGeneration)
        {
            if (data.IsEmpty)
            {
                throw new ArgumentException("串口助手接收批次不能为空。", nameof(data));
            }

            if (portGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(portGeneration));
            }

            this.data = data.ToArray();
            ArrivedAtUtc = arrivedAtUtc.ToUniversalTime();
            PortGeneration = portGeneration;
        }

        /// <summary>
        /// 获取原始线路字节的防御性副本。
        /// </summary>
        public ReadOnlyMemory<byte> Data => (byte[])data.Clone();

        /// <summary>
        /// 获取批次首字节进入传输层的 UTC 时刻。
        /// </summary>
        public DateTimeOffset ArrivedAtUtc { get; }

        /// <summary>
        /// 获取产生本批次的串口会话代次。
        /// </summary>
        public int PortGeneration { get; }

        /// <summary>
        /// 获取批次原始字节数，避免统计路径为读取长度而复制数组。
        /// </summary>
        internal int ByteCount => data.Length;
    }

    /// <summary>
    /// 表示缓存写入后供界面增量刷新或完整重绘的一次通知。
    /// </summary>
    public sealed class SerialAssistantReceiveUpdate
    {
        /// <summary>
        /// 初始化一次接收缓存更新通知。
        /// </summary>
        /// <param name="batch">刚写入有界缓存的新批次。</param>
        /// <param name="requiresFullRefresh">本次写入是否淘汰了旧批次，因而需要完整重绘。</param>
        /// <param name="clearVersion">本批次所属的最近一次清空代次。</param>
        /// <param name="cacheRevision">本批次写入后缓存的单调递增修订号。</param>
        public SerialAssistantReceiveUpdate(
            SerialAssistantReceiveBatch batch,
            bool requiresFullRefresh,
            long clearVersion,
            long cacheRevision)
        {
            ArgumentNullException.ThrowIfNull(batch);

            if (clearVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clearVersion));
            }

            if (cacheRevision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cacheRevision));
            }

            Batch = batch;
            RequiresFullRefresh = requiresFullRefresh;
            ClearVersion = clearVersion;
            CacheRevision = cacheRevision;
        }

        /// <summary>
        /// 获取刚写入有界缓存的新批次。
        /// </summary>
        public SerialAssistantReceiveBatch Batch { get; }

        /// <summary>
        /// 获取是否因旧批次淘汰而必须从缓存快照完整重绘。
        /// </summary>
        public bool RequiresFullRefresh { get; }

        /// <summary>
        /// 获取本批次所属的最近一次清空代次，用于拒绝清空前已经排队的界面更新。
        /// </summary>
        public long ClearVersion { get; }

        /// <summary>
        /// 获取本批次写入后缓存的单调递增修订号，用于检测重复或跳号更新。
        /// </summary>
        public long CacheRevision { get; }
    }

    /// <summary>
    /// 表示一次原子取得的接收缓存、清空代次和修订号快照。
    /// </summary>
    public sealed class SerialAssistantReceiveSnapshot
    {
        /// <summary>
        /// 初始化一项不可变接收缓存快照。
        /// </summary>
        /// <param name="batches">按到达顺序排列的独立接收批次。</param>
        /// <param name="clearVersion">创建快照时最近一次清空代次。</param>
        /// <param name="cacheRevision">创建快照时缓存的单调递增修订号。</param>
        public SerialAssistantReceiveSnapshot(
            IReadOnlyList<SerialAssistantReceiveBatch> batches,
            long clearVersion,
            long cacheRevision)
        {
            ArgumentNullException.ThrowIfNull(batches);

            if (clearVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clearVersion));
            }

            if (cacheRevision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cacheRevision));
            }

            Batches = batches;
            ClearVersion = clearVersion;
            CacheRevision = cacheRevision;
        }

        /// <summary>
        /// 获取按到达顺序排列且不共享可变数组的接收批次。
        /// </summary>
        public IReadOnlyList<SerialAssistantReceiveBatch> Batches { get; }

        /// <summary>
        /// 获取创建快照时最近一次清空代次。
        /// </summary>
        public long ClearVersion { get; }

        /// <summary>
        /// 获取创建快照时缓存的单调递增修订号。
        /// </summary>
        public long CacheRevision { get; }
    }

    /// <summary>
    /// 指定统一串口画布中一项原始数据来自接收线路还是成功发送。
    /// </summary>
    public enum SerialAssistantTrafficDirection
    {
        /// <summary>
        /// 数据由串口驱动接收并进入助手缓存。
        /// </summary>
        Receive = 0,

        /// <summary>
        /// 数据已经成功写入串口线路。
        /// </summary>
        Transmit = 1,
    }

    /// <summary>
    /// 表示统一串口画布中的一项不可变 TX 或 RX 原始数据记录。
    /// </summary>
    public sealed class SerialAssistantTrafficBatch
    {
        /// <summary>
        /// 当前记录独占持有的原始线路字节。
        /// </summary>
        private readonly byte[] data;

        /// <summary>
        /// 初始化一项拥有独立原始字节副本的收发记录。
        /// </summary>
        /// <param name="direction">本项数据是成功发送还是线路接收。</param>
        /// <param name="data">本项非空原始线路字节。</param>
        /// <param name="recordedAtUtc">发送完成或接收批次首字节到达的 UTC 时刻。</param>
        /// <param name="portGeneration">产生本项数据的串口会话代次。</param>
        public SerialAssistantTrafficBatch(
            SerialAssistantTrafficDirection direction,
            ReadOnlySpan<byte> data,
            DateTimeOffset recordedAtUtc,
            int portGeneration)
        {
            if (!Enum.IsDefined(direction))
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }

            if (data.IsEmpty)
            {
                throw new ArgumentException("串口助手收发记录不能为空。", nameof(data));
            }

            if (portGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(portGeneration));
            }

            Direction = direction;
            this.data = data.ToArray();
            RecordedAtUtc = recordedAtUtc.ToUniversalTime();
            PortGeneration = portGeneration;
        }

        /// <summary>
        /// 获取本项数据的 TX 或 RX 方向。
        /// </summary>
        public SerialAssistantTrafficDirection Direction { get; }

        /// <summary>
        /// 获取原始线路字节的防御性副本。
        /// </summary>
        public ReadOnlyMemory<byte> Data => (byte[])data.Clone();

        /// <summary>
        /// 获取发送完成或接收批次首字节到达的 UTC 时刻。
        /// </summary>
        public DateTimeOffset RecordedAtUtc { get; }

        /// <summary>
        /// 获取产生本项数据的串口会话代次。
        /// </summary>
        public int PortGeneration { get; }

        /// <summary>
        /// 获取记录原始字节数，供有界缓存计算占用且不复制数组。
        /// </summary>
        internal int ByteCount => data.Length;
    }

    /// <summary>
    /// 表示统一收发缓存写入后供串口画布增量刷新的一次通知。
    /// </summary>
    public sealed class SerialAssistantTrafficUpdate
    {
        /// <summary>
        /// 初始化一次统一收发缓存更新通知。
        /// </summary>
        /// <param name="batch">刚写入有界缓存的新 TX 或 RX 记录。</param>
        /// <param name="requiresFullRefresh">本次写入是否淘汰旧记录并要求完整重绘。</param>
        /// <param name="clearVersion">本项记录所属的最近一次清空代次。</param>
        /// <param name="trafficRevision">本次写入后的统一收发缓存修订号。</param>
        public SerialAssistantTrafficUpdate(
            SerialAssistantTrafficBatch batch,
            bool requiresFullRefresh,
            long clearVersion,
            long trafficRevision)
        {
            ArgumentNullException.ThrowIfNull(batch);

            if (clearVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clearVersion));
            }

            if (trafficRevision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trafficRevision));
            }

            Batch = batch;
            RequiresFullRefresh = requiresFullRefresh;
            ClearVersion = clearVersion;
            TrafficRevision = trafficRevision;
        }

        /// <summary>
        /// 获取刚写入缓存的新 TX 或 RX 记录。
        /// </summary>
        public SerialAssistantTrafficBatch Batch { get; }

        /// <summary>
        /// 获取是否必须从统一缓存快照完整重绘画布。
        /// </summary>
        public bool RequiresFullRefresh { get; }

        /// <summary>
        /// 获取本项记录所属的最近一次清空代次。
        /// </summary>
        public long ClearVersion { get; }

        /// <summary>
        /// 获取本次写入后的统一收发缓存修订号。
        /// </summary>
        public long TrafficRevision { get; }
    }

    /// <summary>
    /// 表示统一串口画布所需的 TX/RX 缓存及排序版本原子快照。
    /// </summary>
    public sealed class SerialAssistantTrafficSnapshot
    {
        /// <summary>
        /// 初始化一项不可变统一收发缓存快照。
        /// </summary>
        /// <param name="batches">按实际收发发生顺序排列的独立记录。</param>
        /// <param name="clearVersion">创建快照时最近一次清空代次。</param>
        /// <param name="trafficRevision">创建快照时统一收发缓存修订号。</param>
        public SerialAssistantTrafficSnapshot(
            IReadOnlyList<SerialAssistantTrafficBatch> batches,
            long clearVersion,
            long trafficRevision)
        {
            ArgumentNullException.ThrowIfNull(batches);

            if (clearVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clearVersion));
            }

            if (trafficRevision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trafficRevision));
            }

            Batches = batches;
            ClearVersion = clearVersion;
            TrafficRevision = trafficRevision;
        }

        /// <summary>
        /// 获取按实际发生顺序排列且不共享可变数组的 TX/RX 记录。
        /// </summary>
        public IReadOnlyList<SerialAssistantTrafficBatch> Batches { get; }

        /// <summary>
        /// 获取创建快照时最近一次清空代次。
        /// </summary>
        public long ClearVersion { get; }

        /// <summary>
        /// 获取创建快照时统一收发缓存修订号。
        /// </summary>
        public long TrafficRevision { get; }
    }

    /// <summary>
    /// 表示串口助手从本次清空开始累计的不可变收发统计。
    /// </summary>
    public sealed class SerialAssistantStatistics
    {
        /// <summary>
        /// 初始化一项完整统计快照。
        /// </summary>
        /// <param name="transmitOperationCount">成功完成的发送操作次数。</param>
        /// <param name="transmitBytes">成功完成发送的线路字节数。</param>
        /// <param name="receiveBytes">从驱动接收并排空的线路字节数。</param>
        /// <param name="discardedBytes">因暂停或缓存淘汰而未保留的总字节数。</param>
        /// <param name="pausedDiscardedBytes">暂停期间读取但未保留的字节数。</param>
        /// <param name="clearVersion">创建统计时最近一次清空代次。</param>
        /// <param name="statisticsRevision">每次统计改变时单调递增的修订号。</param>
        public SerialAssistantStatistics(
            long transmitOperationCount,
            long transmitBytes,
            long receiveBytes,
            long discardedBytes,
            long pausedDiscardedBytes,
            long clearVersion,
            long statisticsRevision)
        {
            if (clearVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clearVersion));
            }

            if (statisticsRevision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(statisticsRevision));
            }

            TransmitOperationCount = transmitOperationCount;
            TransmitBytes = transmitBytes;
            ReceiveBytes = receiveBytes;
            DiscardedBytes = discardedBytes;
            PausedDiscardedBytes = pausedDiscardedBytes;
            ClearVersion = clearVersion;
            StatisticsRevision = statisticsRevision;
        }

        /// <summary>
        /// 获取成功完成的发送操作次数。
        /// </summary>
        public long TransmitOperationCount { get; }

        /// <summary>
        /// 获取成功完成发送的线路字节数。
        /// </summary>
        public long TransmitBytes { get; }

        /// <summary>
        /// 获取从驱动接收并排空的线路字节数。
        /// </summary>
        public long ReceiveBytes { get; }

        /// <summary>
        /// 获取因暂停或缓存淘汰而未保留的总字节数。
        /// </summary>
        public long DiscardedBytes { get; }

        /// <summary>
        /// 获取暂停期间读取但未保留的字节数。
        /// </summary>
        public long PausedDiscardedBytes { get; }

        /// <summary>
        /// 获取创建统计时最近一次清空代次，用于拒绝清空前已经排队的界面统计。
        /// </summary>
        public long ClearVersion { get; }

        /// <summary>
        /// 获取每次统计改变时单调递增的修订号，用于拒绝并发发布产生的迟到统计。
        /// </summary>
        public long StatisticsRevision { get; }
    }
}
