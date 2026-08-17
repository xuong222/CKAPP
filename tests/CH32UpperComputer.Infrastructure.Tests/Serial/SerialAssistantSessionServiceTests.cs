using System.IO;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证普通串口助手独立会话的收发、缓存、暂停与定时状态机。
    /// </summary>
    [TestFixture]
    public sealed class SerialAssistantSessionServiceTests
    {
        /// <summary>
        /// 验证 115200 会话可以发送而不等待任何响应，并只统计成功写入。
        /// </summary>
        [Test]
        public async Task SendAsync_WithoutResponse_CompletesAndUpdatesStatistics()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);

            await service.SendAsync(new byte[] { 0x01, 0x02, 0x03 }, CancellationToken.None);

            SerialAssistantStatistics statistics = service.Statistics;
            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(transport.WrittenFrames, Has.Count.EqualTo(1));
                    Assert.That(transport.WrittenFrames[0].ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02, 0x03 }));
                    Assert.That(statistics.TransmitOperationCount, Is.EqualTo(1));
                    Assert.That(statistics.TransmitBytes, Is.EqualTo(3));
                }));
        }

        /// <summary>
        /// 验证小块主动上报在 50 毫秒窗口内粘合为一个显示批次。
        /// </summary>
        [Test]
        public async Task ReceiveLoop_WithFragmentedInput_CoalescesAtFiftyMilliseconds()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            List<SerialAssistantReceiveUpdate> updates = [];
            service.ReceiveUpdated += updates.Add;

            await transport.InjectReceiveAsync(new byte[] { 0xE4, 0xB8 });
            await transport.InjectReceiveAsync(new byte[] { 0xAD, 0x41 });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 4);
            timeProvider.Advance(TimeSpan.FromMilliseconds(49));
            Assert.That(updates, Is.Empty);

            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await WaitUntilAsync(() => updates.Count == 1);

            Assert.That(
                updates[0].Batch.Data.ToArray(),
                Is.EqualTo(new byte[] { 0xE4, 0xB8, 0xAD, 0x41 }));
        }

        /// <summary>
        /// 验证统计观察者在后台尚未创建延迟任务时推进时钟，也不会把合批截止点再向后推 50 毫秒。
        /// </summary>
        [Test]
        public async Task ReceiveLoop_WhenClockAdvancesDuringStatisticsEvent_PublishesAtOriginalDeadline()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            TaskCompletionSource<SerialAssistantReceiveUpdate> updateSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.StatisticsChanged += statistics =>
            {
                if (statistics.ReceiveBytes == 1)
                {
                    timeProvider.Advance(SerialAssistantSessionService.ReceiveBatchWindow);
                }
            };
            service.ReceiveUpdated += update => updateSource.TrySetResult(update);

            await transport.InjectReceiveAsync(new byte[] { 0x41 });
            SerialAssistantReceiveUpdate update = await updateSource.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            Assert.That(update.Batch.Data.ToArray(), Is.EqualTo(new byte[] { 0x41 }));
        }

        /// <summary>
        /// 验证暂停期间仍排空并计数，但不会把数据放入可重绘缓存。
        /// </summary>
        [Test]
        public async Task SetPaused_WhileReceiving_DropsPausedBytesAndResumesWithOnlyNewData()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            service.SetPaused(true);

            await transport.InjectReceiveAsync(new byte[] { 0x10, 0x11, 0x12 });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 3);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(service.CreateReceiveSnapshot(), Is.Empty);
                    Assert.That(service.Statistics.DiscardedBytes, Is.EqualTo(3));
                    Assert.That(service.Statistics.PausedDiscardedBytes, Is.EqualTo(3));
                }));

            service.SetPaused(false);
            await transport.InjectReceiveAsync(new byte[] { 0x21, 0x22 });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 5);
            timeProvider.Advance(SerialAssistantSessionService.ReceiveBatchWindow);
            await WaitUntilAsync(() => service.CreateReceiveSnapshot().Count == 1);

            Assert.That(
                service.CreateReceiveSnapshot()[0].Data.ToArray(),
                Is.EqualTo(new byte[] { 0x21, 0x22 }));
        }

        /// <summary>
        /// 验证 512 KiB 缓存满后按最旧完整批次淘汰并累计丢弃字节。
        /// </summary>
        [Test]
        public async Task ReceiveCache_WhenCapacityExceeded_EvictsOldestBatchAndCountsDiscardedBytes()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider, receiveCapacity: 512);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            byte[] block = new byte[SerialAssistantSessionService.MaximumReceiveBatchBytes];

            for (int blockIndex = 0; blockIndex < 129; blockIndex++)
            {
                block[0] = (byte)blockIndex;
                await transport.InjectReceiveAsync(block);
            }

            long expectedBytes = 129L * block.Length;
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == expectedBytes);
            IReadOnlyList<SerialAssistantReceiveBatch> snapshot = service.CreateReceiveSnapshot();

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(snapshot, Has.Count.EqualTo(128));
                    Assert.That(snapshot[0].Data.Span[0], Is.EqualTo(1));
                    Assert.That(service.Statistics.DiscardedBytes, Is.EqualTo(block.Length));
                    Assert.That(snapshot.Sum(batch => batch.Data.Length), Is.EqualTo(SerialAssistantSessionService.ReceiveCacheCapacityBytes));
                }));
        }

        /// <summary>
        /// 验证定时发送冻结启动负载、首发等待完整间隔且一次写完后才安排下一周期。
        /// </summary>
        [Test]
        public async Task PeriodicSend_FreezesPayloadDelaysFirstSendAndNeverOverlapsWrites()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            byte[] payload = [0x31];
            FakeWritePause firstCompletion = transport.PauseNextWriteCompletion();
            service.StartPeriodicSending(payload, TimeSpan.FromSeconds(1));
            payload[0] = 0xFF;

            timeProvider.Advance(TimeSpan.FromMilliseconds(999));
            Assert.That(transport.WrittenFrames, Is.Empty);

            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            await firstCompletion.Entered.WaitAsync(TimeSpan.FromSeconds(1));
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            Assert.That(transport.WrittenFrames, Has.Count.EqualTo(1));
            Assert.That(transport.WrittenFrames[0].Span[0], Is.EqualTo(0x31));

            firstCompletion.Release();
            await WaitUntilAsync(() => service.Statistics.TransmitOperationCount == 1);
            await Task.Yield();
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => transport.WrittenFrames.Count == 2);

            await service.StopPeriodicSendingAsync();
            timeProvider.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
            Assert.That(transport.WrittenFrames, Has.Count.EqualTo(2));
        }

        /// <summary>
        /// 验证接收循环中的拔线故障会结束会话并发布可读中文错误。
        /// </summary>
        [Test]
        public async Task ReceiveLoop_WhenDeviceDisconnects_PublishesFaultedState()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            List<SerialAssistantSessionStateChange> states = [];
            service.StateChanged += states.Add;
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);

            transport.RemoteDisconnect(new IOException("device removed"));
            await WaitUntilAsync(() => service.State == SerialAssistantSessionState.Faulted);

            Assert.That(states[^1].Message, Does.Contain("设备").Or.Contain("串口"));
        }

        /// <summary>
        /// 验证拔线故障收敛后可以直接重新连接，并且新会话只消费自己的接收数据。
        /// </summary>
        [Test]
        public async Task ConnectAsync_AfterDeviceFault_StartsFreshReceiveSession()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            SerialLineSettings settings =
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT");
            await service.ConnectAsync(settings, CancellationToken.None);
            int firstGeneration = transport.PortGeneration;

            transport.RemoteDisconnect(new IOException("device removed"));
            await WaitUntilAsync(() => service.State == SerialAssistantSessionState.Faulted);
            await service.ConnectAsync(settings, CancellationToken.None);
            await transport.InjectReceiveAsync(new byte[] { 0x5A });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 1);
            timeProvider.Advance(SerialAssistantSessionService.ReceiveBatchWindow);
            await WaitUntilAsync(() => service.CreateReceiveSnapshot().Count == 1);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(service.State, Is.EqualTo(SerialAssistantSessionState.Connected));
                    Assert.That(transport.PortGeneration, Is.GreaterThan(firstGeneration));
                    Assert.That(
                        service.CreateReceiveSnapshot()[0].Data.ToArray(),
                        Is.EqualTo(new byte[] { 0x5A }));
                }));
        }

        /// <summary>
        /// 验证底层写入异常不会被误记为成功发送次数或字节。
        /// </summary>
        [Test]
        public async Task SendAsync_WhenWriteFails_DoesNotUpdateTransmitStatistics()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            transport.FailNextWrite(new IOException("removed"));

            Func<Task> failingSend = async () => await service.SendAsync(
                    new byte[] { 0x01, 0x02 },
                    CancellationToken.None);
            Assert.ThrowsAsync<IOException>(failingSend);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(service.Statistics.TransmitOperationCount, Is.Zero);
                    Assert.That(service.Statistics.TransmitBytes, Is.Zero);
                    Assert.That(transport.WrittenFrames, Is.Empty);
                }));
        }

        /// <summary>
        /// 验证断开会等待已经进入驱动的单次写入返回，再关闭串口和接收循环。
        /// </summary>
        [Test]
        public async Task DisconnectAsync_WithManualWriteInProgress_WaitsBeforeClosingTransport()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            FakeWritePause completionPause = transport.PauseNextWriteCompletion();
            Task sendTask = service.SendAsync(
                new byte[] { 0x31 },
                CancellationToken.None);
            await completionPause.Entered.WaitAsync(TimeSpan.FromSeconds(2));

            Task disconnectTask = service.DisconnectAsync(CancellationToken.None);
            await Task.Delay(20);
            Assert.That(disconnectTask.IsCompleted, Is.False);
            Assert.That(transport.IsOpen, Is.True);

            completionPause.Release();
            await sendTask;
            await disconnectTask;

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(service.State, Is.EqualTo(SerialAssistantSessionState.Disconnected));
                    Assert.That(transport.IsOpen, Is.False);
                    Assert.That(service.Statistics.TransmitOperationCount, Is.EqualTo(1));
                }));
        }

        /// <summary>
        /// 验证清空操作同时移除原始缓存并归零全部收发和丢弃计数。
        /// </summary>
        [Test]
        public async Task ClearReceiveData_AfterTrafficAndPause_ResetsCacheAndAllStatistics()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);
            await service.SendAsync(new byte[] { 0x01 }, CancellationToken.None);
            await transport.InjectReceiveAsync(new byte[] { 0x02 });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 1);
            timeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await WaitUntilAsync(() => service.CreateReceiveSnapshot().Count == 1);
            service.SetPaused(true);
            await transport.InjectReceiveAsync(new byte[] { 0x03, 0x04 });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 3);

            service.ClearReceiveData();

            SerialAssistantStatistics statistics = service.Statistics;
            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(service.CreateReceiveSnapshot(), Is.Empty);
                    Assert.That(statistics.TransmitOperationCount, Is.Zero);
                    Assert.That(statistics.TransmitBytes, Is.Zero);
                    Assert.That(statistics.ReceiveBytes, Is.Zero);
                    Assert.That(statistics.DiscardedBytes, Is.Zero);
                    Assert.That(statistics.PausedDiscardedBytes, Is.Zero);
                }));
        }

        /// <summary>
        /// 验证清空半批数据后，新数据会获得独立的完整 50 毫秒窗口，不沿用旧批次的剩余截止点。
        /// </summary>
        [Test]
        public async Task ClearReceiveData_WithPendingBatch_RestartsWindowForFollowingData()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            await using SerialAssistantSessionService service = new(transport, timeProvider);
            await service.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);

            await transport.InjectReceiveAsync(new byte[] { 0x10 });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 1);
            timeProvider.Advance(TimeSpan.FromMilliseconds(25));
            service.ClearReceiveData();

            await transport.InjectReceiveAsync(new byte[] { 0x20 });
            await WaitUntilAsync(() => service.Statistics.ReceiveBytes == 1);
            timeProvider.Advance(TimeSpan.FromMilliseconds(25));
            Assert.That(service.CreateReceiveSnapshot(), Is.Empty);

            timeProvider.Advance(TimeSpan.FromMilliseconds(25));
            await WaitUntilAsync(() => service.CreateReceiveSnapshot().Count == 1);

            Assert.That(
                service.CreateReceiveSnapshot()[0].Data.ToArray(),
                Is.EqualTo(new byte[] { 0x20 }));
        }

        /// <summary>
        /// 提供足以覆盖后台创建 50 毫秒计时器调度边界的确定性推进量。
        /// </summary>
        private static readonly TimeSpan ReceiveWindowWithSchedulingMargin =
            SerialAssistantSessionService.ReceiveBatchWindow + TimeSpan.FromMilliseconds(1);

        /// <summary>
        /// 在短超时内等待后台状态达到条件，使测试不依赖固定真实延迟。
        /// </summary>
        /// <param name="condition">返回真时结束等待的线程安全条件。</param>
        /// <returns>条件满足后的任务。</returns>
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

                while (!condition())
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await Task.Delay(1, timeout.Token);
                }
        }
    }
}
