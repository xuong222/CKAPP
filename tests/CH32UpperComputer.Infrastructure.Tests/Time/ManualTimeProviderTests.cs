using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Time
{
    /// <summary>
    /// 验证手动时间源的一次性、周期、更改、释放和确定性排序语义。
    /// </summary>
    [TestFixture]
    public sealed class ManualTimeProviderTests
    {
        /// <summary>
        /// 验证一次性计时器只触发一次，正周期计时器会补齐推进区间内每个到期点。
        /// </summary>
        [Test]
        public void Advance_ShouldFireOneShotOnceAndPeriodicAtEveryDueTime()
        {
            ManualTimeProvider timeProvider = new();
            int oneShotCount = 0;
            int periodicCount = 0;
            using ITimer oneShot = timeProvider.CreateTimer(
                _ => oneShotCount++,
                null,
                TimeSpan.FromMilliseconds(5),
                TimeSpan.Zero);
            using ITimer periodic = timeProvider.CreateTimer(
                _ => periodicCount++,
                null,
                TimeSpan.FromMilliseconds(2),
                TimeSpan.FromMilliseconds(3));

            timeProvider.Advance(TimeSpan.FromMilliseconds(8));
            timeProvider.Advance(TimeSpan.FromMilliseconds(20));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(oneShotCount, Is.EqualTo(1));
                Assert.That(periodicCount, Is.EqualTo(9));
            }));
        }

        /// <summary>
        /// 验证无限到期时间保持禁用，Change 可启用，Dispose 后无法再次更改或触发。
        /// </summary>
        [Test]
        public void ChangeAndDispose_ShouldControlFutureCallbacks()
        {
            ManualTimeProvider timeProvider = new();
            int callbackCount = 0;
            ITimer timer = timeProvider.CreateTimer(
                _ => callbackCount++,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);

            timeProvider.Advance(TimeSpan.FromHours(1));
            bool changed = timer.Change(TimeSpan.FromMilliseconds(4), TimeSpan.Zero);
            timeProvider.Advance(TimeSpan.FromMilliseconds(4));
            timer.Dispose();
            bool changedAfterDispose = timer.Change(TimeSpan.Zero, TimeSpan.Zero);
            timeProvider.Advance(TimeSpan.FromHours(1));

            Assert.Multiple((Action)(() =>
            {
                Assert.That(changed, Is.True);
                Assert.That(callbackCount, Is.EqualTo(1));
                Assert.That(changedAfterDispose, Is.False);
            }));
        }

        /// <summary>
        /// 验证同一到期时刻按创建顺序执行，并遵守零延迟立即触发的框架约定。
        /// </summary>
        [Test]
        public void SameDueTimeAndZeroDueTime_ShouldUseDeterministicOrder()
        {
            ManualTimeProvider timeProvider = new();
            List<int> order = [];
            using ITimer first = timeProvider.CreateTimer(
                _ => order.Add(1),
                null,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.Zero);
            using ITimer second = timeProvider.CreateTimer(
                _ => order.Add(2),
                null,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.Zero);
            using ITimer immediate = timeProvider.CreateTimer(
                _ => order.Add(0),
                null,
                TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);

            timeProvider.Advance(TimeSpan.FromMilliseconds(10));

            Assert.That(order, Is.EqualTo(new[] { 0, 1, 2 }));
        }

        /// <summary>
        /// 验证回调在时间源锁外执行，能够在回调内安全 Change、Dispose 和 Advance。
        /// </summary>
        [Test]
        public void Callback_ShouldBeAbleToChangeDisposeAndAdvanceWithoutDeadlock()
        {
            ManualTimeProvider timeProvider = new();
            ITimer? timer = null;
            int callbackCount = 0;
            timer = timeProvider.CreateTimer(
                _ =>
                {
                    callbackCount++;
                    timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    timeProvider.Advance(TimeSpan.Zero);
                    timer.Dispose();
                },
                null,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1));

            timeProvider.Advance(TimeSpan.FromMilliseconds(10));

            Assert.That(callbackCount, Is.EqualTo(1));
        }
    }
}
