using System.Collections.Concurrent;

namespace CH32UpperComputer.App.Services
{
    /// <summary>
    /// 将后台产生的轻量项目按最多五十项或五十毫秒批量提交到界面线程。
    /// </summary>
    /// <typeparam name="TItem">单个不可变或由调用方独占的轻量项目类型。</typeparam>
    public sealed class DispatcherBatcher<TItem> : IDisposable
    {
        /// <summary>
        /// 单次界面提交允许处理的最大项目数。
        /// </summary>
        public const int MaximumBatchSize = 50;

        /// <summary>
        /// 有数据但未达到数量上限时允许等待的最长时间。
        /// </summary>
        public static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// 接收后台生产者写入的线程安全队列。
        /// </summary>
        private readonly ConcurrentQueue<TItem> pendingItems = new();

        /// <summary>
        /// 负责把批次投递到 WPF 线程的调度器抽象。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 在界面线程消费一个只读批次的回调。
        /// </summary>
        private readonly Action<IReadOnlyList<TItem>> consumeBatch;

        /// <summary>
        /// 在低流量场景触发五十毫秒批量提交的定时器。
        /// </summary>
        private readonly Timer timer;

        /// <summary>
        /// 防止同一时刻重复向界面调度器投递排空任务。
        /// </summary>
        private int dispatchScheduled;

        /// <summary>
        /// 指示批处理器已经永久释放。
        /// </summary>
        private int disposed;

        /// <summary>
        /// 初始化一个固定批量上限的界面投递器。
        /// </summary>
        /// <param name="dispatcher">负责切换到界面线程的调度器抽象。</param>
        /// <param name="consumeBatch">仅在界面线程调用的批次消费函数。</param>
        public DispatcherBatcher(
            IUiDispatcher dispatcher,
            Action<IReadOnlyList<TItem>> consumeBatch)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(consumeBatch);
            this.dispatcher = dispatcher;
            this.consumeBatch = consumeBatch;
            timer = new Timer(
                static state => ((DispatcherBatcher<TItem>)state!).ScheduleDispatch(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// 追加一个项目；达到五十项时立即安排提交，否则启动五十毫秒定时器。
        /// </summary>
        /// <param name="item">需要交给界面线程的单个项目。</param>
        public void Enqueue(TItem item)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            pendingItems.Enqueue(item);

            if (pendingItems.Count >= MaximumBatchSize)
            {
                ScheduleDispatch();
                return;
            }

            timer.Change(MaximumDelay, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// 将一组已由上游批量产生的项目追加到同一界面批处理队列。
        /// </summary>
        /// <param name="items">按业务顺序排列的项目集合。</param>
        public void EnqueueRange(IEnumerable<TItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            foreach (TItem item in items)
            {
                Enqueue(item);
            }
        }

        /// <summary>
        /// 停止定时器并丢弃尚未提交的界面项目。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            timer.Dispose();

            while (pendingItems.TryDequeue(out _))
            {
            }
        }

        /// <summary>
        /// 保证同一时刻只有一个界面排空动作被投递。
        /// </summary>
        private void ScheduleDispatch()
        {
            if (Volatile.Read(ref disposed) != 0 ||
                Interlocked.CompareExchange(ref dispatchScheduled, 1, 0) != 0)
            {
                return;
            }

            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            dispatcher.Post(DrainOnUiThread);
        }

        /// <summary>
        /// 在界面线程按固定上限排空队列，并在仍有积压时继续安排下一批。
        /// </summary>
        private void DrainOnUiThread()
        {
            List<TItem> batch = new(MaximumBatchSize);

            while (batch.Count < MaximumBatchSize && pendingItems.TryDequeue(out TItem? item))
            {
                batch.Add(item);
            }

            try
            {
                if (batch.Count > 0 && Volatile.Read(ref disposed) == 0)
                {
                    consumeBatch(batch);
                }
            }
            finally
            {
                Interlocked.Exchange(ref dispatchScheduled, 0);

                if (!pendingItems.IsEmpty && Volatile.Read(ref disposed) == 0)
                {
                    ScheduleDispatch();
                }
            }
        }
    }
}
