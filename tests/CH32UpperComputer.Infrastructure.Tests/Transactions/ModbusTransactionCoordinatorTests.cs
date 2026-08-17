using System.Threading.Channels;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Framing;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Transactions
{
    /// <summary>
    /// 验证无队列事务协调、双模式完成、严格截止和迟到响应重同步完整功能。
    /// </summary>
    [TestFixture]
    public sealed class ModbusTransactionCoordinatorTests
    {
        /// <summary>
        /// 验证启动接收循环不会自动写入；Sending 期间数据只记证据，Busy 请求不分配编号或排队。
        /// </summary>
        [Test]
        public async Task StartAndBusyGate_NoAutomaticOrQueuedWrite_AndPreWriteDataIsUnsolicited()
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            harness.TimeProvider.Advance(TimeSpan.FromSeconds(5));
            Assert.That(harness.Transport.WrittenFrames, Is.Empty);
            TransactionRequest firstRequest = CreateReadRequest(TimeSpan.FromSeconds(1));
            FakeWritePause writePause = harness.Transport.PauseNextWrite();
            List<bool> busyChanges = [];
            harness.Coordinator.BusyChanged += busyChanges.Add;
            Task<TransactionExecutionResult> firstExecution = harness.Coordinator
                .TryExecuteAsync(firstRequest, CancellationToken.None)
                .AsTask();
            await writePause.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            byte[] response = CreateReadResponse(0x002A);

            await harness.Transport.InjectReceiveAsync(response);
            await WaitForReceiveSequenceAsync(harness.Coordinator, 1);
            TransactionExecutionResult sameBusy = await harness.Coordinator.TryExecuteAsync(
                firstRequest,
                CancellationToken.None);
            TransactionExecutionResult differentBusy = await harness.Coordinator.TryExecuteAsync(
                TransactionRequest.CreateStandard(
                    ModbusRequestFactory.CreateReadHoldingRegisters(1, 1, 1)),
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Coordinator.State, Is.EqualTo(TransactionCoordinatorState.Sending));
                Assert.That(sameBusy.Rejection, Is.EqualTo(TransactionRejected.Busy));
                Assert.That(differentBusy.Rejection, Is.EqualTo(TransactionRejected.Busy));
                Assert.That(sameBusy.TransactionId, Is.Null);
                Assert.That(differentBusy.TransactionId, Is.Null);
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(1));
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
                Assert.That(firstExecution.IsCompleted, Is.False);
            }));

            writePause.Release();
            await WaitForStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            await harness.Transport.InjectReceiveAsync(response);
            TransactionExecutionResult firstResult = await AwaitExecutionAsync(firstExecution);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(firstResult.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(firstResult.TransactionId, Is.EqualTo(1));
                Assert.That(busyChanges, Is.EqualTo(new[] { true, false }));
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证设备在完整请求已经写出、传输调用尚未返回时立即应答，响应仍能完成当前事务。
        /// </summary>
        [Test]
        public async Task ResponseAfterPhysicalWriteBeforeWriteReturns_CompletesCurrentTransaction()
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            TransactionRequest request = CreateReadRequest(TimeSpan.FromMilliseconds(100));
            FakeWritePause completionPause = harness.Transport.PauseNextWriteCompletion();
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(request, CancellationToken.None)
                .AsTask();
            await completionPause.Entered.WaitAsync(TimeSpan.FromSeconds(1));

            await harness.Transport.InjectReceiveAsync(CreateReadResponse(0x002A));
            await WaitForReceiveSequenceAsync(harness.Coordinator, 1);
            completionPause.Release();
            TransactionExecutionResult result = await AwaitExecutionAsync(execution);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.IsAccepted, Is.True);
                Assert.That(result.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(result.Outcome.Response!.Registers.Span[0], Is.EqualTo(0x002A));
            }));
        }

        /// <summary>
        /// 验证未连接和应用停止后的请求均在分配事务编号、写串口之前立即拒绝。
        /// </summary>
        [Test]
        public async Task LifecycleRejection_NotConnectedOrStopping_DoesNotAllocateOrWrite()
        {
            ManualTimeProvider timeProvider = new();
            await using FakeSerialTransport transport = new(timeProvider);
            await using ModbusTransactionCoordinator coordinator = new(transport, timeProvider);

            TransactionExecutionResult notConnected = await coordinator.TryExecuteAsync(
                CreateReadRequest(),
                CancellationToken.None);
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            await coordinator.StartAsync(CancellationToken.None);
            await coordinator.StopAsync(CancellationToken.None);
            TransactionExecutionResult stopping = await coordinator.TryExecuteAsync(
                CreateReadRequest(),
                CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(notConnected.Rejection, Is.EqualTo(TransactionRejected.NotConnected));
                Assert.That(stopping.Rejection, Is.EqualTo(TransactionRejected.ApplicationStopping));
                Assert.That(notConnected.TransactionId, Is.Null);
                Assert.That(stopping.TransactionId, Is.Null);
                Assert.That(coordinator.LastTransactionId, Is.Zero);
                Assert.That(transport.WrittenFrames, Is.Empty);
            }));
        }

        /// <summary>
        /// 验证正常响应、标准异常响应以及同批后续帧只产生一个事务终态。
        /// </summary>
        /// <param name="useExceptionResponse">是否注入标准异常而不是正常读响应。</param>
        /// <param name="expectedState">预期唯一终态。</param>
        [TestCase(false, TransactionCompletionState.Succeeded)]
        [TestCase(true, TransactionCompletionState.ModbusException)]
        public async Task StandardResponse_NormalOrException_CompletesExpectedState(
            bool useExceptionResponse,
            TransactionCompletionState expectedState)
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(CreateReadRequest(), CancellationToken.None)
                .AsTask();
            byte[] response = useExceptionResponse
                ? ModbusCrc16.Append([0x01, 0x83, 0x02])
                : CreateReadResponse(0x1234);

            await harness.Transport.InjectReceiveAsync(response);
            TransactionExecutionResult result = await AwaitExecutionAsync(execution);
            TransactionOutcome outcome = result.Outcome!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.IsAccepted, Is.True);
                Assert.That(outcome.State, Is.EqualTo(expectedState));
                Assert.That(outcome.Response, Is.Not.Null);
                Assert.That(outcome.ReceivedFrame!.Kind, Is.EqualTo(ReceivedFrameKind.StandardFrame));
                Assert.That(harness.Coordinator.State, Is.EqualTo(TransactionCoordinatorState.ConnectedIdle));
            }));
        }

        /// <summary>
        /// 验证协调器把 0x03、0x06、0x10 和固定 0xFE 查询交给各自完整响应签名校验。
        /// </summary>
        /// <param name="successCase">需要执行的标准请求类型。</param>
        [TestCase(StandardSuccessCase.ReadHoldingRegisters)]
        [TestCase(StandardSuccessCase.WriteSingleRegister)]
        [TestCase(StandardSuccessCase.WriteMultipleRegisters)]
        [TestCase(StandardSuccessCase.UnknownAddressQuery)]
        public async Task StandardResponse_AllSupportedSignatures_CompleteSuccessfully(
            StandardSuccessCase successCase)
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            (ModbusRequest request, byte[] response) = CreateStandardSuccessExchange(successCase);
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(
                    TransactionRequest.CreateStandard(request),
                    CancellationToken.None)
                .AsTask();

            await harness.Transport.InjectReceiveAsync(response);
            TransactionExecutionResult result = await AwaitExecutionAsync(execution);
            TransactionOutcome outcome = result.Outcome!;
            ModbusResponse parsedResponse = outcome.Response!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(outcome.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(parsedResponse.Status, Is.EqualTo(ModbusResponseStatus.Succeeded));
                Assert.That(outcome.RawData.Span.ToArray(), Is.EqualTo(response));
                Assert.That(parsedResponse.DiscoveredSlaveAddress, Is.EqualTo(
                    successCase == StandardSuccessCase.UnknownAddressQuery ? (byte?)5 : null));
            }));
        }

        /// <summary>
        /// 验证不匹配候选不会完成事务，而同一接收块中首个匹配帧后的附加帧只记 Unsolicited。
        /// </summary>
        [Test]
        public async Task StandardResponse_MismatchThenTwoMatches_UsesFirstMatchingCandidateOnly()
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(CreateReadRequest(), CancellationToken.None)
                .AsTask();
            byte[] mismatched = ModbusCrc16.Append([0x02, 0x03, 0x02, 0x00, 0x01]);
            byte[] matching = CreateReadResponse(0x002A);

            await harness.Transport.InjectReceiveAsync(mismatched);
            await WaitForReceiveSequenceAsync(harness.Coordinator, 1);
            Assert.That(execution.IsCompleted, Is.False);
            byte[] stickyMatchingResponses = [.. matching, .. matching];
            await harness.Transport.InjectReceiveAsync(stickyMatchingResponses);
            await WaitForReceiveSequenceAsync(harness.Coordinator, 2);
            TransactionExecutionResult result = await AwaitExecutionAsync(execution);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(harness.Coordinator.Diagnostics.Any(
                    diagnostic => diagnostic.Kind == TransactionDiagnosticKind.MismatchedResponse), Is.True);
                Assert.That(harness.Coordinator.Diagnostics.Any(
                    diagnostic => diagnostic.Kind == TransactionDiagnosticKind.Unsolicited), Is.True);
            }));
        }

        /// <summary>
        /// 验证写失败、用户取消、串口断开和应用退出均通过对应终态完成，并只释放一次活动门。
        /// </summary>
        /// <param name="terminalCase">需要制造的非响应终止事件。</param>
        /// <param name="expectedState">预期终态。</param>
        [TestCase(NonResponseTerminalCase.WriteFailure, TransactionCompletionState.WriteFailed)]
        [TestCase(NonResponseTerminalCase.Cancellation, TransactionCompletionState.Cancelled)]
        [TestCase(NonResponseTerminalCase.Disconnection, TransactionCompletionState.Disconnected)]
        [TestCase(NonResponseTerminalCase.ApplicationStopping, TransactionCompletionState.ApplicationStopping)]
        public async Task NonResponseTerminalEvent_CompletesThroughExpectedState(
            NonResponseTerminalCase terminalCase,
            TransactionCompletionState expectedState)
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            using CancellationTokenSource cancellation = new();
            FakeWritePause? pause = null;

            if (terminalCase == NonResponseTerminalCase.WriteFailure)
            {
                harness.Transport.FailNextWrite(new IOException("写线路故障"));
            }
            else if (terminalCase == NonResponseTerminalCase.Cancellation)
            {
                pause = harness.Transport.PauseNextWrite();
            }

            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(CreateReadRequest(), cancellation.Token)
                .AsTask();

            switch (terminalCase)
            {
                case NonResponseTerminalCase.WriteFailure:
                    break;
                case NonResponseTerminalCase.Cancellation:
                    await pause!.Entered.WaitAsync(TimeSpan.FromSeconds(1));
                    cancellation.Cancel();
                    pause.Release();
                    break;
                case NonResponseTerminalCase.Disconnection:
                    harness.Transport.RemoteDisconnect(new IOException("设备拔出"));
                    break;
                case NonResponseTerminalCase.ApplicationStopping:
                    await harness.Coordinator.StopAsync(CancellationToken.None);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(terminalCase));
            }

            TransactionExecutionResult result = await AwaitExecutionAsync(execution);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Outcome!.State, Is.EqualTo(expectedState));
                Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
                Assert.That(result.TransactionId, Is.EqualTo(1));
            }));

            if (terminalCase is NonResponseTerminalCase.WriteFailure or NonResponseTerminalCase.Cancellation)
            {
                Assert.That(
                    harness.Coordinator.State,
                    Is.EqualTo(TransactionCoordinatorState.Resynchronizing));
            }
        }

        /// <summary>
        /// 验证正常响应与远端断开同时发生时只有一个事务终态和一次完成通知，连接状态仍独立收敛。
        /// </summary>
        [Test]
        public async Task ResponseAndDisconnectRace_HasOneOutcomeAndDisconnectedLifecycle()
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            List<TransactionOutcome> completedOutcomes = [];
            harness.Coordinator.TransactionCompleted += completedOutcomes.Add;
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(CreateReadRequest(), CancellationToken.None)
                .AsTask();
            using Barrier barrier = new(3);
            Task responseCandidate = Task.Run(async () =>
            {
                barrier.SignalAndWait();

                try
                {
                    await harness.Transport.InjectReceiveAsync(CreateReadResponse(0x002A));
                }
                catch (ChannelClosedException)
                {
                    // 注入已经取得旧会话、但断开先完成通道时，候选同样按预期被隔离。
                }
                catch (InvalidOperationException)
                {
                    // 断开先取得会话所有权时，响应候选按预期无法再进入旧会话。
                }
            });
            Task disconnectCandidate = Task.Run(() =>
            {
                barrier.SignalAndWait();
                harness.Transport.RemoteDisconnect(new IOException("响应边界拔出"));
            });

            barrier.SignalAndWait();
            await Task.WhenAll(responseCandidate, disconnectCandidate)
                .WaitAsync(TimeSpan.FromSeconds(1));
            TransactionExecutionResult result = await AwaitExecutionAsync(execution);
            await WaitForStateAsync(harness.Coordinator, TransactionCoordinatorState.Disconnected);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    result.Outcome!.State,
                    Is.EqualTo(TransactionCompletionState.Succeeded)
                        .Or.EqualTo(TransactionCompletionState.Disconnected));
                Assert.That(completedOutcomes, Has.Count.EqualTo(1));
                Assert.That(completedOutcomes[0], Is.SameAs(result.Outcome));
                Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
                Assert.That(harness.Transport.IsOpen, Is.False);
            }));
        }

        /// <summary>
        /// 验证标准接收达到 4096 字节上限时以 ReceiveOverflow 终止并进入重同步。
        /// </summary>
        [Test]
        public async Task StandardReceiveOverflow_FailsAndStartsResynchronization()
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(CreateReadRequest(), CancellationToken.None)
                .AsTask();

            await harness.Transport.InjectReceiveAsync(CreateUnknownNonCrcData(4096));
            TransactionExecutionResult result = await AwaitExecutionAsync(execution);
            TransactionOutcome outcome = result.Outcome!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(outcome.State, Is.EqualTo(TransactionCompletionState.ReceiveOverflow));
                Assert.That(outcome.ReceivedFrame!.Kind, Is.EqualTo(ReceivedFrameKind.ReceiveOverflow));
                Assert.That(harness.Coordinator.State, Is.EqualTo(TransactionCoordinatorState.Resynchronizing));
            }));
        }

        /// <summary>
        /// 验证原始调试对已知结构、未知 CRC、静默残片、总截止残片、空超时和捕获溢出的完成契约。
        /// </summary>
        /// <param name="rawCase">需要制造的原始调试响应边界。</param>
        /// <param name="expectedState">预期事务终态。</param>
        /// <param name="expectedKind">预期接收记录类别；空超时时为空。</param>
        [TestCase(RawCompletionCase.KnownStructure, TransactionCompletionState.RawCaptured, ReceivedFrameKind.StandardFrame)]
        [TestCase(RawCompletionCase.UnknownCrc, TransactionCompletionState.RawCaptured, ReceivedFrameKind.RawCrcFrame)]
        [TestCase(RawCompletionCase.InterByteSilence, TransactionCompletionState.RawCaptured, ReceivedFrameKind.UnparsedRawCapture)]
        [TestCase(RawCompletionCase.TotalTimeoutWithBytes, TransactionCompletionState.RawCaptured, ReceivedFrameKind.UnparsedRawCapture)]
        [TestCase(RawCompletionCase.TotalTimeoutWithoutBytes, TransactionCompletionState.TimedOut, null)]
        [TestCase(RawCompletionCase.CaptureOverflow, TransactionCompletionState.RawCaptured, ReceivedFrameKind.ReceiveOverflow)]
        public async Task RawDebug_ResponseBoundary_CompletesContract(
            RawCompletionCase rawCase,
            TransactionCompletionState expectedState,
            ReceivedFrameKind? expectedKind)
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            TimeSpan responseTimeout = rawCase is RawCompletionCase.TotalTimeoutWithBytes or
                RawCompletionCase.TotalTimeoutWithoutBytes
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromSeconds(1);
            TimeSpan interByteTimeout = rawCase == RawCompletionCase.TotalTimeoutWithBytes
                ? TimeSpan.FromMilliseconds(1000)
                : TimeSpan.FromMilliseconds(20);
            TransactionRequest request = TransactionRequest.CreateRawDebug(
                ModbusCrc16.Append([0x01, 0x44, 0x00]),
                responseTimeout,
                interByteTimeout);
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(request, CancellationToken.None)
                .AsTask();

            switch (rawCase)
            {
                case RawCompletionCase.KnownStructure:
                    byte[] knownResponse = CreateReadResponse(0x002A);
                    byte[] stickyKnownResponses = [.. knownResponse, .. knownResponse];
                    await harness.Transport.InjectReceiveAsync(stickyKnownResponses);
                    break;
                case RawCompletionCase.UnknownCrc:
                    await harness.Transport.InjectReceiveAsync(ModbusCrc16.Append([0x01, 0x44, 0x5A]));
                    break;
                case RawCompletionCase.InterByteSilence:
                    await harness.Transport.InjectReceiveAsync(new byte[] { 0x01, 0x44, 0xAA });
                    await WaitForReceiveSequenceAsync(harness.Coordinator, 1);
                    harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(20));
                    break;
                case RawCompletionCase.TotalTimeoutWithBytes:
                    await harness.Transport.InjectReceiveAsync(new byte[] { 0x01, 0x44, 0xAA });
                    await WaitForReceiveSequenceAsync(harness.Coordinator, 1);
                    harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(50));
                    break;
                case RawCompletionCase.TotalTimeoutWithoutBytes:
                    harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(50));
                    break;
                case RawCompletionCase.CaptureOverflow:
                    await harness.Transport.InjectReceiveAsync(CreateUnknownNonCrcData(4096));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(rawCase));
            }

            TransactionExecutionResult result = await AwaitExecutionAsync(execution);
            TransactionOutcome outcome = result.Outcome!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(outcome.State, Is.EqualTo(expectedState));
                Assert.That(outcome.ReceivedFrame?.Kind, Is.EqualTo(expectedKind));
                Assert.That(outcome.Response, Is.Null);
            }));

            if (expectedState == TransactionCompletionState.RawCaptured)
            {
                Assert.That(outcome.RawData.IsEmpty, Is.False);
            }

            if (rawCase == RawCompletionCase.KnownStructure)
            {
                Assert.That(harness.Coordinator.Diagnostics.Any(diagnostic =>
                    diagnostic.Kind == TransactionDiagnosticKind.Unsolicited), Is.True);
            }
        }

        /// <summary>
        /// 验证截止前一 tick 成功，而截止同 tick 和后一 tick 均严格超时且只产生一个终态。
        /// </summary>
        /// <param name="relativeTick">相对严格截止的 tick 偏移。</param>
        /// <param name="expectedState">预期最终状态。</param>
        [TestCase(-1, TransactionCompletionState.Succeeded)]
        [TestCase(0, TransactionCompletionState.TimedOut)]
        [TestCase(1, TransactionCompletionState.TimedOut)]
        public async Task ResponseDeadline_BeforeAtAfter_IsStrictAndAtomic(
            int relativeTick,
            TransactionCompletionState expectedState)
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            TimeSpan timeout = TimeSpan.FromMilliseconds(50);
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(CreateReadRequest(timeout), CancellationToken.None)
                .AsTask();
            long deadlineTimestamp = timeout.Ticks;

            await harness.Transport.InjectReceiveAtAsync(
                CreateReadResponse(0x002A),
                DateTimeOffset.UnixEpoch.AddTicks(deadlineTimestamp + relativeTick),
                deadlineTimestamp + relativeTick);
            TransactionExecutionResult result = await AwaitExecutionAsync(execution);
            TransactionOutcome outcome = result.Outcome!;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(outcome.State, Is.EqualTo(expectedState));
                Assert.That(outcome.CompletedTimestamp, Is.EqualTo(
                    expectedState == TransactionCompletionState.Succeeded
                        ? deadlineTimestamp - 1
                        : deadlineTimestamp + Math.Max(relativeTick, 0)));
                Assert.That(result.TransactionId, Is.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证 A 超时后隔离期迟到 A 不会完成未来 B，完整静默后才允许手动提交相同 B。
        /// </summary>
        [Test]
        public async Task TimedOutA_LateIsolationThenIdenticalB_PreservesTransactionIdentity()
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            TransactionRequest request = CreateReadRequest(TimeSpan.FromMilliseconds(50));
            Task<TransactionExecutionResult> firstExecution = harness.Coordinator
                .TryExecuteAsync(request, CancellationToken.None)
                .AsTask();
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(50));
            TransactionExecutionResult firstResult = await AwaitExecutionAsync(firstExecution);
            long firstId = firstResult.TransactionId!.Value;
            byte[] response = CreateReadResponse(0x002A);

            await harness.Transport.InjectReceiveAsync(response);
            await WaitForReceiveSequenceAsync(harness.Coordinator, 1);
            TransactionExecutionResult blockedRetry = await harness.Coordinator.TryExecuteAsync(
                request,
                CancellationToken.None);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(99));
            Assert.That(harness.Coordinator.State, Is.EqualTo(TransactionCoordinatorState.Resynchronizing));
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await WaitForStateAsync(harness.Coordinator, TransactionCoordinatorState.ConnectedIdle);
            Assert.That(harness.Coordinator.HasLateResponseRisk, Is.True);
            Task<TransactionExecutionResult> secondExecution = harness.Coordinator
                .TryExecuteAsync(request, CancellationToken.None)
                .AsTask();
            await harness.Transport.InjectReceiveAsync(response);
            TransactionExecutionResult secondResult = await AwaitExecutionAsync(secondExecution);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(firstResult.Outcome!.State, Is.EqualTo(TransactionCompletionState.TimedOut));
                Assert.That(blockedRetry.Rejection, Is.EqualTo(TransactionRejected.Resynchronizing));
                Assert.That(blockedRetry.TransactionId, Is.Null);
                Assert.That(secondResult.TransactionId, Is.EqualTo(firstId + 1));
                Assert.That(secondResult.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(harness.Coordinator.LastTimedOutTransactionId, Is.EqualTo(firstId));
                Assert.That(harness.Coordinator.LastTimedOutRequest, Is.SameAs(request));
                Assert.That(harness.Coordinator.Diagnostics.Any(diagnostic =>
                    diagnostic.Kind == TransactionDiagnosticKind.LateOrUnsolicited &&
                    diagnostic.TransactionId == firstId), Is.True);
            }));
        }

        /// <summary>
        /// 验证持续迟到字节令静默窗口不断后移，并在两倍 guard 绝对上限强制断开。
        /// </summary>
        [Test]
        public async Task ResynchronizationWithoutFullSilence_AtTwoGuardsForcesDisconnect()
        {
            await using CoordinatorHarness harness = await CoordinatorHarness.CreateAsync();
            Task<TransactionExecutionResult> execution = harness.Coordinator
                .TryExecuteAsync(CreateReadRequest(TimeSpan.FromMilliseconds(50)), CancellationToken.None)
                .AsTask();
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(50));
            await AwaitExecutionAsync(execution);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(90));
            await harness.Transport.InjectReceiveAsync(new byte[] { 0xAA });
            await WaitForReceiveSequenceAsync(harness.Coordinator, 1);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(90));
            await harness.Transport.InjectReceiveAsync(new byte[] { 0xBB });
            await WaitForReceiveSequenceAsync(harness.Coordinator, 2);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(20));
            await harness.Coordinator.WaitForBackgroundOperationsAsync(CancellationToken.None);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Coordinator.State, Is.EqualTo(TransactionCoordinatorState.Disconnected));
                Assert.That(harness.Transport.IsOpen, Is.False);
                Assert.That(harness.Coordinator.Diagnostics.Any(diagnostic =>
                    diagnostic.Message.Contains("强制断开", StringComparison.Ordinal)), Is.True);
            }));
        }

        /// <summary>
        /// 验证写请求残余歧义摘要覆盖完整值序列，而不是只记录地址和数量。
        /// </summary>
        [Test]
        public void TransactionRequestSignature_WriteValuesChange_ProducesDistinctFullSignature()
        {
            ushort[] firstValues = [
                0x0001,
                0x0002,
                0x0003,
                0x0004,
                0x0005,
                0x0006,
                0x0007,
                0x0008,
                0x0009,
            ];
            ushort[] secondValues = (ushort[])firstValues.Clone();
            secondValues[^1] = 0x000A;
            TransactionRequest first = TransactionRequest.CreateStandard(
                ModbusRequestFactory.CreateWriteMultipleRegisters(1, 0x0010, firstValues));
            TransactionRequest second = TransactionRequest.CreateStandard(
                ModbusRequestFactory.CreateWriteMultipleRegisters(1, 0x0010, secondValues));
            string firstSignature = first.CreateSignatureSummary();
            string secondSignature = second.CreateSignatureSummary();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(firstSignature, Does.Contain("写值 [0x0001,0x0002"));
                Assert.That(firstSignature, Does.Contain("…(+1)"));
                Assert.That(firstSignature, Does.Contain("值签名 0x"));
                Assert.That(secondSignature, Is.Not.EqualTo(firstSignature));
            }));
        }

        /// <summary>
        /// 验证生产协调器源码不包含请求等待容器，所有提交只能经过原子活动标志。
        /// </summary>
        [Test]
        public void SourcePolicy_HasNoRequestQueueOrUncontrolledTime()
        {
            string repositoryRoot = FindRepositoryRoot(TestContext.CurrentContext.TestDirectory);
            string source = File.ReadAllText(Path.Combine(
                repositoryRoot,
                "src",
                "CH32UpperComputer.Infrastructure",
                "Transactions",
                "ModbusTransactionCoordinator.cs"));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(source, Does.Contain("Interlocked.CompareExchange(ref activeTransactionFlag, 1, 0)"));
                Assert.That(source, Does.Not.Contain("ConcurrentQueue"));
                Assert.That(source, Does.Not.Contain("Channel<TransactionRequest"));
                Assert.That(source, Does.Not.Contain("Task.Delay"));
                Assert.That(source, Does.Not.Contain("DateTime.Now"));
                Assert.That(source, Does.Not.Contain("DateTimeOffset.Now"));
            }));
        }

        /// <summary>
        /// 创建默认读取请求事务。
        /// </summary>
        /// <param name="responseTimeout">可选响应总超时。</param>
        /// <returns>读取地址一、寄存器零、数量一的标准事务请求。</returns>
        private static TransactionRequest CreateReadRequest(TimeSpan? responseTimeout = null)
        {
            return TransactionRequest.CreateStandard(
                ModbusRequestFactory.CreateReadHoldingRegisters(1, 0, 1),
                responseTimeout);
        }

        /// <summary>
        /// 创建返回一个寄存器的 CRC 正确读响应。
        /// </summary>
        /// <param name="value">响应寄存器值。</param>
        /// <returns>地址一、功能 0x03 的完整响应帧。</returns>
        private static byte[] CreateReadResponse(ushort value)
        {
            return ModbusCrc16.Append(
                [0x01, 0x03, 0x02, (byte)(value >> 8), (byte)value]);
        }

        /// <summary>
        /// 创建受支持标准功能的一组严格请求与成功响应。
        /// </summary>
        /// <param name="successCase">需要创建的标准功能场景。</param>
        /// <returns>结构化请求以及必须与之完整匹配的响应帧。</returns>
        private static (ModbusRequest Request, byte[] Response) CreateStandardSuccessExchange(
            StandardSuccessCase successCase)
        {
            return successCase switch
            {
                StandardSuccessCase.ReadHoldingRegisters => (
                    ModbusRequestFactory.CreateReadHoldingRegisters(1, 0, 1),
                    CreateReadResponse(0x1234)),
                StandardSuccessCase.WriteSingleRegister => CreateWriteSingleExchange(),
                StandardSuccessCase.WriteMultipleRegisters => (
                    ModbusRequestFactory.CreateWriteMultipleRegisters(
                        1,
                        0x0010,
                        [0x1234, 0xABCD]),
                    ModbusCrc16.Append([0x01, 0x10, 0x00, 0x10, 0x00, 0x02])),
                StandardSuccessCase.UnknownAddressQuery => (
                    ModbusRequestFactory.CreateUnknownAddressQuery(),
                    ModbusCrc16.Append([0x05, 0x03, 0x02, 0x00, 0x05])),
                _ => throw new ArgumentOutOfRangeException(nameof(successCase)),
            };
        }

        /// <summary>
        /// 创建 0x06 请求及其逐字节相同的标准回显响应。
        /// </summary>
        /// <returns>写单寄存器请求和回显帧。</returns>
        private static (ModbusRequest Request, byte[] Response) CreateWriteSingleExchange()
        {
            ModbusRequest request = ModbusRequestFactory.CreateWriteSingleRegister(
                1,
                0x0008,
                0x4567);
            return (request, request.RawFrame.ToArray());
        }

        /// <summary>
        /// 创建从缓冲头开始在 4 至 256 字节候选内均没有有效 CRC 的未知结构数据。
        /// </summary>
        /// <param name="length">需要创建的数据总长度。</param>
        /// <returns>确定性触发有界溢出的数组。</returns>
        private static byte[] CreateUnknownNonCrcData(int length)
        {
            byte[] data = Enumerable.Repeat((byte)0xA5, length).ToArray();
            data[1] = 0x44;
            int maximumCandidate = Math.Min(length, 256);

            for (int candidateLength = 4; candidateLength <= maximumCandidate; candidateLength++)
            {
                if (ModbusCrc16.IsValid(data.AsSpan(0, candidateLength)))
                {
                    data[candidateLength - 1] ^= 0x01;
                }
            }

            return data;
        }

        /// <summary>
        /// 在一秒保险时限内等待一次已接受或拒绝的执行结果。
        /// </summary>
        /// <param name="execution">事务执行任务。</param>
        /// <returns>任务完成结果。</returns>
        private static async Task<TransactionExecutionResult> AwaitExecutionAsync(
            Task<TransactionExecutionResult> execution)
        {
            return await execution.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 确定性等待接收循环已经完成指定序号数据块的事务路由和诊断处理。
        /// </summary>
        /// <param name="coordinator">发布接收处理完成序号的协调器。</param>
        /// <param name="minimumSequence">必须已经完成处理的最小接收序号。</param>
        /// <returns>目标序号已经处理后完成的任务。</returns>
        private static async Task WaitForReceiveSequenceAsync(
            ModbusTransactionCoordinator coordinator,
            long minimumSequence)
        {
            if (coordinator.LastProcessedReceiveSequence >= minimumSequence)
            {
                return;
            }

            TaskCompletionSource completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<long>? observer = null;
            observer = sequence =>
            {
                if (sequence < minimumSequence)
                {
                    return;
                }

                completionSource.TrySetResult();
            };
            coordinator.ReceiveSequenceProcessed += observer;

            try
            {
                if (coordinator.LastProcessedReceiveSequence >= minimumSequence)
                {
                    completionSource.TrySetResult();
                }

                await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            finally
            {
                coordinator.ReceiveSequenceProcessed -= observer;
            }
        }

        /// <summary>
        /// 通过状态事件确定性等待协调器进入指定状态。
        /// </summary>
        /// <param name="coordinator">待观察协调器。</param>
        /// <param name="expectedState">期望状态。</param>
        /// <returns>状态满足后完成的任务。</returns>
        private static async Task WaitForStateAsync(
            ModbusTransactionCoordinator coordinator,
            TransactionCoordinatorState expectedState)
        {
            if (coordinator.State == expectedState)
            {
                return;
            }

            TaskCompletionSource completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<TransactionCoordinatorState>? observer = null;
            observer = state =>
            {
                if (state == expectedState && coordinator.State == expectedState)
                {
                    completionSource.TrySetResult();
                }
            };
            coordinator.StateChanged += observer;

            try
            {
                if (coordinator.State == expectedState)
                {
                    completionSource.TrySetResult();
                }

                await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            finally
            {
                coordinator.StateChanged -= observer;
            }
        }

        /// <summary>
        /// 从测试输出目录向上查找解决方案根目录。
        /// </summary>
        /// <param name="startDirectory">开始搜索的测试输出目录。</param>
        /// <returns>包含解决方案文件的仓库根目录。</returns>
        private static string FindRepositoryRoot(string startDirectory)
        {
            DirectoryInfo? directory = new(startDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CH32UpperComputer.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("未找到 CH32UpperComputer.sln 所在仓库根目录。");
        }

        /// <summary>
        /// 指定非响应终止测试需要制造的事件。
        /// </summary>
        public enum NonResponseTerminalCase
        {
            /// <summary>
            /// 抽象传输写入抛出 I/O 异常。
            /// </summary>
            WriteFailure = 0,

            /// <summary>
            /// 调用方在写入已经开始后取消。
            /// </summary>
            Cancellation = 1,

            /// <summary>
            /// 接收会话以 I/O 故障断开。
            /// </summary>
            Disconnection = 2,

            /// <summary>
            /// 应用停止流程终止事务。
            /// </summary>
            ApplicationStopping = 3,
        }

        /// <summary>
        /// 指定原始调试完成边界测试场景。
        /// </summary>
        public enum RawCompletionCase
        {
            /// <summary>
            /// 已知功能结构完整到达。
            /// </summary>
            KnownStructure = 0,

            /// <summary>
            /// 未知功能在最早有效 CRC 边界完成。
            /// </summary>
            UnknownCrc = 1,

            /// <summary>
            /// 未知残片由字节间静默结束。
            /// </summary>
            InterByteSilence = 2,

            /// <summary>
            /// 非空残片由响应总截止结束。
            /// </summary>
            TotalTimeoutWithBytes = 3,

            /// <summary>
            /// 完全没有接收字节时响应总超时。
            /// </summary>
            TotalTimeoutWithoutBytes = 4,

            /// <summary>
            /// 原始捕获达到 4096 字节上限。
            /// </summary>
            CaptureOverflow = 5,
        }

        /// <summary>
        /// 指定需要端到端验证的标准成功响应签名。
        /// </summary>
        public enum StandardSuccessCase
        {
            /// <summary>
            /// 0x03 读保持寄存器及 byte-count 签名。
            /// </summary>
            ReadHoldingRegisters = 0,

            /// <summary>
            /// 0x06 写单寄存器及地址和值回显签名。
            /// </summary>
            WriteSingleRegister = 1,

            /// <summary>
            /// 0x10 写多寄存器及起始地址和数量回显签名。
            /// </summary>
            WriteMultipleRegisters = 2,

            /// <summary>
            /// 固定 0xFE 查询及真实地址和值一致签名。
            /// </summary>
            UnknownAddressQuery = 3,
        }

        /// <summary>
        /// 管理单项测试使用的手动时间、模拟传输和协调器完整生命周期。
        /// </summary>
        private sealed class CoordinatorHarness : IAsyncDisposable
        {
            /// <summary>
            /// 初始化已经打开并启动接收循环的测试组合。
            /// </summary>
            /// <param name="timeProvider">确定性时间源。</param>
            /// <param name="transport">已打开模拟传输。</param>
            /// <param name="coordinator">已启动协调器。</param>
            private CoordinatorHarness(
                ManualTimeProvider timeProvider,
                FakeSerialTransport transport,
                ModbusTransactionCoordinator coordinator)
            {
                TimeProvider = timeProvider;
                Transport = transport;
                Coordinator = coordinator;
            }

            /// <summary>
            /// 获取测试使用的确定性时间源。
            /// </summary>
            internal ManualTimeProvider TimeProvider { get; }

            /// <summary>
            /// 获取测试使用的模拟串口传输。
            /// </summary>
            internal FakeSerialTransport Transport { get; }

            /// <summary>
            /// 获取被测无队列事务协调器。
            /// </summary>
            internal ModbusTransactionCoordinator Coordinator { get; }

            /// <summary>
            /// 创建并打开模拟串口，然后只启动接收循环而不发送数据。
            /// </summary>
            /// <returns>准备接受事务的完整测试组合。</returns>
            internal static async Task<CoordinatorHarness> CreateAsync()
            {
                ManualTimeProvider timeProvider = new();
                FakeSerialTransport transport = new(timeProvider);
                await transport.OpenAsync(
                    SerialSettings.CreateDefault("COM_TEST"),
                    CancellationToken.None);
                ModbusTransactionCoordinator coordinator = new(transport, timeProvider);
                await coordinator.StartAsync(CancellationToken.None);
                return new CoordinatorHarness(timeProvider, transport, coordinator);
            }

            /// <summary>
            /// 幂等停止协调器并释放模拟串口。
            /// </summary>
            /// <returns>两个异步资源均已收敛后完成的值任务。</returns>
            public async ValueTask DisposeAsync()
            {
                await Coordinator.DisposeAsync();
                await Transport.DisposeAsync();
            }
        }
    }
}
