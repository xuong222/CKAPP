using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Logging;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

using System.IO;

namespace CH32UpperComputer.App.Tests.Integration
{
    /// <summary>
    /// 验证连接、收发框、严格解析、数据卡、日志以及安全退出组成的完整应用工作流。
    /// </summary>
    [TestFixture]
    public sealed class ApplicationWorkflowTests
    {
        /// <summary>
        /// 验证有效手动读取更新六张卡片和同编号 TX/RX，而 CRC 错误不会覆盖最后有效显示。
        /// </summary>
        [Test]
        public async Task ManualReadThenCrcFailure_PreservesLastValidCardsAndTransactionLogs()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            harness.Device.SetRegister(0x0002, 2534);
            harness.Device.SetRegister(0x0003, 120);
            harness.Device.SetRegister(0x0004, 35);
            harness.Device.SetRegister(0x0005, 87);
            harness.Device.SetRegister(0x0006, 880);
            harness.Device.SetRegister(0x0007, 12);
            harness.Device.SetRegister(0x0008, 1);
            harness.Device.SetRegister(0x0009, 0x0004);
            await harness.ConnectAsync();

            Assert.That(harness.Transport.WrittenFrames, Is.Empty);

            Task successfulSend = harness.CommandConsole.SendCommand.ExecuteAsync(null);
            await harness.RespondToWriteAsync(1);
            await successfulSend.WaitAsync(TimeSpan.FromSeconds(1));
            string[] lastValidValues = harness.Monitor.SensorCards
                .Select(card => card.ValueText)
                .ToArray();
            IReadOnlyList<CommunicationLogEntry> successfulLogs =
                harness.LogService.CreateDataSnapshot();
            harness.Device.SetRegister(0x0002, 3000);
            Task failedSend = harness.CommandConsole.SendCommand.ExecuteAsync(null);
            ReadOnlyMemory<byte> secondWrite = await harness.WaitForWriteAsync(2);
            await harness.Device.HandleWriteAsync(
                secondWrite,
                SimulatedModbusBehavior.CrcError,
                CancellationToken.None);
            await harness.WaitForReceiveSequenceAsync(2);
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(1000));
            await failedSend.WaitAsync(TimeSpan.FromSeconds(1));
            IReadOnlyList<CommunicationLogEntry> allLogs = harness.LogService.CreateDataSnapshot();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(lastValidValues, Is.EqualTo(new[]
                {
                    "25.34",
                    "120",
                    "35",
                    "8.7",
                    "880",
                    "1.2",
                }));
                Assert.That(harness.Monitor.SensorCards.Select(card => card.ValueText), Is.EqualTo(lastValidValues));
                Assert.That(harness.Monitor.OverallStateText, Is.EqualTo("设备报警"));
                Assert.That(harness.Monitor.AlarmItems[2].IsActive, Is.True);
                Assert.That(harness.CommandConsole.SendCount, Is.EqualTo(2));
                Assert.That(harness.CommandConsole.SuccessCount, Is.EqualTo(1));
                Assert.That(harness.CommandConsole.FailureCount, Is.EqualTo(1));
                Assert.That(successfulLogs.Count(entry =>
                    entry.Direction == CommunicationDirection.Transmit), Is.EqualTo(1));
                Assert.That(successfulLogs.Count(entry =>
                    entry.Direction == CommunicationDirection.Receive), Is.EqualTo(1));
                Assert.That(successfulLogs.Select(entry => entry.TransactionId).Distinct(), Is.EqualTo(new long?[] { 1 }));
                Assert.That(allLogs.Count(entry => entry.TransactionId == 2), Is.EqualTo(3));
                Assert.That(allLogs.Any(entry =>
                    entry.TransactionId == 2 &&
                    entry.Direction == CommunicationDirection.LateOrUnsolicited &&
                    !entry.RawData.IsEmpty), Is.True);
                Assert.That(allLogs.Any(entry =>
                    entry.TransactionId == 2 &&
                    entry.Direction == CommunicationDirection.Error &&
                    entry.State == TransactionCompletionState.TimedOut), Is.True);
                Assert.That(allLogs.Any(entry =>
                    entry.TransactionId == 2 &&
                    entry.Direction == CommunicationDirection.Receive), Is.False);
            }));
        }

        /// <summary>
        /// 验证空闲期收到的无归属线路帧会带原始字节进入通信日志和实时数据收发框。
        /// </summary>
        [Test]
        public async Task UnsolicitedFrame_IsVisibleInCommunicationLogAndLiveDataView()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            byte[] unsolicitedFrame = ModbusCrc16.Append(
                [0x01, 0x03, 0x02, 0x00, 0x2A]);

            await harness.Transport.InjectReceiveAsync(unsolicitedFrame);
            await harness.WaitForReceiveSequenceAsync(1);
            harness.CommunicationLog.FlushPendingEntries();
            IReadOnlyList<CommunicationLogEntry> dataSnapshot =
                harness.LogService.CreateDataSnapshot();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(dataSnapshot, Has.Count.EqualTo(1));
                Assert.That(
                    dataSnapshot[0].Direction,
                    Is.EqualTo(CommunicationDirection.LateOrUnsolicited));
                Assert.That(dataSnapshot[0].RawData.ToArray(), Is.EqualTo(unsolicitedFrame));
                Assert.That(harness.CommunicationLog.Entries, Has.Count.EqualTo(1));
                Assert.That(
                    harness.CommunicationLog.Entries[0].Direction,
                    Is.EqualTo(CommunicationDirection.LateOrUnsolicited));
            }));
        }

        /// <summary>
        /// 验证活动事务、定时等待和线程池日志导出同时存在时，退出序列等待应用副作用排空并只释放串口一次。
        /// </summary>
        [Test]
        public async Task ShutdownWithPendingTransaction_WaitsForOperationsAndDisposesTransportOnce()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            ModbusRequest readRequest = ModbusRequestFactory.CreateReadHoldingRegisters(1, 0x0002, 1);
            TransactionRequest transaction = harness.CommandConsole.CreateStandardTransaction(readRequest);
            harness.Periodic.Configure(transaction, TimeSpan.FromMilliseconds(100));
            harness.Periodic.Start();
            Task<TransactionExecutionResult> pendingOperation = harness.OperationService
                .ExecuteAsync(transaction, CancellationToken.None)
                .AsTask();
            await harness.WaitForWriteAsync(1);
            string temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"CH32UpperComputer_AppShutdown_{Guid.NewGuid():N}");
            string exportPath = Path.Combine(temporaryDirectory, "退出并发日志.csv");
            Task export = harness.LogService.ExportCsvAsync(exportPath, CancellationToken.None);

            await Task.WhenAll(
                export,
                harness.ShutdownRuntimeAsync()).WaitAsync(TimeSpan.FromSeconds(2));
            TransactionExecutionResult result = await pendingOperation.WaitAsync(TimeSpan.FromSeconds(1));
            await harness.ShutdownRuntimeAsync();

            try
            {
                Assert.Multiple((Action)(() =>
                {
                    Assert.That(
                        result.Outcome!.State,
                        Is.EqualTo(TransactionCompletionState.ApplicationStopping));
                    Assert.That(harness.Periodic.IsRunning, Is.False);
                    Assert.That(harness.Coordinator.IsTransactionBusy, Is.False);
                    Assert.That(harness.Transport.IsOpen, Is.False);
                    Assert.That(harness.Transport.CloseOperationCount, Is.EqualTo(1));
                    Assert.That(harness.Transport.DisposeOperationCount, Is.EqualTo(1));
                    Assert.That(File.Exists(exportPath), Is.True);
                    Assert.That(new FileInfo(exportPath).Length, Is.GreaterThan(0));
                }));
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, true);
                }
            }
        }
    }
}
