using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Channels;

namespace CH32UpperComputer.Testing
{
    /// <summary>
    /// 提供可脚本化读取块、流结束和 I/O 故障的双工流，用于验证单后台读取循环的边界行为。
    /// </summary>
    public sealed class ControllableDuplexStream : Stream
    {
        /// <summary>
        /// 默认待读取指令容量，写满时生产方等待或显式获知容量不足。
        /// </summary>
        public const int DefaultReadDirectiveCapacity = 256;

        /// <summary>
        /// 默认保留的最近写入块数量。
        /// </summary>
        public const int DefaultWriteHistoryCapacity = 1024;

        /// <summary>
        /// 保护待交付块余量、写历史和释放状态的同步门。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 串行化读取，保持单后台读取循环语义并保护块余量。
        /// </summary>
        private readonly SemaphoreSlim readGate = new(1, 1);

        /// <summary>
        /// 保存调用方排入的有界读取指令。
        /// </summary>
        private readonly Channel<ReadDirective> readDirectives;

        /// <summary>
        /// 在流释放时唤醒挂起读取的令牌源。
        /// </summary>
        private readonly CancellationTokenSource disposeCancellation = new();

        /// <summary>
        /// 最近写入块的最大保留数量。
        /// </summary>
        private readonly int writeHistoryCapacity;

        /// <summary>
        /// 按写入顺序保存的独占字节副本。
        /// </summary>
        private readonly List<byte[]> writtenBuffers = [];

        /// <summary>
        /// 上一次读取块因调用方缓冲区较小而尚未交付的完整数据。
        /// </summary>
        private byte[]? pendingReadData;

        /// <summary>
        /// 下一次应从 <see cref="pendingReadData"/> 交付的零基偏移。
        /// </summary>
        private int pendingReadOffset;

        /// <summary>
        /// 指示持久流结束指令已经成功排入，后续生产操作必须被拒绝。
        /// </summary>
        private bool isEndOfStreamQueued;

        /// <summary>
        /// 指示读取方已经消费持久流结束指令，后续非空读取必须立即返回零。
        /// </summary>
        private bool hasReachedEndOfStream;

        /// <summary>
        /// 指示流是否已经释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 初始化使用默认有界容量的可控双工流。
        /// </summary>
        public ControllableDuplexStream()
            : this(DefaultReadDirectiveCapacity, DefaultWriteHistoryCapacity)
        {
        }

        /// <summary>
        /// 初始化使用指定有界读取容量和写历史容量的可控双工流。
        /// </summary>
        /// <param name="readDirectiveCapacity">最多允许等待处理的读取指令数量。</param>
        /// <param name="writeHistoryCapacity">最多保留的最近写入块数量。</param>
        /// <exception cref="ArgumentOutOfRangeException">任一容量不是正数时抛出。</exception>
        public ControllableDuplexStream(
            int readDirectiveCapacity,
            int writeHistoryCapacity)
        {
            if (readDirectiveCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(readDirectiveCapacity),
                    readDirectiveCapacity,
                    "读取指令容量必须为正数。");
            }

            if (writeHistoryCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(writeHistoryCapacity),
                    writeHistoryCapacity,
                    "写历史容量必须为正数。");
            }

            this.writeHistoryCapacity = writeHistoryCapacity;
            readDirectives = Channel.CreateBounded<ReadDirective>(
                new BoundedChannelOptions(readDirectiveCapacity)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
        }

        /// <summary>
        /// 获取流是否支持读取。
        /// </summary>
        public override bool CanRead
        {
            get
            {
                lock (syncRoot)
                {
                    return !isDisposed;
                }
            }
        }

        /// <summary>
        /// 获取流是否支持定位；可控串口流不支持定位。
        /// </summary>
        public override bool CanSeek => false;

        /// <summary>
        /// 获取流是否支持写入。
        /// </summary>
        public override bool CanWrite
        {
            get
            {
                lock (syncRoot)
                {
                    return !isDisposed;
                }
            }
        }

        /// <summary>
        /// 获取流长度；串口流没有可查询长度。
        /// </summary>
        /// <exception cref="NotSupportedException">始终抛出。</exception>
        public override long Length => throw new NotSupportedException("串口双工流不支持长度查询。");

        /// <summary>
        /// 获取或设置当前位置；串口流没有可定位位置。
        /// </summary>
        /// <exception cref="NotSupportedException">始终抛出。</exception>
        public override long Position
        {
            get => throw new NotSupportedException("串口双工流不支持位置查询。");
            set => throw new NotSupportedException("串口双工流不支持位置设置。");
        }

        /// <summary>
        /// 获取最近写入块的防御性只读快照。
        /// </summary>
        public IReadOnlyList<ReadOnlyMemory<byte>> WrittenBuffers
        {
            get
            {
                lock (syncRoot)
                {
                    ReadOnlyMemory<byte>[] snapshot = writtenBuffers
                        .Select(buffer => new ReadOnlyMemory<byte>((byte[])buffer.Clone()))
                        .ToArray();
                    return new ReadOnlyCollection<ReadOnlyMemory<byte>>(snapshot);
                }
            }
        }

        /// <summary>
        /// 排入一个独占复制的非空读取块；容量已满时明确失败而不静默丢弃。
        /// </summary>
        /// <param name="data">后续读取需要按顺序交付的非空字节块。</param>
        /// <exception cref="ArgumentException"><paramref name="data"/> 为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">指令队列已满或已经完成时抛出。</exception>
        public void QueueRead(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                throw new ArgumentException("排入的读取块不能为空。", nameof(data));
            }

            ReadDirective directive = ReadDirective.CreateData(data);
            QueueDirective(directive);
        }

        /// <summary>
        /// 异步排入一个独占复制的非空读取块；容量已满时等待空间。
        /// </summary>
        /// <param name="data">后续读取需要按顺序交付的非空字节块。</param>
        /// <param name="cancellationToken">取消容量等待的令牌。</param>
        /// <returns>排入成功后完成的值任务。</returns>
        /// <exception cref="ArgumentException"><paramref name="data"/> 为空时抛出。</exception>
        public async ValueTask QueueReadAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            if (data.IsEmpty)
            {
                throw new ArgumentException("排入的读取块不能为空。", nameof(data));
            }

            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
                ThrowIfEndOfStreamQueuedUnderLock();
            }

            try
            {
                await readDirectives.Writer
                    .WriteAsync(ReadDirective.CreateData(data.Span), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException exception)
            {
                throw new InvalidOperationException(
                    "流结束已经排入，不能再排入读取数据。",
                    exception);
            }
        }

        /// <summary>
        /// 排入一次返回零字节的脚本指令，用于模拟远端正常结束读取。
        /// </summary>
        /// <exception cref="InvalidOperationException">指令队列已满或已经完成时抛出。</exception>
        public void QueueEndOfStream()
        {
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
                ThrowIfEndOfStreamQueuedUnderLock();

                if (!readDirectives.Writer.TryWrite(ReadDirective.CreateEndOfStream()))
                {
                    throw new InvalidOperationException("读取指令队列已满或已经完成。");
                }

                isEndOfStreamQueued = true;
                readDirectives.Writer.TryComplete();
            }
        }

        /// <summary>
        /// 排入一次读取 I/O 故障，读取方处理到该指令时原样抛出异常。
        /// </summary>
        /// <param name="exception">需要由后续读取抛出的 I/O 异常。</param>
        /// <exception cref="InvalidOperationException">指令队列已满或已经完成时抛出。</exception>
        public void QueueIOException(IOException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            QueueDirective(ReadDirective.CreateError(exception));
        }

        /// <summary>
        /// 同步读取下一段已排入数据；缓冲区较小时保留未交付余量供下一次读取。
        /// </summary>
        /// <param name="buffer">接收数据的数组。</param>
        /// <param name="offset">写入数组的零基偏移。</param>
        /// <param name="count">本次最多读取的字节数。</param>
        /// <returns>实际读取字节数；脚本化流结束时为零。</returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ValidateBufferRange(buffer.Length, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        /// <summary>
        /// 异步读取下一段已排入数据；无指令时挂起并观察调用方取消或流释放。
        /// </summary>
        /// <param name="buffer">接收数据的目标内存。</param>
        /// <param name="cancellationToken">取消等待的令牌。</param>
        /// <returns>实际读取字节数；脚本化流结束或挂起读取被释放终止时为零。</returns>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            ThrowIfDisposed();

            lock (syncRoot)
            {
                if (hasReachedEndOfStream)
                {
                    return 0;
                }
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    disposeCancellation.Token);

            try
            {
                await readGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                disposeCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            try
            {
                int pendingCount = CopyPendingBytes(buffer.Span);

                if (pendingCount > 0)
                {
                    return pendingCount;
                }

                ReadDirective directive;

                try
                {
                    directive = await readDirectives.Reader
                        .ReadAsync(linkedCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    disposeCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }

                if (directive.Error is not null)
                {
                    throw directive.Error;
                }

                if (directive.IsEndOfStream)
                {
                    lock (syncRoot)
                    {
                        pendingReadData = null;
                        pendingReadOffset = 0;
                        hasReachedEndOfStream = true;
                    }

                    return 0;
                }

                lock (syncRoot)
                {
                    pendingReadData = directive.Data;
                    pendingReadOffset = 0;
                }

                return CopyPendingBytes(buffer.Span);
            }
            finally
            {
                readGate.Release();
            }
        }

        /// <summary>
        /// 记录待写入字节的独占副本；不对真实设备执行 I/O。
        /// </summary>
        /// <param name="buffer">包含待记录数据的数组。</param>
        /// <param name="offset">待记录数据的零基偏移。</param>
        /// <param name="count">待记录字节数。</param>
        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ValidateBufferRange(buffer.Length, offset, count);
            RecordWrite(buffer.AsSpan(offset, count));
        }

        /// <summary>
        /// 记录异步写入字节的独占副本，并观察调用方取消。
        /// </summary>
        /// <param name="buffer">待记录的数据内存。</param>
        /// <param name="cancellationToken">在记录前取消操作的令牌。</param>
        /// <returns>同步完成的值任务。</returns>
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordWrite(buffer.Span);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 刷新可控流；所有写入均已在内存中同步完成，因此本方法无操作。
        /// </summary>
        public override void Flush()
        {
            ThrowIfDisposed();
        }

        /// <summary>
        /// 异步刷新可控流；所有写入均已同步完成。
        /// </summary>
        /// <param name="cancellationToken">取消调用的令牌。</param>
        /// <returns>已完成任务。</returns>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 拒绝串口流不支持的定位操作。
        /// </summary>
        /// <param name="offset">未使用的偏移。</param>
        /// <param name="origin">未使用的起点。</param>
        /// <returns>本方法不返回。</returns>
        /// <exception cref="NotSupportedException">始终抛出。</exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException("串口双工流不支持定位。");
        }

        /// <summary>
        /// 拒绝串口流不支持的长度修改操作。
        /// </summary>
        /// <param name="value">未使用的新长度。</param>
        /// <exception cref="NotSupportedException">始终抛出。</exception>
        public override void SetLength(long value)
        {
            throw new NotSupportedException("串口双工流不支持长度修改。");
        }

        /// <summary>
        /// 释放流、完成指令通道并唤醒全部挂起读取。
        /// </summary>
        /// <param name="disposing">由显式释放路径调用时为 <see langword="true"/>。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (syncRoot)
                {
                    if (isDisposed)
                    {
                        base.Dispose(disposing);
                        return;
                    }

                    isDisposed = true;
                }

                disposeCancellation.Cancel();
                readDirectives.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// 异步释放流；实现同步完成并保持幂等语义。
        /// </summary>
        /// <returns>已经完成的值任务。</returns>
        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 从上一个大块的未交付余量中复制本次缓冲区能够容纳的字节。
        /// </summary>
        /// <param name="destination">本次读取的目标缓冲区。</param>
        /// <returns>实际复制字节数；没有余量时为零。</returns>
        private int CopyPendingBytes(Span<byte> destination)
        {
            lock (syncRoot)
            {
                if (pendingReadData is null)
                {
                    return 0;
                }

                int remaining = pendingReadData.Length - pendingReadOffset;
                int copyCount = Math.Min(destination.Length, remaining);
                pendingReadData.AsSpan(pendingReadOffset, copyCount).CopyTo(destination);
                pendingReadOffset += copyCount;

                if (pendingReadOffset == pendingReadData.Length)
                {
                    pendingReadData = null;
                    pendingReadOffset = 0;
                }

                return copyCount;
            }
        }

        /// <summary>
        /// 保存写入数据的独占副本，并按容量淘汰最早记录。
        /// </summary>
        /// <param name="buffer">调用方待写入字节。</param>
        private void RecordWrite(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();
            byte[] copy = buffer.ToArray();

            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);

                if (writtenBuffers.Count == writeHistoryCapacity)
                {
                    writtenBuffers.RemoveAt(0);
                }

                writtenBuffers.Add(copy);
            }
        }

        /// <summary>
        /// 在线程安全边界内排入普通数据或故障指令，并拒绝持久流结束后的生产操作。
        /// </summary>
        /// <param name="directive">待排入的非流结束读取指令。</param>
        /// <exception cref="ObjectDisposedException">流已经释放时抛出。</exception>
        /// <exception cref="InvalidOperationException">流结束已经排入或指令通道已满时抛出。</exception>
        private void QueueDirective(ReadDirective directive)
        {
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
                ThrowIfEndOfStreamQueuedUnderLock();

                if (!readDirectives.Writer.TryWrite(directive))
                {
                    throw new InvalidOperationException("读取指令队列已满或已经完成。");
                }
            }
        }

        /// <summary>
        /// 校验传统数组读取和写入重载的范围参数。
        /// </summary>
        /// <param name="bufferLength">数组总长度。</param>
        /// <param name="offset">调用方指定偏移。</param>
        /// <param name="count">调用方指定数量。</param>
        private static void ValidateBufferRange(
            int bufferLength,
            int offset,
            int count)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (offset > bufferLength - count)
            {
                throw new ArgumentException("偏移与数量超出目标数组范围。");
            }
        }

        /// <summary>
        /// 拒绝释放后的新操作。
        /// </summary>
        /// <exception cref="ObjectDisposedException">流已经释放时抛出。</exception>
        private void ThrowIfDisposed()
        {
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
            }
        }

        /// <summary>
        /// 在已经持有同步门时拒绝持久流结束之后的任何新读取指令。
        /// </summary>
        /// <exception cref="InvalidOperationException">流结束指令已经成功排入时抛出。</exception>
        private void ThrowIfEndOfStreamQueuedUnderLock()
        {
            if (isEndOfStreamQueued)
            {
                throw new InvalidOperationException("流结束已经排入，不能再排入读取指令。");
            }
        }

        /// <summary>
        /// 表示一次排队读取行为：数据块、正常结束或 I/O 故障三者之一。
        /// </summary>
        private sealed class ReadDirective
        {
            /// <summary>
            /// 初始化一个不可变读取指令。
            /// </summary>
            /// <param name="data">数据指令独占持有的字节；其他类型为空。</param>
            /// <param name="isEndOfStream">是否要求读取返回零。</param>
            /// <param name="error">是否要求读取抛出指定异常。</param>
            private ReadDirective(
                byte[]? data,
                bool isEndOfStream,
                IOException? error)
            {
                Data = data;
                IsEndOfStream = isEndOfStream;
                Error = error;
            }

            /// <summary>
            /// 获取数据块指令的独占字节。
            /// </summary>
            internal byte[]? Data { get; }

            /// <summary>
            /// 获取是否要求读取返回零。
            /// </summary>
            internal bool IsEndOfStream { get; }

            /// <summary>
            /// 获取需要由读取抛出的 I/O 异常。
            /// </summary>
            internal IOException? Error { get; }

            /// <summary>
            /// 创建一个独占复制调用方字节的数据指令。
            /// </summary>
            /// <param name="data">需要排入的非空数据。</param>
            /// <returns>新的数据指令。</returns>
            internal static ReadDirective CreateData(ReadOnlySpan<byte> data)
            {
                return new ReadDirective(data.ToArray(), false, null);
            }

            /// <summary>
            /// 创建一个令下一次读取返回零的指令。
            /// </summary>
            /// <returns>新的流结束指令。</returns>
            internal static ReadDirective CreateEndOfStream()
            {
                return new ReadDirective(null, true, null);
            }

            /// <summary>
            /// 创建一个令下一次读取抛出 I/O 异常的指令。
            /// </summary>
            /// <param name="exception">需要原样抛出的异常。</param>
            /// <returns>新的故障指令。</returns>
            internal static ReadDirective CreateError(IOException exception)
            {
                return new ReadDirective(null, false, exception);
            }
        }
    }
}
