using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Transactions
{
    /// <summary>
    /// 验证默认关闭、纯 fixed-delay、Busy 背压和停止代次门的完整定时发送行为。
    /// </summary>
    [TestFixture]
    public sealed class PeriodicSendServiceTests
    {
        /// <summary>
        /// 验证仅连接或配置不会写串口，Start 后也必须先等待一个完整间隔。
        /// </summary>
        [Test]
        public async Task ConfigureAndStart_FirstWriteOccursOnlyAfterFullInterval()
        {
            await using PeriodicHarness harness = await PeriodicHarness.CreateAsync();
            harness.Service.Configure(CreateReadRequest(), TimeSpan.FromMilliseconds(100));
            harness.TimeProvider.Advance(TimeSpan.FromSeconds(5));
            Assert.That(harness.Transport.WrittenFrames, Is.Empty);

            harness.Service.Start();
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(99));
            Assert.That(harness.Transport.WrittenFrames, Is.Empty);
            Task waitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await waitingResponse;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Service.IsRunning, Is.True);
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
                Assert.That(harness.Service.Phase, Is.EqualTo(PeriodicSendPhase.WaitingTransaction));
            }));

            Task<TransactionExecutionResult> firstAttempt =
                WaitForAttemptAsync(harness.Service);
            await harness.Transport.InjectReceiveAsync(CreateReadResponse(0x002A));
            await firstAttempt;
            await harness.Service.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// 验证事务耗时不会形成周期 tick 或追赶，成功完成后才重新等待完整间隔。
        /// </summary>
        [Test]
        public async Task FixedDelay_LongTransaction_DoesNotAccumulateOrCatchUp()
        {
            await using PeriodicHarness harness = await PeriodicHarness.CreateAsync();
            harness.Service.Configure(
                CreateReadRequest(TimeSpan.FromSeconds(1)),
                TimeSpan.FromMilliseconds(100));
            harness.Service.Start();
            Task firstWaitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            await firstWaitingResponse;
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(500));
            Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
            Task nextInterval = WaitForNextPhaseAsync(
                harness.Service,
                PeriodicSendPhase.WaitingInterval);

            await harness.Transport.InjectReceiveAsync(CreateReadResponse(0x002A));
            await nextInterval;
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(99));
            Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
            Task secondWaitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await secondWaitingResponse;

            Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(2));
            await harness.Transport.InjectReceiveAsync(CreateReadResponse(0x002B));
            await WaitForAttemptCountAsync(harness.Service, 2);
            await harness.Service.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// 验证间隔到期遇到手动事务 Busy 时不排队，并从 Busy 结果重新等待完整间隔。
        /// </summary>
        [Test]
        public async Task FixedDelay_ManualTransactionBusy_DelaysWithoutQueue()
        {
            await using PeriodicHarness harness = await PeriodicHarness.CreateAsync();
            Task<TransactionExecutionResult> manualExecution = harness.Coordinator
                .TryExecuteAsync(
                    CreateReadRequest(TimeSpan.FromSeconds(1)),
                    CancellationToken.None)
                .AsTask();
            Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
            harness.Service.Configure(CreateReadRequest(), TimeSpan.FromMilliseconds(100));
            harness.Service.Start();
            Task<TransactionExecutionResult> busyAttempt = WaitForAttemptAsync(harness.Service);
            Task nextInterval = WaitForNextPhaseAsync(
                harness.Service,
                PeriodicSendPhase.WaitingInterval);

            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            TransactionExecutionResult busyResult = await busyAttempt;
            await nextInterval;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(busyResult.IsAccepted, Is.False);
                Assert.That(busyResult.Rejection, Is.EqualTo(TransactionRejected.Busy));
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(1));
            }));

            await harness.Transport.InjectReceiveAsync(CreateReadResponse(0x0101));
            await manualExecution.WaitAsync(TimeSpan.FromSeconds(1));
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(99));
            Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(1));
            Task periodicWaitingResponse = WaitForCoordinatorStateAsync(
                harness.Coordinator,
                TransactionCoordinatorState.WaitingResponse);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await periodicWaitingResponse;
            Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(2));

            await harness.Transport.InjectReceiveAsync(CreateReadResponse(0x0102));
            await WaitForAttemptCountAsync(harness.Service, 2);
            await harness.Service.StopAsync(CancellationToken.None);
        }

        /// <summary>
        /// 验证等待中、间隔刚结束和协调器已占用但尚未写串口三个停止边界都不会产生新写入。
        /// </summary>
        /// <param name="boundary">需要制造的停止边界。</param>
        [TestCase(PeriodicStopBoundary.WaitingInterval)]
        [TestCase(PeriodicStopBoundary.IntervalElapsed)]
        [TestCase(PeriodicStopBoundary.CoordinatorAcceptedBeforeWrite)]
        public async Task StopBoundary_InvalidatesGenerationBeforeNewWrite(
            PeriodicStopBoundary boundary)
        {
            await using PeriodicHarness harness = await PeriodicHarness.CreateAsync();
            harness.Service.Configure(CreateReadRequest(), TimeSpan.FromMilliseconds(100));
            Task? stoppedSignal = null;
            Task<TransactionExecutionResult>? attemptSignal = null;
            Action<long>? intervalObserver = null;
            Action<bool>? busyObserver = null;

            if (boundary == PeriodicStopBoundary.IntervalElapsed)
            {
                intervalObserver = _ => harness.Service.Stop();
                harness.Service.IntervalElapsed += intervalObserver;
            }
            else if (boundary == PeriodicStopBoundary.CoordinatorAcceptedBeforeWrite)
            {
                attemptSignal = WaitForAttemptAsync(harness.Service);
                busyObserver = isBusy =>
                {
                    if (isBusy)
                    {
                        harness.Service.Stop();
                    }
                };
                harness.Coordinator.BusyChanged += busyObserver;
            }

            harness.Service.Start();

            if (boundary == PeriodicStopBoundary.WaitingInterval)
            {
                await harness.Service.StopAsync(CancellationToken.None);
            }
            else
            {
                stoppedSignal = WaitForNextRunningStateAsync(harness.Service, false);
                harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
                await stoppedSignal;

                if (attemptSignal is not null)
                {
                    TransactionExecutionResult result = await attemptSignal;
                    Assert.That(
                        result.Outcome!.State,
                        Is.EqualTo(TransactionCompletionState.Cancelled));
                }

                await harness.Service.StopAsync(CancellationToken.None);
            }

            harness.Service.IntervalElapsed -= intervalObserver;
            harness.Coordinator.BusyChanged -= busyObserver;
            harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Service.IsRunning, Is.False);
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
            }));
        }

        /// <summary>
        /// 验证超时终态和间隔等待期断线都会停止调度，后续不自动重试。
        /// </summary>
        /// <param name="stopCause">需要制造的自动停止原因。</param>
        [TestCase(PeriodicAutomaticStopCause.TransactionTimedOut)]
        [TestCase(PeriodicAutomaticStopCause.TransportDisconnected)]
        public async Task TerminalFailureOrDisconnect_StopsWithoutRetry(
            PeriodicAutomaticStopCause stopCause)
        {
            await using PeriodicHarness harness = await PeriodicHarness.CreateAsync();
            harness.Service.Configure(CreateReadRequest(), TimeSpan.FromMilliseconds(100));
            harness.Service.Start();
            Task stoppedSignal = WaitForNextRunningStateAsync(harness.Service, false);

            if (stopCause == PeriodicAutomaticStopCause.TransactionTimedOut)
            {
                Task waitingResponse = WaitForCoordinatorStateAsync(
                    harness.Coordinator,
                    TransactionCoordinatorState.WaitingResponse);
                harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
                await waitingResponse;
                harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(50));
            }
            else
            {
                harness.Transport.RemoteDisconnect(new IOException("定时间隔等待期拔出"));
            }

            await stoppedSignal;
            int writeCountAfterStop = harness.Transport.WrittenFrames.Count;
            harness.TimeProvider.Advance(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Service.IsRunning, Is.False);
                Assert.That(
                    harness.Transport.WrittenFrames,
                    Has.Count.EqualTo(writeCountAfterStop));
                Assert.That(
                    writeCountAfterStop,
                    Is.EqualTo(
                        stopCause == PeriodicAutomaticStopCause.TransactionTimedOut ? 1 : 0));
            }));
        }

        /// <summary>
        /// 创建使用调用方指定响应超时的标准读事务。
        /// </summary>
        /// <param name="responseTimeout">响应总超时；为空时使用五十毫秒。</param>
        /// <returns>读取地址一、寄存器零、数量一的事务。</returns>
        private static TransactionRequest CreateReadRequest(TimeSpan? responseTimeout = null)
        {
            return TransactionRequest.CreateStandard(
                ModbusRequestFactory.CreateReadHoldingRegisters(1, 0, 1),
                responseTimeout ?? TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(20));
        }

        /// <summary>
        /// 创建一个寄存器的 CRC 正确标准读响应。
        /// </summary>
        /// <param name="value">响应寄存器原始字。</param>
        /// <returns>地址一、功能 0x03 的完整响应帧。</returns>
        private static byte[] CreateReadResponse(ushort value)
        {
            return ModbusCrc16.Append(
                [0x01, 0x03, 0x02, (byte)(value >> 8), (byte)value]);
        }

        /// <summary>
        /// 等待协调器进入指定非当前状态。
        /// </summary>
        /// <param name="coordinator">待观察协调器。</param>
        /// <param name="expectedState">期望状态。</param>
        /// <returns>状态已经进入后的任务。</returns>
        private static Task WaitForCoordinatorStateAsync(
            ModbusTransactionCoordinator coordinator,
            TransactionCoordinatorState expectedState)
        {
            if (coordinator.State == expectedState)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<TransactionCoordinatorState>? observer = null;
            observer = state =>
            {
                if (state == expectedState && coordinator.State == expectedState)
                {
                    coordinator.StateChanged -= observer;
                    completionSource.TrySetResult();
                }
            };
            coordinator.StateChanged += observer;
            return completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 等待下一次指定定时阶段事件，不使用轮询或业务时间延迟。
        /// </summary>
        /// <param name="service">待观察定时服务。</param>
        /// <param name="expectedPhase">下一次期望阶段。</param>
        /// <returns>阶段事件发生后的任务。</returns>
        private static Task WaitForNextPhaseAsync(
            PeriodicSendService service,
            PeriodicSendPhase expectedPhase)
        {
            TaskCompletionSource completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeriodicSendPhase>? observer = null;
            observer = phase =>
            {
                if (phase == expectedPhase)
                {
                    service.PhaseChanged -= observer;
                    completionSource.TrySetResult();
                }
            };
            service.PhaseChanged += observer;
            return completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 等待下一次定时尝试结果。
        /// </summary>
        /// <param name="service">待观察定时服务。</param>
        /// <returns>协调器返回的下一项尝试结果。</returns>
        private static Task<TransactionExecutionResult> WaitForAttemptAsync(
            PeriodicSendService service)
        {
            TaskCompletionSource<TransactionExecutionResult> completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<TransactionExecutionResult>? observer = null;
            observer = result =>
            {
                service.AttemptCompleted -= observer;
                completionSource.TrySetResult(result);
            };
            service.AttemptCompleted += observer;
            return completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 等待已完成定时尝试数量达到目标值。
        /// </summary>
        /// <param name="service">待观察定时服务。</param>
        /// <param name="expectedCount">期望最小完成数量。</param>
        /// <returns>数量达到目标后的任务。</returns>
        private static Task WaitForAttemptCountAsync(
            PeriodicSendService service,
            long expectedCount)
        {
            if (service.CompletedAttemptCount >= expectedCount)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<TransactionExecutionResult>? observer = null;
            observer = _ =>
            {
                if (service.CompletedAttemptCount >= expectedCount)
                {
                    service.AttemptCompleted -= observer;
                    completionSource.TrySetResult();
                }
            };
            service.AttemptCompleted += observer;

            if (service.CompletedAttemptCount >= expectedCount)
            {
                service.AttemptCompleted -= observer;
                completionSource.TrySetResult();
            }

            return completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 等待定时运行开关进入指定值。
        /// </summary>
        /// <param name="service">待观察定时服务。</param>
        /// <param name="expectedRunning">期望运行值。</param>
        /// <returns>运行开关达到目标后的任务。</returns>
        private static Task WaitForNextRunningStateAsync(
            PeriodicSendService service,
            bool expectedRunning)
        {
            TaskCompletionSource completionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<bool>? observer = null;
            observer = isRunning =>
            {
                if (isRunning == expectedRunning)
                {
                    service.RunningChanged -= observer;
                    completionSource.TrySetResult();
                }
            };
            service.RunningChanged += observer;
            return completionSource.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 指定停止竞态测试需要制造的边界。
        /// </summary>
        public enum PeriodicStopBoundary
        {
            /// <summary>
            /// 完整间隔尚未结束。
            /// </summary>
            WaitingInterval = 0,

            /// <summary>
            /// 间隔刚结束但尚未尝试协调器。
            /// </summary>
            IntervalElapsed = 1,

            /// <summary>
            /// 协调器已接受请求并发布 Busy，但物理写授权尚未通过。
            /// </summary>
            CoordinatorAcceptedBeforeWrite = 2,
        }

        /// <summary>
        /// 指定定时流程自动停止的原因。
        /// </summary>
        public enum PeriodicAutomaticStopCause
        {
            /// <summary>
            /// 已发送事务以 TimedOut 终止。
            /// </summary>
            TransactionTimedOut = 0,

            /// <summary>
            /// 完整间隔等待期间串口意外断开。
            /// </summary>
            TransportDisconnected = 1,
        }

        /// <summary>
        /// 管理定时测试使用的手动时间、模拟串口、协调器和服务生命周期。
        /// </summary>
        private sealed class PeriodicHarness : IAsyncDisposable
        {
            /// <summary>
            /// 初始化已打开并启动协调器的测试组合。
            /// </summary>
            /// <param name="timeProvider">确定性时间源。</param>
            /// <param name="transport">已打开模拟串口。</param>
            /// <param name="coordinator">已启动协调器。</param>
            /// <param name="service">默认关闭的定时服务。</param>
            private PeriodicHarness(
                ManualTimeProvider timeProvider,
                FakeSerialTransport transport,
                ModbusTransactionCoordinator coordinator,
                PeriodicSendService service)
            {
                TimeProvider = timeProvider;
                Transport = transport;
                Coordinator = coordinator;
                Service = service;
            }

            /// <summary>
            /// 获取测试确定性时间源。
            /// </summary>
            internal ManualTimeProvider TimeProvider { get; }

            /// <summary>
            /// 获取模拟串口。
            /// </summary>
            internal FakeSerialTransport Transport { get; }

            /// <summary>
            /// 获取共享无队列事务协调器。
            /// </summary>
            internal ModbusTransactionCoordinator Coordinator { get; }

            /// <summary>
            /// 获取被测定时发送服务。
            /// </summary>
            internal PeriodicSendService Service { get; }

            /// <summary>
            /// 创建使用 50 毫秒响应总超时的完整测试组合。
            /// </summary>
            /// <returns>已连接且定时默认关闭的组合。</returns>
            internal static async Task<PeriodicHarness> CreateAsync()
            {
                ManualTimeProvider timeProvider = new();
                FakeSerialTransport transport = new(timeProvider);
                SerialSettings settings = new(
                    "COM_TEST",
                    9600,
                    8,
                    System.IO.Ports.Parity.None,
                    System.IO.Ports.StopBits.One,
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(20));
                await transport.OpenAsync(settings, CancellationToken.None);
                ModbusTransactionCoordinator coordinator = new(transport, timeProvider);
                await coordinator.StartAsync(CancellationToken.None);
                PeriodicSendService service = new(coordinator, timeProvider);
                return new PeriodicHarness(timeProvider, transport, coordinator, service);
            }

            /// <summary>
            /// 停止协调器以结束任何残余事务，再释放定时服务和模拟串口。
            /// </summary>
            /// <returns>全部异步资源收敛后的值任务。</returns>
            public async ValueTask DisposeAsync()
            {
                await Coordinator.StopAsync(CancellationToken.None);
                await Service.DisposeAsync();
                await Transport.DisposeAsync();
            }
        }
    }
}
