using System.IO.Ports;
using System.IO;
using System.Text;
using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证串口助手界面的严格编码、跨批次显示、命令状态和文本保存行为。
    /// </summary>
    [TestFixture]
    public sealed class SerialAssistantViewModelTests
    {
        /// <summary>
        /// 为后台登记 50 毫秒合批计时器预留一毫秒调度裕量，避免虚拟时钟推进竞态。
        /// </summary>
        private static readonly TimeSpan ReceiveWindowWithSchedulingMargin =
            SerialAssistantSessionService.ReceiveBatchWindow + TimeSpan.FromMilliseconds(1);

        /// <summary>
        /// 验证 HEX 输入不会追加 CRC，且“发送新行”只追加 CRLF 字节。
        /// </summary>
        [Test]
        public void TryBuildPayload_WithHexAndNewLine_AppendsOnlyCrLf()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            harness.ViewModel.SendMode = SerialAssistantDataMode.Hex;
            harness.ViewModel.AppendNewLine = true;
            harness.ViewModel.SendText = "01 A3";

            bool succeeded = harness.ViewModel.TryBuildPayload(
                out ReadOnlyMemory<byte> payload,
                out string errorMessage);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(succeeded, Is.True);
                    Assert.That(errorMessage, Is.Empty);
                    Assert.That(payload.ToArray(), Is.EqualTo(new byte[] { 0x01, 0xA3, 0x0D, 0x0A }));
                }));
        }

        /// <summary>
        /// 验证 UTF-8 发送采用严格编码，孤立代理项不会静默变为替代字符发出。
        /// </summary>
        [Test]
        public void TryBuildPayload_WithInvalidUtf16Input_ReturnsChineseError()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            harness.ViewModel.SendMode = SerialAssistantDataMode.Utf8;
            harness.ViewModel.SendText = "\uD800";

            bool succeeded = harness.ViewModel.TryBuildPayload(
                out ReadOnlyMemory<byte> payload,
                out string errorMessage);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(succeeded, Is.False);
                    Assert.That(payload.IsEmpty, Is.True);
                    Assert.That(errorMessage, Does.Contain("UTF-8"));
                }));
        }

        /// <summary>
        /// 验证 UTF-8 字符跨两个 50 毫秒显示批次时仍由同一 Decoder 正确还原。
        /// </summary>
        [Test]
        public async Task ReceiveView_WithUtf8AcrossBatches_RendersCompleteCharacter()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();

            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0xE4, 0xB8 });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(() => harness.Service.CreateReceiveSnapshot().Count == 1);
            Assert.That(harness.ViewModel.ReceiveText, Is.Empty);

            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0xAD });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText == "RX\r\n     中\r\n\r\n");

            Assert.That(harness.ViewModel.ReceiveText, Is.EqualTo("RX\r\n     中\r\n\r\n"));
        }

        /// <summary>
        /// 验证切换到 HEX 模式会从原始缓存完整重绘，而不是转换当前文本。
        /// </summary>
        [Test]
        public async Task ReceiveMode_WhenChangedToHex_RerendersRawCache()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0xE4, 0xB8, 0xAD });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText == "RX\r\n     中\r\n\r\n");

            harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Hex;

            Assert.That(
                harness.ViewModel.ReceiveText,
                Is.EqualTo("RX\r\n     E4 B8 AD\r\n\r\n"));
        }

        /// <summary>
        /// 验证清空视图后才执行的旧接收界面事件不会把清空前字节重新追加回来。
        /// </summary>
        [Test]
        public async Task ClearCommand_WithQueuedOldReceiveUpdate_DoesNotRestoreClearedData()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport transport = new(timeProvider);
            SerialAssistantSessionService service = new(transport, timeProvider);
            QueuedUiDispatcher dispatcher = new();
            SerialAssistantViewModel viewModel = new(
                service,
                new FakeSerialPortDiscovery([]),
                dispatcher,
                new SerialAssistantPreferences());

            try
            {
                await service.ConnectAsync(
                    SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                    CancellationToken.None);
                dispatcher.RunAll();
                long previousRevision = service.Statistics.StatisticsRevision;
                TaskCompletionSource receiveProcessed = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                service.StatisticsChanged += statistics =>
                {
                    if (statistics.StatisticsRevision > previousRevision)
                    {
                        receiveProcessed.TrySetResult();
                    }
                };
                await transport.InjectReceiveAsync(new byte[] { 0x41 });
                await receiveProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                timeProvider.Advance(ReceiveWindowWithSchedulingMargin);
                await WaitUntilAsync(() => service.CreateReceiveSnapshot().Count == 1);

                viewModel.ClearCommand.Execute(null);
                dispatcher.RunAll();

                Assert.That(viewModel.ReceiveText, Is.Empty);
                Assert.That(viewModel.ReceiveBytes, Is.Zero);
            }
            finally
            {
                viewModel.Dispose();
                await service.DisposeAsync();
            }
        }

        /// <summary>
        /// 验证发送成功和随后收到的数据会按发生顺序同时出现在统一串口画布中。
        /// </summary>
        [Test]
        public async Task TrafficCanvas_AfterManualSendAndReceive_ShowsTxAndRx()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            harness.ViewModel.SendMode = SerialAssistantDataMode.Utf8;
            harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Utf8;
            harness.ViewModel.SendText = "ping";

            await harness.ViewModel.SendCommand.ExecuteAsync(null);
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText.Contains(
                    "TX\r\n     ping\r\n\r\n",
                    StringComparison.Ordinal));
            await harness.InjectReceiveAndWaitForStatisticsAsync(
                Encoding.UTF8.GetBytes("pong"));
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText.Contains(
                    "RX\r\n     pong\r\n\r\n",
                    StringComparison.Ordinal));

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(
                        harness.ViewModel.ReceiveText,
                        Is.EqualTo("TX\r\n     ping\r\n\r\nRX\r\n     pong\r\n\r\n"));
                    Assert.That(harness.ViewModel.TrafficRecords, Has.Count.EqualTo(2));
                    Assert.That(harness.ViewModel.TrafficRecords[0].HeaderText, Is.EqualTo("TX"));
                    Assert.That(harness.ViewModel.TrafficRecords[0].IsTransmit, Is.True);
                    Assert.That(harness.ViewModel.TrafficRecords[1].HeaderText, Is.EqualTo("RX"));
                    Assert.That(harness.ViewModel.TrafficRecords[1].IsTransmit, Is.False);
                }));
        }

        /// <summary>
        /// 验证开启时间戳后使用秒级标题行，并把 TX 数据放在下一条缩进正文行中。
        /// </summary>
        [Test]
        public async Task TrafficCanvas_WithTimestamp_UsesTwoLineMessageLayout()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            harness.ViewModel.ShowTimestamps = true;
            harness.ViewModel.SendText = "1411";

            await harness.ViewModel.SendCommand.ExecuteAsync(null);
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText.Contains(
                    "TX",
                    StringComparison.Ordinal));

            Assert.That(
                harness.ViewModel.ReceiveText,
                Does.Match("^\\[\\d{2}:\\d{2}:\\d{2}\\]  TX\\r\\n     1411\\r\\n\\r\\n$"));
        }

        /// <summary>
        /// 验证切换统一画布到 HEX 模式时，会从原始 TX/RX 快照完整重绘且不丢失发送记录。
        /// </summary>
        [Test]
        public async Task TrafficCanvas_WhenDisplayModeChanges_PreservesTxAndRxHistory()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            harness.ViewModel.SendText = "A";
            await harness.ViewModel.SendCommand.ExecuteAsync(null);
            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0x42 });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText ==
                    "TX\r\n     A\r\n\r\nRX\r\n     B\r\n\r\n");

            harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Hex;

            Assert.That(
                harness.ViewModel.ReceiveText,
                Is.EqualTo("TX\r\n     41\r\n\r\nRX\r\n     42\r\n\r\n"));
        }

        /// <summary>
        /// 验证定时循环每次真实写入成功后也会把冻结负载追加为一项 TX 画布记录。
        /// </summary>
        [Test]
        public async Task TrafficCanvas_AfterPeriodicWrite_ShowsTxRecord()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            harness.ViewModel.SendText = "tick";
            harness.ViewModel.PeriodicIntervalMilliseconds = 1000;
            harness.ViewModel.StartPeriodicCommand.Execute(null);

            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(1001));
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText == "TX\r\n     tick\r\n\r\n");
            await harness.ViewModel.StopPeriodicCommand.ExecuteAsync(null);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(
                        harness.ViewModel.ReceiveText,
                        Is.EqualTo("TX\r\n     tick\r\n\r\n"));
                    Assert.That(harness.ViewModel.TransmitOperationCount, Is.EqualTo(1));
                    Assert.That(harness.ViewModel.TransmitBytes, Is.EqualTo(4));
                }));
        }

        /// <summary>
        /// 在短超时内等待独立服务状态满足条件，避免固定真实延迟。
        /// </summary>
        /// <param name="condition">成功时返回真的线程安全条件。</param>
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

        /// <summary>
        /// 验证定时发送运行期间手动发送和负载编辑均被锁定，停止后恢复。
        /// </summary>
        [Test]
        public async Task PeriodicCommands_WhileRunning_LockManualSendAndPayloadEditing()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            harness.ViewModel.SendText = "ping";
            harness.ViewModel.PeriodicIntervalMilliseconds = 1000;

            harness.ViewModel.StartPeriodicCommand.Execute(null);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(harness.ViewModel.IsPeriodicSending, Is.True);
                    Assert.That(harness.ViewModel.CanEditPayload, Is.False);
                    Assert.That(harness.ViewModel.SendCommand.CanExecute(null), Is.False);
                }));

            await harness.ViewModel.StopPeriodicCommand.ExecuteAsync(null);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(harness.ViewModel.IsPeriodicSending, Is.False);
                    Assert.That(harness.ViewModel.CanEditPayload, Is.True);
                    Assert.That(harness.ViewModel.SendCommand.CanExecute(null), Is.True);
                }));
        }

        /// <summary>
        /// 验证保存内容就是当前视图，并明确写入 UTF-8 BOM。
        /// </summary>
        [Test]
        public async Task SaveCurrentViewAsync_WritesExactViewAsUtf8WithBom()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            string directory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"assistant-save-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string filePath = Path.Combine(directory, "receive.txt");

            try
            {
                await harness.ConnectAsync();
                harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Hex;
                await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0x41, 0x0D, 0x0A });
                harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
                await harness.WaitUntilAsync(
                    () => harness.ViewModel.ReceiveText ==
                        "RX\r\n     41 0D 0A\r\n\r\n");

                await harness.ViewModel.SaveCurrentViewAsync(filePath, CancellationToken.None);

                byte[] bytes = await File.ReadAllBytesAsync(filePath);
                string content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                Assert.Multiple(
                    (Action)(() =>
                    {
                        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
                        Assert.That(
                            content,
                            Is.EqualTo("RX\r\n     41 0D 0A\r\n\r\n"));
                        Assert.That(harness.ViewModel.SaveDirectory, Is.EqualTo(directory));
                    }));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary>
        /// 验证非法 UTF-8 接收字节按替代字符显示而不会终止接收会话。
        /// </summary>
        [Test]
        public async Task ReceiveView_WithInvalidUtf8_RendersReplacementCharacter()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();

            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0xFF });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(
                () => harness.ViewModel.ReceiveText == "RX\r\n     �\r\n\r\n");

            Assert.That(
                harness.ViewModel.ReceiveText,
                Is.EqualTo("RX\r\n     �\r\n\r\n"));
        }

        /// <summary>
        /// 验证会话以不完整 UTF-8 尾部结束后，切换显示模式再切回仍从原始缓存恢复替代字符。
        /// </summary>
        [Test]
        public async Task ReceiveView_AfterDisconnectWithIncompleteUtf8_RerenderKeepsReplacementCharacter()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0xE4, 0xB8 });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(() => harness.Service.CreateReceiveSnapshot().Count == 1);

            await harness.ViewModel.DisconnectCommand.ExecuteAsync(null);
            harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Hex;
            harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Utf8;

            Assert.That(
                harness.ViewModel.ReceiveText,
                Is.EqualTo("RX\r\n     �\r\n\r\n"));
        }

        /// <summary>
        /// 验证重连后的完整重绘不会把旧会话半个 UTF-8 字符与新会话字节错误拼接。
        /// </summary>
        [Test]
        public async Task ReceiveView_AfterReconnect_RerenderResetsDecoderAtPortGenerationBoundary()
        {
            using AssistantHarness harness = AssistantHarness.Create();
            await harness.ConnectAsync();
            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0xE4, 0xB8 });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(() => harness.Service.CreateReceiveSnapshot().Count == 1);
            await harness.ViewModel.DisconnectCommand.ExecuteAsync(null);
            await harness.ConnectAsync();
            await harness.InjectReceiveAndWaitForStatisticsAsync(new byte[] { 0xAD });
            harness.TimeProvider.Advance(ReceiveWindowWithSchedulingMargin);
            await harness.WaitUntilAsync(() => harness.Service.CreateReceiveSnapshot().Count == 2);

            harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Hex;
            harness.ViewModel.ReceiveMode = SerialAssistantDataMode.Utf8;

            Assert.That(
                harness.ViewModel.ReceiveText,
                Is.EqualTo("RX\r\n     �\r\n\r\nRX\r\n     �\r\n\r\n"));
        }

        /// <summary>
        /// 管理一套不访问真实硬件的串口助手 ViewModel 测试依赖。
        /// </summary>
        private sealed class AssistantHarness : IDisposable
        {
            /// <summary>
            /// 初始化完整测试依赖。
            /// </summary>
            /// <param name="timeProvider">确定性时间源。</param>
            /// <param name="transport">独立模拟串口传输。</param>
            /// <param name="service">独立助手会话服务。</param>
            /// <param name="viewModel">被测助手 ViewModel。</param>
            private AssistantHarness(
                ManualTimeProvider timeProvider,
                FakeSerialTransport transport,
                SerialAssistantSessionService service,
                SerialAssistantViewModel viewModel)
            {
                TimeProvider = timeProvider;
                Transport = transport;
                Service = service;
                ViewModel = viewModel;
            }

            /// <summary>
            /// 获取确定性时间源。
            /// </summary>
            internal ManualTimeProvider TimeProvider { get; }

            /// <summary>
            /// 获取独立模拟串口传输。
            /// </summary>
            internal FakeSerialTransport Transport { get; }

            /// <summary>
            /// 获取独立助手会话服务。
            /// </summary>
            internal SerialAssistantSessionService Service { get; }

            /// <summary>
            /// 获取被测助手 ViewModel。
            /// </summary>
            internal SerialAssistantViewModel ViewModel { get; }

            /// <summary>
            /// 创建默认 115200-8-N-1、UTF-8 模式且未连接的测试环境。
            /// </summary>
            /// <returns>拥有独立助手传输和会话服务的测试环境。</returns>
            internal static AssistantHarness Create()
            {
                ManualTimeProvider timeProvider = new();
                FakeSerialTransport transport = new(timeProvider);
                SerialAssistantSessionService service = new(transport, timeProvider);
                FakeSerialPortDiscovery discovery = new(
                    new[]
                    {
                        new SerialPortDescriptor(
                            "COM_ASSISTANT",
                            "模拟助手串口 (COM_ASSISTANT)",
                            null,
                            false),
                    });
                SerialAssistantPreferences preferences = new()
                {
                    PortName = "COM_ASSISTANT",
                    BaudRate = 115200,
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                };
                SerialAssistantViewModel viewModel = new(
                    service,
                    discovery,
                    new ImmediateUiDispatcher(),
                    preferences);
                return new AssistantHarness(
                    timeProvider,
                    transport,
                    service,
                    viewModel);
            }

            /// <summary>
            /// 通过连接命令启动测试会话。
            /// </summary>
            /// <returns>连接命令完成后的任务。</returns>
            internal Task ConnectAsync()
            {
                return ViewModel.ConnectCommand.ExecuteAsync(null);
            }

            /// <summary>
            /// 在注入前订阅下一项统计事件，并等待服务完成本接收块的合批状态提交。
            /// </summary>
            /// <param name="data">需要注入当前模拟会话的非空线路字节。</param>
            /// <returns>与本次注入对应的统计事件完成后的任务。</returns>
            internal async Task InjectReceiveAndWaitForStatisticsAsync(ReadOnlyMemory<byte> data)
            {
                long previousRevision = Service.Statistics.StatisticsRevision;
                TaskCompletionSource completion = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void HandleStatistics(SerialAssistantStatistics statistics)
                {
                    if (statistics.StatisticsRevision > previousRevision)
                    {
                        completion.TrySetResult();
                    }
                }

                Service.StatisticsChanged += HandleStatistics;

                try
                {
                    await Transport.InjectReceiveAsync(data);
                    await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    Service.StatisticsChanged -= HandleStatistics;
                }
            }

            /// <summary>
            /// 在短真实超时内等待后台状态满足条件。
            /// </summary>
            /// <param name="condition">成功时返回真的线程安全条件。</param>
            /// <returns>条件满足后的任务。</returns>
            internal async Task WaitUntilAsync(Func<bool> condition)
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

                while (!condition())
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await Task.Delay(1, timeout.Token);
                }
            }

            /// <summary>
            /// 取消事件订阅并同步释放测试专属会话服务。
            /// </summary>
            public void Dispose()
            {
                ViewModel.Dispose();
                Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
