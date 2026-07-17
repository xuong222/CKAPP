using System.Buffers;
using System.IO;
using System.Threading.Channels;

namespace CH32UpperComputer.Infrastructure.Serial
{
    /// <summary>
    /// 提供生产串口会话唯一允许的后台异步读取循环。
    /// </summary>
    public static class SerialReadPump
    {
        /// <summary>
        /// 每次从共享数组池租用并提交给底层流的固定读取字节数。
        /// </summary>
        public const int ReadBufferBytes = 512;

        /// <summary>
        /// 连续读取双工流，把每次非空读取复制为一个不可变接收块并写入有界通道。
        /// </summary>
        /// <param name="stream">当前串口会话独占的可读基础流。</param>
        /// <param name="writer">接收块有界通道的写入端。</param>
        /// <param name="portGeneration">当前会话的非负端口代次。</param>
        /// <param name="timeProvider">为每个块提供 UTC 和单调到达时间的统一时间源。</param>
        /// <param name="cancellationToken">关闭会话时终止挂起读取与通道背压等待的令牌。</param>
        /// <returns>流正常结束、会话取消或关闭期资源释放后完成的任务。</returns>
        /// <exception cref="ArgumentNullException">流、通道写入端或时间源为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="portGeneration"/> 为负数时抛出。</exception>
        /// <remarks>
        /// 非关闭期底层异常会同时用于故障完成通道并由返回任务原样传播；
        /// 已发出取消的关闭路径则正常完成通道，避免把用户关闭记录为通信故障。
        /// </remarks>
        public static async Task RunAsync(
            Stream stream,
            ChannelWriter<SerialReceiveChunk> writer,
            int portGeneration,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(timeProvider);

            if (portGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(portGeneration));
            }

            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
            Exception? completionError = null;
            long receiveSequence = 0;

            try
            {
                while (true)
                {
                    int receivedByteCount = await stream
                        .ReadAsync(
                            rentedBuffer.AsMemory(0, ReadBufferBytes),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (receivedByteCount == 0)
                    {
                        break;
                    }

                    long sequence = checked(++receiveSequence);
                    SerialReceiveChunk chunk = new(
                        rentedBuffer.AsSpan(0, receivedByteCount),
                        portGeneration,
                        sequence,
                        timeProvider.GetUtcNow(),
                        timeProvider.GetTimestamp());
                    await writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 会话关闭通过同一令牌终止挂起读取或通道背压，属于正常生命周期收敛。
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                // 部分串口驱动在取消后通过释放基础流解除挂起读取，属于正常关闭路径。
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                // 部分串口驱动在关闭句柄后以 I/O 异常结束读取，属于正常关闭路径。
            }
            catch (Exception exception)
            {
                completionError = exception;
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: true);
                writer.TryComplete(completionError);
            }
        }
    }
}
