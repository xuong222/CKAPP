using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Integration
{
    /// <summary>
    /// 验证实际协调器在并发终止事件、请求洪泛及手动和定时发送竞争下保持唯一完成与无队列约束。
    /// </summary>
    [TestFixture]
    public sealed class TransactionRaceTests
    {
        /// <summary>
        /// 验证七组终止事件通过同一原子完成门竞争，最终只发布一个结果并只释放一次活动门。
        /// </summary>
        /// <param name="raceCase">需要制造的两个终止事件组合。</param>
        [TestCase(TransactionRaceCase.ResponseAndTimeout)]
        [TestCase(TransactionRaceCase.ExceptionAndTimeout)]
        [TestCase(TransactionRaceCase.WriteFailureAndCancellation)]
        [TestCase(TransactionRaceCase.CancellationAndResponse)]
        [TestCase(TransactionRaceCase.DisconnectionAndTimeout)]
        [TestCase(TransactionRaceCase.OverflowAndCancellation)]
        [TestCase(TransactionRaceCase.ApplicationStoppingAndResponse)]
        public async Task CompetingTerminalEvents_CompleteExactlyOnce(
            TransactionRaceCase raceCase)
        {
            await using IntegrationCoordinatorHarness harness =
                await IntegrationCoordinatorHarness.CreateAsync();
            ConcurrentQueue<TransactionOutcome> completedOutcomes = new();
            ConcurrentQueue<bool> busyChanges = new();
            harness.Coordinator.TransactionCompleted += completedOutcomes.Enqueue;
            harness.Coordinator.BusyChanged += busyChanges.Enqueue;
            using CancellationTokenSource cancellation = new();
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            TimeSpan timeout = TimeSpan.FromMilliseconds(50);
            Task<TransactionExecutionResult> execution;
            Func<Task> firstCandidate;
            Func<Task> secondCandidate;

            if (raceCase == TransactionRaceCase.WriteFailureAndCancellation)
            {
                FakeWritePause writePause = harness.Transport.PauseNextWrite();
                execution = harness.Coordinator.TryExecuteAsync(
                    TransactionRequest.CreateStandard(request, timeout),
                    cancellation.Token).AsTask();
                await writePause.Entered.WaitAsync(TimeSpan.FromSeconds(1));
                firstCandidate = () =>
                {
                    writePause.ReleaseWithFailure(new IOException("模拟写失败竞态。"));
                    return Task.CompletedTask;
                };
                secondCandidate = () =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                };
            }
            else
            {
                CancellationToken executionCancellation = raceCase is
                    TransactionRaceCase.CancellationAndResponse or
                    TransactionRaceCase.OverflowAndCancellation
                        ? cancellation.Token
                        : CancellationToken.None;
                var pendingExchange = await harness.BeginStandardAsync(
                    request,
                    timeout,
                    executionCancellation);
                execution = pendingExchange.Execution;
                ReadOnlyMemory<byte> writtenFrame = pendingExchange.WrittenFrame;

                switch (raceCase)
                {
                    case TransactionRaceCase.ResponseAndTimeout:
                        firstCandidate = () => TryHandleDeviceAsync(
                            harness,
                            writtenFrame,
                            SimulatedModbusBehavior.Normal);
                        secondCandidate = () => AdvanceAsync(harness, timeout);
                        break;
                    case TransactionRaceCase.ExceptionAndTimeout:
                        firstCandidate = () => TryHandleDeviceAsync(
                            harness,
                            writtenFrame,
                            SimulatedModbusBehavior.ModbusException);
                        secondCandidate = () => AdvanceAsync(harness, timeout);
                        break;
                    case TransactionRaceCase.CancellationAndResponse:
                        firstCandidate = () => CancelAsync(cancellation);
                        secondCandidate = () => TryHandleDeviceAsync(
                            harness,
                            writtenFrame,
                            SimulatedModbusBehavior.Normal);
                        break;
                    case TransactionRaceCase.DisconnectionAndTimeout:
                        firstCandidate = () => DisconnectAsync(harness);
                        secondCandidate = () => AdvanceAsync(harness, timeout);
                        break;
                    case TransactionRaceCase.OverflowAndCancellation:
                        firstCandidate = () => InjectOverflowAsync(harness);
                        secondCandidate = () => CancelAsync(cancellation);
                        break;
                    case TransactionRaceCase.ApplicationStoppingAndResponse:
                        firstCandidate = () => harness.Coordinator
                            .StopAsync(CancellationToken.None)
                            .AsTask();
                        secondCandidate = () => TryHandleDeviceAsync(
                            harness,
                            writtenFrame,
                            SimulatedModbusBehavior.Normal);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(raceCase));
                }
            }

            await RunSimultaneouslyAsync(firstCandidate, secondCandidate);
            TransactionExecutionResult result = await execution.WaitAsync(TimeSpan.FromSeconds(1));

            if (raceCase is TransactionRaceCase.DisconnectionAndTimeout or
                TransactionRaceCase.ApplicationStoppingAndResponse)
            {
                await harness.Coordinator.StopAsync(CancellationToken.None);
            }

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    IsAllowedOutcome(raceCase, result.Outcome!.State),
                    Is.True,
                    $"竞态 {raceCase} 得到未允许的终态 {result.Outcome.State}。");
                Assert.That(completedOutcomes, Has.Count.EqualTo(1));
                Assert.That(completedOutcomes.Single(), Is.SameAs(result.Outcome));
                Assert.That(busyChanges.ToArray(), Is.EqualTo(new[] { true, false }));
                Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(1));
            }));

            if (raceCase == TransactionRaceCase.ApplicationStoppingAndResponse)
            {
                Assert.Multiple((Action)(() =>
                {
                    Assert.That(harness.Transport.IsOpen, Is.False);
                    Assert.That(harness.Transport.CloseOperationCount, Is.EqualTo(1));
                }));
            }
        }

        /// <summary>
        /// 验证一个慢事务占用活动门时，一百项并发请求全部立即 Busy，断开后也不会形成积压。
        /// </summary>
        [Test]
        public async Task RequestFlood_DuringSlowResponseAndAfterDisconnect_NeverQueues()
        {
            await using IntegrationCoordinatorHarness harness =
                await IntegrationCoordinatorHarness.CreateAsync();
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            FakeWritePause writePause = harness.Transport.PauseNextWrite();
            Task<TransactionExecutionResult> activeExecution = harness.Coordinator
                .TryExecuteAsync(
                    TransactionRequest.CreateStandard(request),
                    CancellationToken.None)
                .AsTask();
            await writePause.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            Task<TransactionExecutionResult>[] busyExecutions = Enumerable.Range(0, 100)
                .Select(_ => harness.Coordinator.TryExecuteAsync(
                    TransactionRequest.CreateStandard(request),
                    CancellationToken.None).AsTask())
                .ToArray();
            TransactionExecutionResult[] busyResults = await Task.WhenAll(busyExecutions);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(busyResults.All(result => result.Rejection == TransactionRejected.Busy), Is.True);
                Assert.That(busyResults.All(result => result.TransactionId is null), Is.True);
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(1));
            }));

            writePause.Release();
            await IntegrationCoordinatorHarness.WaitUntilAsync(
                () => harness.Transport.WrittenFrames.Count == 1,
                "慢事务释放后没有形成唯一物理写入。");
            await harness.WaitForStateAsync(TransactionCoordinatorState.WaitingResponse);
            await harness.Device.HandleWriteAsync(harness.Transport.WrittenFrames[0]);
            TransactionExecutionResult activeResult = await activeExecution.WaitAsync(TimeSpan.FromSeconds(1));
            harness.Transport.RemoteDisconnect(new IOException("洪泛后模拟拔出。"));
            await harness.WaitForStateAsync(TransactionCoordinatorState.Disconnected);
            TransactionExecutionResult[] disconnectedResults = await Task.WhenAll(
                Enumerable.Range(0, 100)
                    .Select(_ => harness.Coordinator.TryExecuteAsync(
                        TransactionRequest.CreateStandard(request),
                        CancellationToken.None).AsTask()));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(activeResult.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(disconnectedResults.All(result =>
                    result.Rejection == TransactionRejected.NotConnected), Is.True);
                Assert.That(disconnectedResults.All(result => result.TransactionId is null), Is.True);
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(1));
                Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
            }));
        }

        /// <summary>
        /// 验证定时请求与手动慢事务同时到达时定时请求立即 Busy，并在完整新间隔后才再次尝试。
        /// </summary>
        [Test]
        public async Task ManualAndPeriodicCollision_OneWinsAndPeriodicWaitsFullNextInterval()
        {
            await using IntegrationCoordinatorHarness harness =
                await IntegrationCoordinatorHarness.CreateAsync();
            await using PeriodicSendService periodic = new(
                harness.Coordinator,
                harness.TimeProvider);
            ConcurrentQueue<TransactionExecutionResult> attempts = new();
            periodic.AttemptCompleted += attempts.Enqueue;
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            TransactionRequest transaction = TransactionRequest.CreateStandard(request);
            periodic.Configure(transaction, TimeSpan.FromMilliseconds(100));
            periodic.Start();
            FakeWritePause writePause = harness.Transport.PauseNextWrite();
            Task<TransactionExecutionResult> manualExecution = harness.Coordinator
                .TryExecuteAsync(transaction, CancellationToken.None)
                .AsTask();
            await writePause.Entered.WaitAsync(TimeSpan.FromSeconds(1));

            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            await IntegrationCoordinatorHarness.WaitUntilAsync(
                () => attempts.Count == 1,
                "首个定时 Busy 尝试未完成。");
            writePause.Release();
            await IntegrationCoordinatorHarness.WaitUntilAsync(
                () => harness.Transport.WrittenFrames.Count == 1,
                "手动事务释放后未写出请求。");
            await harness.WaitForStateAsync(TransactionCoordinatorState.WaitingResponse);
            await harness.Device.HandleWriteAsync(harness.Transport.WrittenFrames[0]);
            TransactionExecutionResult manualResult = await manualExecution.WaitAsync(TimeSpan.FromSeconds(1));

            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(99));
            await Task.Yield();
            int writesBeforeFullInterval = harness.Transport.WrittenFrames.Count;
            long attemptsBeforeFullInterval = periodic.CompletedAttemptCount;
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await IntegrationCoordinatorHarness.WaitUntilAsync(
                () => harness.Transport.WrittenFrames.Count == 2,
                "完整新间隔后定时事务没有写出请求。");
            await harness.WaitForStateAsync(TransactionCoordinatorState.WaitingResponse);
            await harness.Device.HandleWriteAsync(harness.Transport.WrittenFrames[1]);
            await IntegrationCoordinatorHarness.WaitUntilAsync(
                () => attempts.Count == 2,
                "第二个定时成功尝试未完成。");
            await periodic.StopAsync(CancellationToken.None);
            TransactionExecutionResult[] attemptResults = attempts.ToArray();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(manualResult.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(attemptResults[0].Rejection, Is.EqualTo(TransactionRejected.Busy));
                Assert.That(attemptResults[0].TransactionId, Is.Null);
                Assert.That(writesBeforeFullInterval, Is.EqualTo(1));
                Assert.That(attemptsBeforeFullInterval, Is.EqualTo(1));
                Assert.That(attemptResults[1].Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(attemptResults[1].TransactionId, Is.EqualTo(2));
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(2));
                Assert.That(periodic.IsRunning, Is.False);
            }));
        }

        /// <summary>
        /// 使用同步屏障让两个候选事件尽可能同时进入协调器完成入口。
        /// </summary>
        /// <param name="firstCandidate">第一项终止事件。</param>
        /// <param name="secondCandidate">第二项终止事件。</param>
        /// <returns>两个候选事件均退出后的任务。</returns>
        private static async Task RunSimultaneouslyAsync(
            Func<Task> firstCandidate,
            Func<Task> secondCandidate)
        {
            ArgumentNullException.ThrowIfNull(firstCandidate);
            ArgumentNullException.ThrowIfNull(secondCandidate);
            using Barrier barrier = new(3);
            Task first = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await firstCandidate().ConfigureAwait(false);
            });
            Task second = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await secondCandidate().ConfigureAwait(false);
            });
            barrier.SignalAndWait();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 尝试让模拟设备处理请求；连接级事件先胜出时，旧会话注入失败按预期隔离。
        /// </summary>
        /// <param name="harness">提供模拟设备和传输会话的测试组合。</param>
        /// <param name="writtenFrame">本事务已经写出的完整请求帧。</param>
        /// <param name="behavior">需要制造的正常或异常响应。</param>
        /// <returns>响应注入或预期隔离完成后的任务。</returns>
        private static async Task TryHandleDeviceAsync(
            IntegrationCoordinatorHarness harness,
            ReadOnlyMemory<byte> writtenFrame,
            SimulatedModbusBehavior behavior)
        {
            try
            {
                await harness.Device.HandleWriteAsync(
                    writtenFrame,
                    behavior,
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                ChannelClosedException or
                ObjectDisposedException)
            {
                // 断开或应用退出已经取得会话所有权时，响应候选不得重新进入旧接收通道。
            }
        }

        /// <summary>
        /// 推进确定性时间以触发响应总超时。
        /// </summary>
        /// <param name="harness">持有手动时间源的测试组合。</param>
        /// <param name="amount">需要推进的非负时长。</param>
        /// <returns>同步推进完成的任务。</returns>
        private static Task AdvanceAsync(
            IntegrationCoordinatorHarness harness,
            TimeSpan amount)
        {
            harness.TimeProvider.Advance(amount);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 取消当前事务调用方令牌。
        /// </summary>
        /// <param name="cancellation">需要触发的取消源。</param>
        /// <returns>同步取消完成的任务。</returns>
        private static Task CancelAsync(CancellationTokenSource cancellation)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 模拟远端串口故障断开。
        /// </summary>
        /// <param name="harness">提供当前模拟传输的测试组合。</param>
        /// <returns>同步断开触发完成的任务。</returns>
        private static Task DisconnectAsync(IntegrationCoordinatorHarness harness)
        {
            harness.Transport.RemoteDisconnect(new IOException("模拟断开与超时竞态。"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// 向标准事务注入达到缓存上限的无有效 CRC 噪声。
        /// </summary>
        /// <param name="harness">提供当前模拟接收通道的测试组合。</param>
        /// <returns>溢出数据进入接收通道后的任务。</returns>
        private static async Task InjectOverflowAsync(IntegrationCoordinatorHarness harness)
        {
            try
            {
                await harness.Transport.InjectReceiveAsync(
                    SimulatedDeviceEndToEndTests.CreateUnknownNonCrcData(4096));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ChannelClosedException)
            {
                // 取消或连接级事件先关闭会话时，溢出候选不再改变已经确定的事务终态。
            }
        }

        /// <summary>
        /// 判断竞态结果是否属于对应两个候选事件允许产生的终态集合。
        /// </summary>
        /// <param name="raceCase">正在验证的竞态组合。</param>
        /// <param name="state">协调器实际得到的唯一终态。</param>
        /// <returns>状态由任一候选事件合法产生时返回真。</returns>
        private static bool IsAllowedOutcome(
            TransactionRaceCase raceCase,
            TransactionCompletionState state)
        {
            return raceCase switch
            {
                TransactionRaceCase.ResponseAndTimeout => state is
                    TransactionCompletionState.Succeeded or TransactionCompletionState.TimedOut,
                TransactionRaceCase.ExceptionAndTimeout => state is
                    TransactionCompletionState.ModbusException or TransactionCompletionState.TimedOut,
                TransactionRaceCase.WriteFailureAndCancellation => state is
                    TransactionCompletionState.WriteFailed or TransactionCompletionState.Cancelled,
                TransactionRaceCase.CancellationAndResponse => state is
                    TransactionCompletionState.Cancelled or TransactionCompletionState.Succeeded,
                TransactionRaceCase.DisconnectionAndTimeout => state is
                    TransactionCompletionState.Disconnected or TransactionCompletionState.TimedOut,
                TransactionRaceCase.OverflowAndCancellation => state is
                    TransactionCompletionState.ReceiveOverflow or TransactionCompletionState.Cancelled,
                TransactionRaceCase.ApplicationStoppingAndResponse => state is
                    TransactionCompletionState.ApplicationStopping or TransactionCompletionState.Succeeded,
                _ => false,
            };
        }
    }

    /// <summary>
    /// 指定需要在实际事务协调器上制造的两项终止事件组合。
    /// </summary>
    public enum TransactionRaceCase
    {
        /// <summary>
        /// 正常响应与总响应超时竞争。
        /// </summary>
        ResponseAndTimeout,

        /// <summary>
        /// Modbus 异常响应与总响应超时竞争。
        /// </summary>
        ExceptionAndTimeout,

        /// <summary>
        /// 物理写 I/O 失败与调用方取消竞争。
        /// </summary>
        WriteFailureAndCancellation,

        /// <summary>
        /// 调用方取消与正常响应竞争。
        /// </summary>
        CancellationAndResponse,

        /// <summary>
        /// 串口断开与总响应超时竞争。
        /// </summary>
        DisconnectionAndTimeout,

        /// <summary>
        /// 接收缓存溢出与调用方取消竞争。
        /// </summary>
        OverflowAndCancellation,

        /// <summary>
        /// 应用退出与正常响应竞争。
        /// </summary>
        ApplicationStoppingAndResponse,
    }
}
