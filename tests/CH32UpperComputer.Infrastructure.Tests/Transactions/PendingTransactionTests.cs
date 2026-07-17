using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Framing;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Transactions
{
    /// <summary>
    /// 验证每项事务只有一个终止事件能够通过 Interlocked 完成门。
    /// </summary>
    [TestFixture]
    public sealed class PendingTransactionTests
    {
        /// <summary>
        /// 验证所有规定终态均可成为首个胜者，后续候选只留下诊断且不会改变 Task 结果。
        /// </summary>
        /// <param name="winningState">本轮第一个尝试的终态。</param>
        [TestCase(TransactionCompletionState.Succeeded)]
        [TestCase(TransactionCompletionState.RawCaptured)]
        [TestCase(TransactionCompletionState.ModbusException)]
        [TestCase(TransactionCompletionState.TimedOut)]
        [TestCase(TransactionCompletionState.Cancelled)]
        [TestCase(TransactionCompletionState.WriteFailed)]
        [TestCase(TransactionCompletionState.Disconnected)]
        [TestCase(TransactionCompletionState.ReceiveOverflow)]
        [TestCase(TransactionCompletionState.ApplicationStopping)]
        public async Task TryComplete_FirstTerminalStateWinsExactlyOnce(
            TransactionCompletionState winningState)
        {
            ManualTimeProvider timeProvider = new();
            PendingTransaction pending = new(11, timeProvider);
            TransactionOutcome winningOutcome = CreateOutcome(11, winningState, 10);
            TransactionCompletionState losingState = winningState == TransactionCompletionState.Cancelled
                ? TransactionCompletionState.ApplicationStopping
                : TransactionCompletionState.Cancelled;
            TransactionOutcome losingOutcome = CreateOutcome(11, losingState, 20);
            Task<TransactionOutcome> completionTask = pending.Completion;

            bool firstWon = pending.TryComplete(winningState, winningOutcome);
            timeProvider.Advance(TimeSpan.FromTicks(1));
            bool secondWon = pending.TryComplete(losingState, losingOutcome);
            TransactionOutcome completed = await completionTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(firstWon, Is.True);
                Assert.That(secondWon, Is.False);
                Assert.That(completed, Is.SameAs(winningOutcome));
                Assert.That(pending.Completion, Is.SameAs(completionTask));
                Assert.That(pending.CompletionState, Is.EqualTo(winningState));
                Assert.That(pending.WinningOutcome, Is.SameAs(winningOutcome));
                Assert.That(pending.LosingAttempts, Has.Count.EqualTo(1));
                Assert.That(pending.LosingAttempts[0].AttemptedState, Is.EqualTo(losingState));
                Assert.That(pending.LosingAttempts[0].WinningState, Is.EqualTo(winningState));
            }));
        }

        /// <summary>
        /// 验证响应/超时、取消/响应、断开/超时并发时每轮恰有一个原子门胜者。
        /// </summary>
        /// <param name="firstState">第一并发候选终态。</param>
        /// <param name="secondState">第二并发候选终态。</param>
        [TestCase(TransactionCompletionState.Succeeded, TransactionCompletionState.TimedOut)]
        [TestCase(TransactionCompletionState.Cancelled, TransactionCompletionState.Succeeded)]
        [TestCase(TransactionCompletionState.Disconnected, TransactionCompletionState.TimedOut)]
        public async Task TryComplete_ConcurrentTerminalEvents_HaveSingleWinner(
            TransactionCompletionState firstState,
            TransactionCompletionState secondState)
        {
            const int iterationCount = 64;

            for (int iteration = 1; iteration <= iterationCount; iteration++)
            {
                ManualTimeProvider timeProvider = new();
                PendingTransaction pending = new(iteration, timeProvider);
                TransactionOutcome firstOutcome = CreateOutcome(iteration, firstState, 1);
                TransactionOutcome secondOutcome = CreateOutcome(iteration, secondState, 2);
                using Barrier barrier = new(3);
                Task<bool> first = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    return pending.TryComplete(firstState, firstOutcome);
                });
                Task<bool> second = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    return pending.TryComplete(secondState, secondOutcome);
                });

                barrier.SignalAndWait();
                bool[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
                TransactionOutcome completed = await pending.Completion.WaitAsync(TimeSpan.FromSeconds(1));
                int winningIndex = results[0] ? 0 : 1;
                TransactionCompletionState expectedState = winningIndex == 0 ? firstState : secondState;

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(results.Count(result => result), Is.EqualTo(1));
                    Assert.That(pending.CompletionState, Is.EqualTo(expectedState));
                    Assert.That(completed.State, Is.EqualTo(expectedState));
                    Assert.That(pending.LosingAttempts, Has.Count.EqualTo(1));
                    Assert.That(pending.LosingAttempts[0].WinningState, Is.EqualTo(expectedState));
                }));
            }
        }

        /// <summary>
        /// 验证最终状态不能携带与其模式或协议证据相矛盾的组合。
        /// </summary>
        [Test]
        public void TransactionOutcome_ContradictoryEvidence_IsRejectedBeforeAtomicGate()
        {
            Action createSuccessWithoutResponse = () =>
            {
                _ = new TransactionOutcome(
                    1,
                    TransactionCompletionState.Succeeded,
                    TransactionMode.Standard,
                    0,
                    "缺少成功响应证据。");
            };
            Action createCancelledWithException = () =>
            {
                _ = new TransactionOutcome(
                    1,
                    TransactionCompletionState.Cancelled,
                    TransactionMode.Standard,
                    0,
                    "取消不得携带异常。",
                    exception: new IOException("矛盾异常"));
            };

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    createSuccessWithoutResponse,
                    Throws.ArgumentException);
                Assert.That(
                    createCancelledWithException,
                    Throws.ArgumentException);
            }));
        }

        /// <summary>
        /// 创建满足终态证据一致性约束的测试结果。
        /// </summary>
        /// <param name="transactionId">结果事务编号。</param>
        /// <param name="state">需要创建的终态。</param>
        /// <param name="timestamp">结果单调完成时间戳。</param>
        /// <returns>具有对应模式、响应、接收帧或异常证据的结果。</returns>
        private static TransactionOutcome CreateOutcome(
            long transactionId,
            TransactionCompletionState state,
            long timestamp)
        {
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0, 1);

            return state switch
            {
                TransactionCompletionState.Succeeded => CreateStandardOutcome(
                    transactionId,
                    state,
                    request,
                    ModbusCrc16.Append([0x01, 0x03, 0x02, 0x00, 0x2A]),
                    timestamp),
                TransactionCompletionState.ModbusException => CreateStandardOutcome(
                    transactionId,
                    state,
                    request,
                    ModbusCrc16.Append([0x01, 0x83, 0x02]),
                    timestamp),
                TransactionCompletionState.RawCaptured => new TransactionOutcome(
                    transactionId,
                    state,
                    TransactionMode.RawDebug,
                    timestamp,
                    "原始捕获完成。",
                    receivedFrame: CreateReceivedFrame(
                        ReceivedFrameKind.UnparsedRawCapture,
                        [0xAA, 0x55],
                        timestamp,
                        false)),
                TransactionCompletionState.ReceiveOverflow => new TransactionOutcome(
                    transactionId,
                    state,
                    TransactionMode.Standard,
                    timestamp,
                    "接收溢出。",
                    receivedFrame: CreateReceivedFrame(
                        ReceivedFrameKind.ReceiveOverflow,
                        [0xAA, 0x55],
                        timestamp,
                        false)),
                TransactionCompletionState.WriteFailed => new TransactionOutcome(
                    transactionId,
                    state,
                    TransactionMode.Standard,
                    timestamp,
                    "写入失败。",
                    exception: new IOException("测试写故障")),
                _ => new TransactionOutcome(
                    transactionId,
                    state,
                    TransactionMode.Standard,
                    timestamp,
                    $"{state} 测试结果。"),
            };
        }

        /// <summary>
        /// 解析测试响应并创建具有一致原始帧的标准事务结果。
        /// </summary>
        /// <param name="transactionId">结果事务编号。</param>
        /// <param name="state">Succeeded 或 ModbusException。</param>
        /// <param name="request">参与响应签名解析的请求。</param>
        /// <param name="frameData">CRC 正确的完整响应帧。</param>
        /// <param name="timestamp">结果与接收帧完成时间戳。</param>
        /// <returns>新的标准事务结果。</returns>
        private static TransactionOutcome CreateStandardOutcome(
            long transactionId,
            TransactionCompletionState state,
            ModbusRequest request,
            ReadOnlySpan<byte> frameData,
            long timestamp)
        {
            ReceivedFrame frame = CreateReceivedFrame(
                ReceivedFrameKind.StandardFrame,
                frameData,
                timestamp,
                true);
            ModbusResponse response = ModbusResponseParser.Parse(request, frameData);

            return new TransactionOutcome(
                transactionId,
                state,
                TransactionMode.Standard,
                timestamp,
                state == TransactionCompletionState.Succeeded ? "成功。" : "设备异常。",
                response,
                frame);
        }

        /// <summary>
        /// 创建一项具有非空来源和有效时间的接收记录。
        /// </summary>
        /// <param name="kind">接收记录类别。</param>
        /// <param name="data">接收记录字节。</param>
        /// <param name="timestamp">开始与完成单调时间戳。</param>
        /// <param name="isCrcValid">CRC 是否有效。</param>
        /// <returns>新的不可变接收记录。</returns>
        private static ReceivedFrame CreateReceivedFrame(
            ReceivedFrameKind kind,
            ReadOnlySpan<byte> data,
            long timestamp,
            bool isCrcValid)
        {
            return new ReceivedFrame(
                kind,
                data,
                1,
                1,
                1,
                1,
                timestamp,
                timestamp,
                isCrcValid,
                string.Empty,
                data.Length,
                kind == ReceivedFrameKind.ReceiveOverflow ? 1 : 0);
        }
    }
}
