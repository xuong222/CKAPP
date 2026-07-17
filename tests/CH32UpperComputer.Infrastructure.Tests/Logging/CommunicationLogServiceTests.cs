using System.Text;
using CH32UpperComputer.Infrastructure.Logging;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Logging
{
    /// <summary>
    /// 验证通信日志双容量、批量发布、筛选和 UTF-8 导出契约。
    /// </summary>
    [TestFixture]
    public sealed class CommunicationLogServiceTests
    {
        /// <summary>
        /// 验证数据缓存与实时视图独立裁剪，并且每条记录防御性保留完整原始帧。
        /// </summary>
        [Test]
        public void Append_OverBothCapacities_KeepsNewestIndependentSnapshotsAndRawBytes()
        {
            ManualTimeProvider timeProvider = new();
            using CommunicationLogService service = new(timeProvider);
            byte[] firstRaw = [0x01, 0x03, 0x00, 0x00];
            CommunicationLogEntry first = service.Append(
                1,
                CommunicationDirection.Transmit,
                null,
                1,
                0,
                firstRaw,
                "第一条查询");
            firstRaw[0] = 0xFF;
            byte[] returnedRaw = first.RawData.ToArray();
            returnedRaw[1] = 0xFF;

            for (int index = 2; index <= 5001; index++)
            {
                service.Append(
                    index,
                    CommunicationDirection.Receive,
                    TransactionCompletionState.Succeeded,
                    1,
                    index,
                    new byte[] { (byte)index },
                    $"记录 {index}");
            }

            IReadOnlyList<CommunicationLogEntry> data = service.CreateDataSnapshot();
            IReadOnlyList<CommunicationLogEntry> live = service.CreateLiveViewSnapshot();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(first.RawData.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x03, 0x00, 0x00 }));
                Assert.That(data, Has.Count.EqualTo(CommunicationLogService.DataCacheCapacity));
                Assert.That(data[0].SequenceId, Is.EqualTo(2));
                Assert.That(data[^1].SequenceId, Is.EqualTo(5001));
                Assert.That(live, Has.Count.EqualTo(CommunicationLogService.LiveViewCapacity));
                Assert.That(live[0].SequenceId, Is.EqualTo(4252));
                Assert.That(live[^1].SequenceId, Is.EqualTo(5001));
                Assert.That(data[^1].TransactionId, Is.EqualTo(5001));
                Assert.That(data[^1].ReceiveSequence, Is.EqualTo(5001));
            }));
        }

        /// <summary>
        /// 验证不足五十条时等待五十毫秒，达到五十条时立即发布，且单批永不超过上限。
        /// </summary>
        [Test]
        public void Publication_UsesFiftyItemsOrFiftyMillisecondsWithoutPerEntryUiUpdates()
        {
            ManualTimeProvider timeProvider = new();
            using CommunicationLogService service = new(timeProvider);
            List<IReadOnlyList<CommunicationLogEntry>> batches = [];
            service.EntriesPublished += batches.Add;

            for (int index = 1; index <= 49; index++)
            {
                AppendSystemRecord(service, index);
            }

            timeProvider.Advance(TimeSpan.FromMilliseconds(49));
            Assert.That(batches, Is.Empty);
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));

            for (int index = 50; index <= 99; index++)
            {
                AppendSystemRecord(service, index);
            }

            Assert.Multiple((Action)(() =>
            {
                Assert.That(batches, Has.Count.EqualTo(2));
                Assert.That(batches[0], Has.Count.EqualTo(49));
                Assert.That(batches[1], Has.Count.EqualTo(50));
                Assert.That(batches.Select(batch => batch.Count), Has.All.LessThanOrEqualTo(50));
                Assert.That(batches.SelectMany(batch => batch).Select(entry => entry.SequenceId),
                    Is.EqualTo(Enumerable.Range(1, 99).Select(value => (long)value)));
            }));
        }

        /// <summary>
        /// 验证筛选不破坏原缓存，导出固定使用 UTF-8 BOM，且导出快照不阻塞或吸收后续追加。
        /// </summary>
        [Test]
        public async Task FilterAndExport_KeepOriginalCacheAndWriteSnapshotWithUtf8Bom()
        {
            string directory = CreateTemporaryDirectory();

            try
            {
                ManualTimeProvider timeProvider = new(
                    new DateTimeOffset(2026, 7, 17, 2, 0, 0, TimeSpan.Zero));
                using CommunicationLogService service = new(timeProvider);
                service.Append(
                    1,
                    CommunicationDirection.Transmit,
                    null,
                    3,
                    0,
                    new byte[] { 0x01, 0x03 },
                    "温度查询");
                service.Append(
                    1,
                    CommunicationDirection.Receive,
                    TransactionCompletionState.Succeeded,
                    3,
                    1,
                    new byte[] { 0x01, 0x03, 0x02, 0x09, 0xC4 },
                    "温度 25.00 ℃");
                service.Append(
                    2,
                    CommunicationDirection.LateOrUnsolicited,
                    TransactionCompletionState.TimedOut,
                    3,
                    2,
                    new byte[] { 0x01, 0x03, 0x02 },
                    "迟到响应");
                HashSet<CommunicationDirection> directions = [CommunicationDirection.Receive];

                IReadOnlyList<CommunicationLogEntry> filtered = service.CreateFilteredSnapshot(
                    directions,
                    "温度",
                    1);
                string exportPath = Path.Combine(directory, "通信日志.csv");
                Task export = service.ExportCsvAsync(exportPath);
                service.Append(
                    null,
                    CommunicationDirection.System,
                    null,
                    3,
                    0,
                    Array.Empty<byte>(),
                    "导出后新增");
                await export;
                byte[] bytes = await File.ReadAllBytesAsync(exportPath);
                string text = await File.ReadAllTextAsync(exportPath, Encoding.UTF8);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(filtered, Has.Count.EqualTo(1));
                    Assert.That(filtered[0].Summary, Is.EqualTo("温度 25.00 ℃"));
                    Assert.That(service.CreateDataSnapshot(), Has.Count.EqualTo(4));
                    Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
                    Assert.That(text, Does.Contain("温度查询"));
                    Assert.That(text, Does.Contain("温度 25.00 ℃"));
                    Assert.That(text, Does.Contain("迟到响应"));
                    Assert.That(text, Does.Not.Contain("导出后新增"));
                }));
            }
            finally
            {
                DeleteTemporaryDirectory(directory);
            }
        }

        /// <summary>
        /// 向服务追加一条没有线路字节的系统记录。
        /// </summary>
        /// <param name="service">需要接收记录的通信日志服务。</param>
        /// <param name="index">用于摘要和预期序号的正整数。</param>
        private static void AppendSystemRecord(
            CommunicationLogService service,
            int index)
        {
            service.Append(
                null,
                CommunicationDirection.System,
                null,
                0,
                0,
                Array.Empty<byte>(),
                $"系统记录 {index}");
        }

        /// <summary>
        /// 在测试工作目录下创建唯一临时目录。
        /// </summary>
        /// <returns>已经创建的绝对目录路径。</returns>
        private static string CreateTemporaryDirectory()
        {
            string directory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"communication-log-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return directory;
        }

        /// <summary>
        /// 删除由当前测试创建的临时目录和全部文件。
        /// </summary>
        /// <param name="directory">由 <see cref="CreateTemporaryDirectory"/> 创建的绝对路径。</param>
        private static void DeleteTemporaryDirectory(string directory)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
