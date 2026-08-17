using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Transactions
{
    /// <summary>
    /// 验证回读后写入使用的独占事务序列不建立队列且不会被普通请求插入。
    /// </summary>
    [TestFixture]
    public sealed class TransactionSequenceLeaseTests
    {
        /// <summary>
        /// 验证租约保持期间普通请求立即 Busy，租约释放后普通请求可以再次发送。
        /// </summary>
        [Test]
        public async Task SequenceLease_BlocksOrdinaryRequestWithoutQueueUntilReleased()
        {
            ManualTimeProvider timeProvider = new();
            await using FakeSerialTransport transport = new(timeProvider);
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            await using ModbusTransactionCoordinator coordinator = new(
                transport,
                timeProvider);
            await coordinator.StartAsync(CancellationToken.None);
            SimulatedModbusDevice device = new(transport, timeProvider);
            TransactionRequest transaction = CreateReadTransaction();

            await using (ModbusTransactionSequenceLease lease =
                coordinator.TryAcquireSequence() ??
                throw new AssertionException("空闲协调器必须能够创建独占序列。"))
            {
                TransactionExecutionResult rejected =
                    await coordinator.TryExecuteAsync(
                        transaction,
                        CancellationToken.None);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(rejected.IsAccepted, Is.False);
                    Assert.That(rejected.Rejection, Is.EqualTo(TransactionRejected.Busy));
                    Assert.That(transport.WrittenFrames, Is.Empty);
                }));

                Task<TransactionExecutionResult> leasedExecution = lease.ExecuteAsync(
                    transaction,
                    CancellationToken.None).AsTask();
                ReadOnlyMemory<byte> leasedFrame = await WaitForWriteAsync(
                    transport,
                    1);
                await device.HandleWriteAsync(leasedFrame, CancellationToken.None);
                TransactionExecutionResult leasedResult =
                    await leasedExecution.WaitAsync(TimeSpan.FromSeconds(1));

                Assert.That(
                    leasedResult.Outcome?.State,
                    Is.EqualTo(TransactionCompletionState.Succeeded));
            }

            Task<TransactionExecutionResult> ordinaryExecution =
                coordinator.TryExecuteAsync(
                    transaction,
                    CancellationToken.None).AsTask();
            ReadOnlyMemory<byte> ordinaryFrame = await WaitForWriteAsync(
                transport,
                2);
            await device.HandleWriteAsync(ordinaryFrame, CancellationToken.None);
            TransactionExecutionResult ordinaryResult =
                await ordinaryExecution.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    ordinaryResult.Outcome?.State,
                    Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(transport.WrittenFrames, Has.Count.EqualTo(2));
            }));
        }

        /// <summary>
        /// 创建读取地址寄存器的一项标准事务请求。
        /// </summary>
        /// <returns>使用一秒总超时和 20 毫秒字节静默超时的标准事务。</returns>
        private static TransactionRequest CreateReadTransaction()
        {
            ModbusRequest request =
                ModbusRequestFactory.CreateReadHoldingRegisters(
                    1,
                    0x0000,
                    1);
            return TransactionRequest.CreateStandard(
                request,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(20));
        }

        /// <summary>
        /// 等待模拟传输写历史达到指定数量。
        /// </summary>
        /// <param name="transport">记录完整发送帧的模拟串口。</param>
        /// <param name="expectedCount">从一开始计数的目标写帧数量。</param>
        /// <returns>目标位置完整帧的防御性副本。</returns>
        private static async Task<ReadOnlyMemory<byte>> WaitForWriteAsync(
            FakeSerialTransport transport,
            int expectedCount)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);

            while (transport.WrittenFrames.Count < expectedCount)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException("等待独占序列写入模拟串口超时。");
                }

                await Task.Yield();
            }

            return transport.WrittenFrames[expectedCount - 1];
        }
    }
}
