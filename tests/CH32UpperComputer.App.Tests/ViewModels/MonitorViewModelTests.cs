using CH32UpperComputer.App.Tests.TestSupport;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证监控页固定卡片和报警项在尚无有效快照时保持明确占位显示。
    /// </summary>
    [TestFixture]
    public sealed class MonitorViewModelTests
    {
        /// <summary>
        /// 验证监控页始终包含六张数据卡和六项报警，且创建页面不会产生线路写入。
        /// </summary>
        [Test]
        public async Task InitialState_HasSixCardsSixAlarmsAndNoWrite()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Monitor.SensorCards, Has.Count.EqualTo(6));
                Assert.That(harness.Monitor.SensorCards.All(card => card.ValueText == "--"), Is.True);
                Assert.That(harness.Monitor.AlarmItems, Has.Count.EqualTo(6));
                Assert.That(harness.Monitor.OverallStateText, Is.EqualTo("等待报警状态"));
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
            }));
        }
    }
}
