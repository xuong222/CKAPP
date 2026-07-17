using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Infrastructure.Logging;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证日志 ViewModel 的七百五十项实时容量和批量界面投影。
    /// </summary>
    [TestFixture]
    public sealed class CommunicationLogViewModelTests
    {
        /// <summary>
        /// 验证七百五十一条日志进入界面后只保留最新七百五十条且顺序正确。
        /// </summary>
        [Test]
        public void PublishedEntries_TrimLiveViewToSevenHundredFifty()
        {
            ManualTimeProvider timeProvider = new();
            using CommunicationLogService service = new(timeProvider);
            using CommunicationLogViewModel viewModel = new(
                service,
                new ImmediateUiDispatcher(),
                timeProvider,
                string.Empty);

            for (int index = 1; index <= 751; index++)
            {
                service.Append(
                    index,
                    CommunicationDirection.System,
                    null,
                    0,
                    0,
                    ReadOnlySpan<byte>.Empty,
                    $"记录 {index}");
            }

            viewModel.FlushPendingEntries();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(viewModel.Entries, Has.Count.EqualTo(750));
                Assert.That(viewModel.Entries[0].SequenceId, Is.EqualTo(2));
                Assert.That(viewModel.Entries[^1].SequenceId, Is.EqualTo(751));
            }));
        }
    }
}
