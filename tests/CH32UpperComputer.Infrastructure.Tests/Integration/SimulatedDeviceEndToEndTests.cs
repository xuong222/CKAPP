using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Core.Registers;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Integration
{
    /// <summary>
    /// 验证请求构建、无队列协调、RTU 组帧、严格解析和模拟设备状态组成的完整通信链路。
    /// </summary>
    [TestFixture]
    public sealed class SimulatedDeviceEndToEndTests
    {
        /// <summary>
        /// 验证连接保持静默，0x03 能形成有效快照，0x06 与 0x10 写入后能够由后续读取确认。
        /// </summary>
        [Test]
        public async Task NormalWorkflow_ConnectReadWriteAndReadback_CompletesEndToEnd()
        {
            await using IntegrationCoordinatorHarness harness =
                await IntegrationCoordinatorHarness.CreateAsync();
            DeviceSnapshot snapshot = new();
            harness.Device.SetRegister(0x0002, 2534);
            harness.Device.SetRegister(0x0003, 120);
            harness.Device.SetRegister(0x0004, 35);
            harness.Device.SetRegister(0x0005, 87);
            harness.Device.SetRegister(0x0006, 880);
            harness.Device.SetRegister(0x0007, 12);
            harness.Device.SetRegister(0x0008, 1);
            harness.Device.SetRegister(0x0009, 0x0004);

            Assert.That(harness.Transport.WrittenFrames, Is.Empty);

            ModbusRequest monitorRead = ModbusRequestFactory.CreateReadHoldingRegisters(
                1,
                0x0002,
                8);
            TransactionExecutionResult monitorResult = await harness.ExecuteWithDeviceAsync(
                monitorRead,
                SimulatedModbusBehavior.Normal);
            SnapshotApplyResult monitorApply = snapshot.Apply(
                monitorRead,
                monitorResult.Outcome!.Response!,
                harness.TimeProvider.GetUtcNow());
            TransactionExecutionResult singleWrite = await harness.ExecuteWithDeviceAsync(
                ModbusRequestFactory.CreateWriteSingleRegister(1, 0x000A, 321),
                SimulatedModbusBehavior.Normal);
            ModbusRequest singleReadRequest = ModbusRequestFactory.CreateReadHoldingRegisters(
                1,
                0x000A,
                1);
            TransactionExecutionResult singleRead = await harness.ExecuteWithDeviceAsync(
                singleReadRequest,
                SimulatedModbusBehavior.Normal);
            ushort[] multipleValues = [10, 20, 30];
            TransactionExecutionResult multipleWrite = await harness.ExecuteWithDeviceAsync(
                ModbusRequestFactory.CreateWriteMultipleRegisters(1, 0x001D, multipleValues),
                SimulatedModbusBehavior.Normal);
            TransactionExecutionResult multipleRead = await harness.ExecuteWithDeviceAsync(
                ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x001D, 3),
                SimulatedModbusBehavior.Normal);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(monitorResult.Outcome.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(monitorApply.IsSuccess, Is.True);
                Assert.That(monitorApply.UpdatedRegisterCount, Is.EqualTo(8));
                Assert.That(snapshot.GetByProtocolAddress(0x0002).FormattedValue, Is.EqualTo("25.34"));
                Assert.That(snapshot.GetByProtocolAddress(0x0006).FormattedValue, Is.EqualTo("880"));
                Assert.That(singleWrite.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(singleRead.Outcome!.Response!.Registers.Span[0], Is.EqualTo(321));
                Assert.That(multipleWrite.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(multipleRead.Outcome!.Response!.Registers.ToArray(), Is.EqualTo(multipleValues));
                Assert.That(harness.Transport.WrittenFrames, Has.Count.EqualTo(5));
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(5));
                Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
            }));
        }

        /// <summary>
        /// 验证 Modbus 允许的最长 0x03 响应和最长 0x10 请求均以 255 字节完成严格往返。
        /// </summary>
        [Test]
        public async Task MaximumLegalFrames_Read125AndWrite123_RoundTripAt255Bytes()
        {
            await using IntegrationCoordinatorHarness harness =
                await IntegrationCoordinatorHarness.CreateAsync();
            const ushort readStartAddress = 0x0100;

            for (ushort index = 0; index < 125; index++)
            {
                harness.Device.SetRegister(
                    checked((ushort)(readStartAddress + index)),
                    checked((ushort)(0x1000 + index)));
            }

            ModbusRequest maximumRead = ModbusRequestFactory.CreateReadHoldingRegisters(
                1,
                readStartAddress,
                125);
            TransactionExecutionResult readResult = await harness.ExecuteWithDeviceAsync(
                maximumRead,
                SimulatedModbusBehavior.Normal);
            ushort[] maximumWriteValues = Enumerable.Range(0, 123)
                .Select(index => checked((ushort)(0x2000 + index)))
                .ToArray();
            ModbusRequest maximumWrite = ModbusRequestFactory.CreateWriteMultipleRegisters(
                1,
                0x0200,
                maximumWriteValues);
            ModbusRequestParseResult parsedWrite = ModbusRequestParser.TryParseSupported(
                maximumWrite.RawFrame.Span);
            TransactionExecutionResult writeResult = await harness.ExecuteWithDeviceAsync(
                maximumWrite,
                SimulatedModbusBehavior.Normal);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(readResult.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(readResult.Outcome.RawData.Length, Is.EqualTo(255));
                Assert.That(readResult.Outcome.Response!.Registers.Length, Is.EqualTo(125));
                Assert.That(readResult.Outcome.Response.Registers.Span[0], Is.EqualTo(0x1000));
                Assert.That(readResult.Outcome.Response.Registers.Span[^1], Is.EqualTo(0x107C));
                Assert.That(maximumWrite.RawFrame.Length, Is.EqualTo(255));
                Assert.That(parsedWrite.Status, Is.EqualTo(ModbusRequestParseStatus.Succeeded));
                Assert.That(parsedWrite.Request!.Quantity, Is.EqualTo(123));
                Assert.That(writeResult.Outcome!.State, Is.EqualTo(TransactionCompletionState.Succeeded));
                Assert.That(writeResult.Outcome.RawData.Length, Is.EqualTo(8));
                Assert.That(harness.Device.GetRegister(0x0200), Is.EqualTo(0x2000));
                Assert.That(harness.Device.GetRegister(0x023D), Is.EqualTo(0x203D));
                Assert.That(harness.Device.GetRegister(0x027A), Is.EqualTo(0x207A));
            }));
        }

        /// <summary>
        /// 验证模拟设备故障矩阵产生明确终态，并且只有成功读响应能够进入设备快照。
        /// </summary>
        /// <param name="behavior">本轮模拟设备需要制造的线路行为。</param>
        /// <param name="expectedState">协调器预期得到的唯一事务终态。</param>
        /// <param name="shouldUpdateSnapshot">该响应是否应当通过快照更新门。</param>
        [TestCase(SimulatedModbusBehavior.Delayed, TransactionCompletionState.Succeeded, true)]
        [TestCase(SimulatedModbusBehavior.CrcError, TransactionCompletionState.TimedOut, false)]
        [TestCase(SimulatedModbusBehavior.ModbusException, TransactionCompletionState.ModbusException, false)]
        [TestCase(SimulatedModbusBehavior.Segmented, TransactionCompletionState.Succeeded, true)]
        [TestCase(SimulatedModbusBehavior.StickyFrames, TransactionCompletionState.Succeeded, true)]
        [TestCase(SimulatedModbusBehavior.NoResponse, TransactionCompletionState.TimedOut, false)]
        [TestCase(SimulatedModbusBehavior.Disconnect, TransactionCompletionState.Disconnected, false)]
        public async Task ScriptedFaultMatrix_ProducesExpectedTerminalStateAndSnapshotGate(
            SimulatedModbusBehavior behavior,
            TransactionCompletionState expectedState,
            bool shouldUpdateSnapshot)
        {
            await using IntegrationCoordinatorHarness harness =
                await IntegrationCoordinatorHarness.CreateAsync();
            DeviceSnapshot snapshot = new();
            harness.Device.SetRegister(0x0002, 2534);
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);

            TransactionExecutionResult result = await harness.ExecuteWithDeviceAsync(request, behavior);
            SnapshotApplyResult? applyResult = result.Outcome!.Response?.IsSuccess == true
                ? snapshot.Apply(
                    request,
                    result.Outcome.Response,
                    harness.TimeProvider.GetUtcNow())
                : null;

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Outcome.State, Is.EqualTo(expectedState));
                Assert.That(snapshot.Values.ContainsKey(0x0002), Is.EqualTo(shouldUpdateSnapshot));
                Assert.That(applyResult?.IsSuccess ?? false, Is.EqualTo(shouldUpdateSnapshot));
                Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
                Assert.That(harness.Coordinator.LastTransactionId, Is.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证标准接收缓存达到上限后事务以 ReceiveOverflow 终止，非法数据不会进入快照。
        /// </summary>
        [Test]
        public async Task ReceiveOverflow_EndsTransactionWithoutUpdatingSnapshot()
        {
            await using IntegrationCoordinatorHarness harness =
                await IntegrationCoordinatorHarness.CreateAsync();
            DeviceSnapshot snapshot = new();
            ModbusRequest request = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            (Task<TransactionExecutionResult> execution, _) = await harness.BeginStandardAsync(request);

            await harness.Transport.InjectReceiveAsync(CreateUnknownNonCrcData(4096));
            TransactionExecutionResult result = await execution.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(result.Outcome!.State, Is.EqualTo(TransactionCompletionState.ReceiveOverflow));
                Assert.That(snapshot.Values, Is.Empty);
                Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
                Assert.That(
                    harness.Coordinator.State,
                    Is.EqualTo(TransactionCoordinatorState.Resynchronizing));
            }));
        }

        /// <summary>
        /// 创建达到接收上限且所有可能短前缀均不携带有效 CRC 的确定性噪声。
        /// </summary>
        /// <param name="length">需要创建的噪声字节数。</param>
        /// <returns>不会被误识别为短 CRC 帧的新字节数组。</returns>
        internal static byte[] CreateUnknownNonCrcData(int length)
        {
            if (length < 4)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            byte[] data = Enumerable.Repeat((byte)0xA5, length).ToArray();
            data[1] = 0x44;
            int maximumCandidate = Math.Min(length, TransactionRequest.MaximumRequestFrameBytes);

            for (int candidateLength = 4; candidateLength <= maximumCandidate; candidateLength++)
            {
                if (ModbusCrc16.IsValid(data.AsSpan(0, candidateLength)))
                {
                    data[candidateLength - 1] ^= 0x01;
                }
            }

            return data;
        }
    }

    /// <summary>
    /// 管理集成测试使用的手动时间、模拟串口、事务协调器和模拟 Modbus 设备完整生命周期。
    /// </summary>
    internal sealed class IntegrationCoordinatorHarness : IAsyncDisposable
    {
        /// <summary>
        /// 初始化已经打开、启动接收循环且尚未发送任何请求的测试组合。
        /// </summary>
        /// <param name="timeProvider">确定性推进通信截止时间的时间源。</param>
        /// <param name="transport">记录写帧并允许注入接收块的模拟串口。</param>
        /// <param name="coordinator">只允许一个活动事务的协调器。</param>
        /// <param name="device">根据写帧生成可脚本化响应的模拟设备。</param>
        private IntegrationCoordinatorHarness(
            ManualTimeProvider timeProvider,
            FakeSerialTransport transport,
            ModbusTransactionCoordinator coordinator,
            SimulatedModbusDevice device)
        {
            TimeProvider = timeProvider;
            Transport = transport;
            Coordinator = coordinator;
            Device = device;
        }

        /// <summary>
        /// 获取测试使用的确定性统一时间源。
        /// </summary>
        internal ManualTimeProvider TimeProvider { get; }

        /// <summary>
        /// 获取有界、可暂停且记录完整写帧的模拟串口。
        /// </summary>
        internal FakeSerialTransport Transport { get; }

        /// <summary>
        /// 获取被测无队列事务协调器。
        /// </summary>
        internal ModbusTransactionCoordinator Coordinator { get; }

        /// <summary>
        /// 获取与模拟串口相连的可脚本化 Modbus 设备。
        /// </summary>
        internal SimulatedModbusDevice Device { get; }

        /// <summary>
        /// 创建并打开模拟串口，只启动接收循环而不产生任何线路写入。
        /// </summary>
        /// <returns>可直接提交标准事务的新测试组合。</returns>
        internal static async Task<IntegrationCoordinatorHarness> CreateAsync()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            ModbusTransactionCoordinator coordinator = new(transport, timeProvider);
            await coordinator.StartAsync(CancellationToken.None);
            SimulatedModbusDevice device = new(transport, timeProvider)
            {
                ResponseDelay = TimeSpan.FromMilliseconds(20),
            };
            return new IntegrationCoordinatorHarness(
                timeProvider,
                transport,
                coordinator,
                device);
        }

        /// <summary>
        /// 启动一项标准事务并等待物理写入完成、协调器进入等待响应状态。
        /// </summary>
        /// <param name="request">需要发送且等待严格签名响应的结构化请求。</param>
        /// <param name="responseTimeout">可选总响应超时；默认使用五十毫秒受控时间。</param>
        /// <param name="cancellationToken">可与响应、溢出或断开竞争唯一完成门的调用方取消令牌。</param>
        /// <returns>尚待终态的执行任务和本次实际写入的完整帧。</returns>
        internal async Task<(
            Task<TransactionExecutionResult> Execution,
            ReadOnlyMemory<byte> WrittenFrame)> BeginStandardAsync(
                ModbusRequest request,
                TimeSpan? responseTimeout = null,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            int expectedWriteCount = checked(Transport.WrittenFrames.Count + 1);
            TransactionRequest transaction = TransactionRequest.CreateStandard(
                request,
                responseTimeout ?? TimeSpan.FromMilliseconds(50));
            Task<TransactionExecutionResult> execution = Coordinator
                .TryExecuteAsync(transaction, cancellationToken)
                .AsTask();
            await WaitUntilAsync(
                () => Transport.WrittenFrames.Count >= expectedWriteCount,
                "等待模拟串口记录请求帧超时。");
            await WaitForStateAsync(TransactionCoordinatorState.WaitingResponse);
            return (execution, Transport.WrittenFrames[expectedWriteCount - 1]);
        }

        /// <summary>
        /// 通过模拟设备完整执行一项标准事务，并按脚本行为推进受控超时。
        /// </summary>
        /// <param name="request">需要发送和严格匹配的结构化 Modbus 请求。</param>
        /// <param name="behavior">模拟设备本次制造的响应或故障行为。</param>
        /// <param name="responseTimeout">可选总响应超时；默认五十毫秒。</param>
        /// <returns>协调器唯一终态对应的执行结果。</returns>
        internal async Task<TransactionExecutionResult> ExecuteWithDeviceAsync(
            ModbusRequest request,
            SimulatedModbusBehavior behavior,
            TimeSpan? responseTimeout = null)
        {
            TimeSpan timeout = responseTimeout ?? TimeSpan.FromMilliseconds(50);
            long sequenceBeforeResponse = Coordinator.LastProcessedReceiveSequence;
            (Task<TransactionExecutionResult> execution, ReadOnlyMemory<byte> writtenFrame) =
                await BeginStandardAsync(request, timeout);
            ValueTask deviceHandling = Device.HandleWriteAsync(
                writtenFrame,
                behavior,
                CancellationToken.None);

            if (behavior == SimulatedModbusBehavior.Delayed)
            {
                TimeProvider.Advance(Device.ResponseDelay);
            }

            await deviceHandling.ConfigureAwait(false);

            if (behavior == SimulatedModbusBehavior.CrcError)
            {
                await WaitForReceiveSequenceAsync(sequenceBeforeResponse + 1);
                TimeProvider.Advance(timeout);
            }
            else if (behavior == SimulatedModbusBehavior.NoResponse)
            {
                TimeProvider.Advance(timeout);
            }

            return await execution.WaitAsync(TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// 等待协调器处理至少指定序号的接收块。
        /// </summary>
        /// <param name="minimumSequence">必须完成路由的最小接收序号。</param>
        /// <returns>目标序号完成后的任务。</returns>
        internal async Task WaitForReceiveSequenceAsync(long minimumSequence)
        {
            await WaitUntilAsync(
                () => Coordinator.LastProcessedReceiveSequence >= minimumSequence,
                "等待协调器处理接收块超时。");
        }

        /// <summary>
        /// 等待协调器进入指定生命周期或事务状态。
        /// </summary>
        /// <param name="expectedState">必须观察到的协调器状态。</param>
        /// <returns>状态满足后的任务。</returns>
        internal async Task WaitForStateAsync(TransactionCoordinatorState expectedState)
        {
            await WaitUntilAsync(
                () => Coordinator.State == expectedState,
                $"等待协调器进入 {expectedState} 状态超时。");
        }

        /// <summary>
        /// 在一秒保险时限内只通过让出调度器等待内存条件成立。
        /// </summary>
        /// <param name="condition">无副作用且成功时返回真的内存条件。</param>
        /// <param name="timeoutMessage">保险时限到达时用于定位场景的错误说明。</param>
        /// <returns>条件成立后的任务。</returns>
        internal static async Task WaitUntilAsync(
            Func<bool> condition,
            string timeoutMessage)
        {
            ArgumentNullException.ThrowIfNull(condition);
            ArgumentException.ThrowIfNullOrWhiteSpace(timeoutMessage);
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);

            while (!condition())
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException(timeoutMessage);
                }

                await Task.Yield();
            }
        }

        /// <summary>
        /// 停止协调器、关闭会话并永久释放模拟传输。
        /// </summary>
        /// <returns>全部后台读取和资源生命周期收敛后的值任务。</returns>
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }
}
