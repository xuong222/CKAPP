using System.Buffers.Binary;
using System.Net;

using CH32UpperComputer.Core.Iap;
using CH32UpperComputer.Infrastructure.Iap;

namespace CH32UpperComputer.Testing
{
    /// <summary>
    /// 控制模拟 Bootloader 下一次 DATA 响应的可取消暂停点。
    /// </summary>
    public sealed class BootloaderSimulatorPause
    {
        /// <summary>
        /// DATA 响应读取已经到达暂停点的信号。
        /// </summary>
        private readonly TaskCompletionSource entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 测试允许模拟响应继续的信号。
        /// </summary>
        private readonly TaskCompletionSource released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 获取 DATA 响应读取已经进入暂停点的任务。
        /// </summary>
        public Task Entered => entered.Task;

        /// <summary>
        /// 幂等允许已经暂停的 DATA 响应继续。
        /// </summary>
        public void Release()
        {
            released.TrySetResult();
        }

        /// <summary>
        /// 由模拟传输标记 DATA 响应读取已经到达暂停点。
        /// </summary>
        internal void SignalEntered()
        {
            entered.TrySetResult();
        }

        /// <summary>
        /// 等待测试释放暂停点或调用方取消事务。
        /// </summary>
        /// <param name="cancellationToken">取消模拟响应等待。</param>
        /// <returns>暂停点释放后的任务。</returns>
        internal Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            return released.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 定义模拟 Bootloader 对 RUN 请求的响应方式。
    /// </summary>
    public enum BootloaderSimulatorRunBehavior
    {
        /// <summary>
        /// 返回完整匹配的 RUN ACK。
        /// </summary>
        Acknowledge,

        /// <summary>
        /// 返回明确匹配的 RUN NACK。
        /// </summary>
        Reject,

        /// <summary>
        /// 假定 RUN 已收到但不返回完整响应。
        /// </summary>
        Timeout,
    }

    /// <summary>
    /// 定义模拟 Bootloader 收到的一条完整请求记录。
    /// </summary>
    /// <param name="ConnectionId">请求所属的模拟 TCP 连接编号。</param>
    /// <param name="Command">请求命令。</param>
    /// <param name="Sequence">连接内请求序号。</param>
    /// <param name="Offset">DATA 相对偏移。</param>
    /// <param name="Length">BEGIN 镜像长度或 DATA 分片长度。</param>
    public sealed record BootloaderSimulatorRequest(
        int ConnectionId,
        IapCommand Command,
        uint Sequence,
        uint Offset,
        uint Length);

    /// <summary>
    /// 在内存中实现当前 CH32 Bootloader 协议并可主动制造关键网络故障。
    /// </summary>
    public sealed class BootloaderSimulator : IIapTransport
    {
        /// <summary>
        /// 保护连接、响应队列、镜像状态和请求历史。
        /// </summary>
        private readonly object sync = new();

        /// <summary>
        /// 等待上位机精确读取的响应字节。
        /// </summary>
        private readonly Queue<byte> responseBytes = new();

        /// <summary>
        /// 按接收顺序保存请求历史。
        /// </summary>
        private readonly List<BootloaderSimulatorRequest> requests = [];

        /// <summary>
        /// 当前 BEGIN 声明的镜像长度。
        /// </summary>
        private uint expectedImageLength;

        /// <summary>
        /// 当前 BEGIN 声明的整体 CRC32。
        /// </summary>
        private uint expectedImageCrc32;

        /// <summary>
        /// 当前完整升级尝试已经接收的 DATA 字节。
        /// </summary>
        private byte[] receivedImage = [];

        /// <summary>
        /// 当前 DATA 已确认长度。
        /// </summary>
        private int receivedLength;

        /// <summary>
        /// 当前 App 是否有效。
        /// </summary>
        private bool appValid;

        /// <summary>
        /// 当前有效 App 长度。
        /// </summary>
        private uint appLength;

        /// <summary>
        /// 当前有效 App CRC32。
        /// </summary>
        private uint appCrc32;

        /// <summary>
        /// 下一次精确读取需要抛出的故障。
        /// </summary>
        private Exception? pendingReadException;

        /// <summary>
        /// 尚未被 DATA 请求领取的单次响应暂停点。
        /// </summary>
        private BootloaderSimulatorPause? nextDataResponsePause;

        /// <summary>
        /// 已由 DATA 请求领取、等待下一次精确读取执行的暂停点。
        /// </summary>
        private BootloaderSimulatorPause? pendingReadPause;

        /// <summary>
        /// 当前模拟 TCP 连接编号。
        /// </summary>
        private int connectionId;

        /// <summary>
        /// 当前是否存在模拟连接。
        /// </summary>
        private bool isConnected;

        /// <summary>
        /// 模拟器是否已经永久释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 获取或设置需要在 DATA 响应阶段制造的断线次数。
        /// </summary>
        public int DataDisconnectsRemaining { get; set; }

        /// <summary>
        /// 获取或设置需要在 END 已实际完成后制造的响应超时次数。
        /// </summary>
        public int EndTimeoutsRemaining { get; set; }

        /// <summary>
        /// 获取或设置需要明确返回的 DATA CRC NACK 次数。
        /// </summary>
        public int DataCrcNacksRemaining { get; set; }

        /// <summary>
        /// 获取或设置 END 后 HELLO 需要明确报告 App 无效的次数。
        /// </summary>
        public int VerificationMismatchesRemaining { get; set; }

        /// <summary>
        /// 获取或设置 RUN 请求的模拟响应方式。
        /// </summary>
        public BootloaderSimulatorRunBehavior RunBehavior { get; set; }

        /// <summary>
        /// 获取或设置 HELLO 返回的 App 固定基址。
        /// </summary>
        public uint HelloAppBase { get; set; } =
            IapUpgradeCoordinator.ExpectedAppBase;

        /// <summary>
        /// 获取或设置 HELLO 返回的 App 最大容量。
        /// </summary>
        public uint HelloAppMaxSize { get; set; } =
            IapFrameCodec.MaximumImageLength;

        /// <summary>
        /// 获取当前是否存在模拟 TCP 连接。
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (sync)
                {
                    return isConnected;
                }
            }
        }

        /// <summary>
        /// 获取全部请求的防御性快照。
        /// </summary>
        public IReadOnlyList<BootloaderSimulatorRequest> Requests
        {
            get
            {
                lock (sync)
                {
                    return requests.ToArray();
                }
            }
        }

        /// <summary>
        /// 配置下一条成功 DATA 响应在读取前暂停。
        /// </summary>
        /// <returns>可等待进入并由测试选择释放的暂停控制器。</returns>
        public BootloaderSimulatorPause PauseNextDataResponse()
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);

                if (nextDataResponsePause is not null ||
                    pendingReadPause is not null)
                {
                    throw new InvalidOperationException("已经存在 DATA 响应暂停点。");
                }

                nextDataResponsePause = new BootloaderSimulatorPause();
                return nextDataResponsePause;
            }
        }

        /// <summary>
        /// 建立一条新模拟连接并清理旧连接残留响应。
        /// </summary>
        /// <param name="address">目标 IPv4 地址。</param>
        /// <param name="port">目标 TCP 端口。</param>
        /// <param name="timeout">连接总超时。</param>
        /// <param name="cancellationToken">取消连接。</param>
        /// <returns>连接已经建立后的任务。</returns>
        public ValueTask ConnectAsync(
            IPAddress address,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            _ = port;
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();

            lock (sync)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
                connectionId = checked(connectionId + 1);
                isConnected = true;
                pendingReadException = null;
                pendingReadPause = null;
                responseBytes.Clear();
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 解析并处理一条完整上位机请求。
        /// </summary>
        /// <param name="bytes">固定 32 字节头及可选 DATA 载荷。</param>
        /// <param name="cancellationToken">取消写入。</param>
        /// <returns>响应已经进入模拟接收队列后的任务。</returns>
        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (sync)
            {
                EnsureConnectedUnderLock();

                if (bytes.Length < IapFrameCodec.RequestHeaderSize)
                {
                    throw new InvalidDataException("模拟器收到的 IAP 请求短于固定请求头。");
                }

                ReadOnlySpan<byte> frame = bytes.Span;
                IapCommand command = (IapCommand)BinaryPrimitives.ReadUInt32LittleEndian(
                    frame[8..12]);
                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame[12..16]);
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(frame[16..20]);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame[20..24]);
                requests.Add(new BootloaderSimulatorRequest(
                    connectionId,
                    command,
                    sequence,
                    offset,
                    length));

                switch (command)
                {
                    case IapCommand.Hello:
                        QueueResponseUnderLock(command, sequence, IapResponseStatus.Ack, 0U);
                        bool forceMismatch =
                            appValid && VerificationMismatchesRemaining > 0;

                        if (forceMismatch)
                        {
                            VerificationMismatchesRemaining--;
                        }

                        QueueHelloInfoUnderLock(forceMismatch);
                        break;

                    case IapCommand.Begin:
                        HandleBeginUnderLock(sequence, length, frame);
                        break;

                    case IapCommand.Data:
                        HandleDataUnderLock(sequence, offset, length, frame);
                        break;

                    case IapCommand.End:
                        HandleEndUnderLock(sequence);
                        break;

                    case IapCommand.Run:
                        HandleRunUnderLock(sequence);
                        break;

                    case IapCommand.Abort:
                        QueueResponseUnderLock(command, sequence, IapResponseStatus.Ack, 0U);
                        break;

                    default:
                        QueueResponseUnderLock(command, sequence, IapResponseStatus.Nack, 6U);
                        break;
                }
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 从模拟响应队列精确读取指定字节数。
        /// </summary>
        /// <param name="destination">必须完整填充的目标内存。</param>
        /// <param name="timeout">读取总超时。</param>
        /// <param name="cancellationToken">取消读取。</param>
        /// <returns>目标内存已经填满后的任务。</returns>
        public async ValueTask ReadExactAsync(
            Memory<byte> destination,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            BootloaderSimulatorPause? pause;

            lock (sync)
            {
                if (pendingReadException is not null)
                {
                    Exception exception = pendingReadException;
                    pendingReadException = null;
                    throw exception;
                }

                pause = pendingReadPause;
                pendingReadPause = null;
            }

            if (pause is not null)
            {
                pause.SignalEntered();
                await pause.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
            }

            byte[] received;

            lock (sync)
            {
                EnsureConnectedUnderLock();

                if (responseBytes.Count < destination.Length)
                {
                    throw new EndOfStreamException(
                        $"模拟 Bootloader 仅有 {responseBytes.Count} 字节，无法读满 {destination.Length} 字节。");
                }

                received = new byte[destination.Length];

                for (int index = 0; index < received.Length; index++)
                {
                    received[index] = responseBytes.Dequeue();
                }
            }

            received.AsMemory().CopyTo(destination);
        }

        /// <summary>
        /// 清除当前模拟连接和未读响应。
        /// </summary>
        /// <returns>连接已经清除后的任务。</returns>
        public ValueTask DisconnectAsync()
        {
            lock (sync)
            {
                isConnected = false;
                pendingReadException = null;
                pendingReadPause = null;
                responseBytes.Clear();
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 永久释放模拟传输。
        /// </summary>
        /// <returns>模拟连接已经释放后的任务。</returns>
        public ValueTask DisposeAsync()
        {
            lock (sync)
            {
                isDisposed = true;
                isConnected = false;
                pendingReadException = null;
                pendingReadPause = null;
                nextDataResponsePause = null;
                responseBytes.Clear();
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 初始化一次新的完整镜像接收。
        /// </summary>
        /// <param name="sequence">BEGIN 请求序号。</param>
        /// <param name="length">完整固件长度。</param>
        /// <param name="frame">完整 BEGIN 帧。</param>
        private void HandleBeginUnderLock(
            uint sequence,
            uint length,
            ReadOnlySpan<byte> frame)
        {
            if (length is 0U or > IapFrameCodec.MaximumImageLength)
            {
                QueueResponseUnderLock(
                    IapCommand.Begin,
                    sequence,
                    IapResponseStatus.Nack,
                    4U);
                return;
            }

            expectedImageLength = length;
            expectedImageCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(frame[28..32]);
            receivedImage = new byte[checked((int)length)];
            receivedLength = 0;
            appValid = false;
            appLength = 0U;
            appCrc32 = 0U;
            QueueResponseUnderLock(
                IapCommand.Begin,
                sequence,
                IapResponseStatus.Ack,
                0U);
        }

        /// <summary>
        /// 校验并接收一个顺序 DATA 分片，或按脚本制造断线。
        /// </summary>
        /// <param name="sequence">DATA 请求序号。</param>
        /// <param name="offset">分片相对偏移。</param>
        /// <param name="length">分片字节数。</param>
        /// <param name="frame">完整 DATA 帧。</param>
        private void HandleDataUnderLock(
            uint sequence,
            uint offset,
            uint length,
            ReadOnlySpan<byte> frame)
        {
            if (DataDisconnectsRemaining > 0)
            {
                DataDisconnectsRemaining--;
                isConnected = false;
                pendingReadException = new IOException("模拟 DATA 响应前断线。");
                return;
            }

            if (DataCrcNacksRemaining > 0)
            {
                DataCrcNacksRemaining--;
                QueueResponseUnderLock(
                    IapCommand.Data,
                    sequence,
                    IapResponseStatus.Nack,
                    5U);
                return;
            }

            if (offset != receivedLength ||
                length is 0U or > IapFrameCodec.MaximumDataLength ||
                frame.Length != IapFrameCodec.RequestHeaderSize + length)
            {
                QueueResponseUnderLock(
                    IapCommand.Data,
                    sequence,
                    IapResponseStatus.Nack,
                    checked((uint)receivedLength));
                return;
            }

            ReadOnlySpan<byte> payload = frame[IapFrameCodec.RequestHeaderSize..];
            uint payloadCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(frame[24..28]);

            if (IapCrc32.Compute(payload) != payloadCrc32)
            {
                QueueResponseUnderLock(
                    IapCommand.Data,
                    sequence,
                    IapResponseStatus.Nack,
                    5U);
                return;
            }

            payload.CopyTo(receivedImage.AsSpan(receivedLength));
            receivedLength = checked(receivedLength + payload.Length);
            QueueResponseUnderLock(
                IapCommand.Data,
                sequence,
                IapResponseStatus.Ack,
                checked((uint)receivedLength));
            pendingReadPause = nextDataResponsePause;
            nextDataResponsePause = null;
        }

        /// <summary>
        /// 校验完整镜像并按脚本返回 ACK 或制造 END 超时。
        /// </summary>
        /// <param name="sequence">END 请求序号。</param>
        private void HandleEndUnderLock(uint sequence)
        {
            if (receivedLength != expectedImageLength ||
                IapCrc32.Compute(receivedImage) != expectedImageCrc32)
            {
                QueueResponseUnderLock(
                    IapCommand.End,
                    sequence,
                    IapResponseStatus.Nack,
                    5U);
                return;
            }

            appValid = true;
            appLength = expectedImageLength;
            appCrc32 = expectedImageCrc32;

            if (EndTimeoutsRemaining > 0)
            {
                EndTimeoutsRemaining--;
                pendingReadException = new TimeoutException("模拟 END ACK 超时。");
                return;
            }

            QueueResponseUnderLock(
                IapCommand.End,
                sequence,
                IapResponseStatus.Ack,
                0U);
        }

        /// <summary>
        /// 按配置返回 RUN ACK、NACK 或超时。
        /// </summary>
        /// <param name="sequence">RUN 请求序号。</param>
        private void HandleRunUnderLock(uint sequence)
        {
            switch (RunBehavior)
            {
                case BootloaderSimulatorRunBehavior.Acknowledge:
                    QueueResponseUnderLock(
                        IapCommand.Run,
                        sequence,
                        IapResponseStatus.Ack,
                        0U);
                    break;

                case BootloaderSimulatorRunBehavior.Reject:
                    QueueResponseUnderLock(
                        IapCommand.Run,
                        sequence,
                        IapResponseStatus.Nack,
                        2U);
                    break;

                case BootloaderSimulatorRunBehavior.Timeout:
                    pendingReadException = new TimeoutException("模拟 RUN ACK 超时。");
                    break;

                default:
                    throw new InvalidOperationException(
                        $"未知模拟 RUN 行为：{RunBehavior}。");
            }
        }

        /// <summary>
        /// 追加一条固定 24 字节响应头。
        /// </summary>
        /// <param name="command">回显命令。</param>
        /// <param name="sequence">回显序号。</param>
        /// <param name="status">ACK 或 NACK。</param>
        /// <param name="detail">命令相关详情。</param>
        private void QueueResponseUnderLock(
            IapCommand command,
            uint sequence,
            IapResponseStatus status,
            uint detail)
        {
            byte[] response = new byte[IapFrameCodec.ResponseHeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(
                response.AsSpan(0, 4),
                IapFrameCodec.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(
                response.AsSpan(4, 2),
                IapFrameCodec.ResponseHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(
                response.AsSpan(8, 4),
                (uint)command);
            BinaryPrimitives.WriteUInt32LittleEndian(
                response.AsSpan(12, 4),
                sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(
                response.AsSpan(16, 4),
                (uint)status);
            BinaryPrimitives.WriteUInt32LittleEndian(
                response.AsSpan(20, 4),
                detail);

            foreach (byte value in response)
            {
                responseBytes.Enqueue(value);
            }
        }

        /// <summary>
        /// 在 HELLO ACK 后追加固定 24 字节设备信息。
        /// </summary>
        /// <param name="forceMismatch">是否在本次 HELLO 中明确报告 App 无效。</param>
        private void QueueHelloInfoUnderLock(bool forceMismatch)
        {
            byte[] hello = new byte[IapFrameCodec.HelloInfoSize];
            BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(0, 4), 0x00010000U);
            BinaryPrimitives.WriteUInt32LittleEndian(
                hello.AsSpan(4, 4),
                HelloAppBase);
            BinaryPrimitives.WriteUInt32LittleEndian(
                hello.AsSpan(8, 4),
                HelloAppMaxSize);
            BinaryPrimitives.WriteUInt32LittleEndian(
                hello.AsSpan(12, 4),
                appValid && !forceMismatch ? 1U : 0U);
            BinaryPrimitives.WriteUInt32LittleEndian(
                hello.AsSpan(16, 4),
                forceMismatch ? 0U : appLength);
            BinaryPrimitives.WriteUInt32LittleEndian(
                hello.AsSpan(20, 4),
                forceMismatch ? 0U : appCrc32);

            foreach (byte value in hello)
            {
                responseBytes.Enqueue(value);
            }
        }

        /// <summary>
        /// 在已持有同步门时要求连接存在且对象未释放。
        /// </summary>
        private void EnsureConnectedUnderLock()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (!isConnected)
            {
                throw new IOException("模拟 Bootloader TCP 已断开。");
            }
        }
    }
}
