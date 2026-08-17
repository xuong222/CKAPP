using CH32UpperComputer.App.Services;

namespace CH32UpperComputer.App.Tests.TestSupport
{
    /// <summary>
    /// 保存界面投递动作并由测试显式执行，用于制造后台事件迟到和重排边界。
    /// </summary>
    internal sealed class QueuedUiDispatcher : IUiDispatcher
    {
        /// <summary>
        /// 保护待执行动作队列的同步门。
        /// </summary>
        private readonly object syncRoot = new();

        /// <summary>
        /// 按实际投递顺序保存、等待测试执行的界面动作。
        /// </summary>
        private readonly Queue<Action> pendingActions = new();

        /// <summary>
        /// 测试调用线程不被视为界面线程，所有更新都必须经过显式排队。
        /// </summary>
        public bool CheckAccess => false;

        /// <summary>
        /// 将指定非空动作追加到待执行队列。
        /// </summary>
        /// <param name="action">需要延迟到测试显式调度时执行的界面动作。</param>
        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            lock (syncRoot)
            {
                pendingActions.Enqueue(action);
            }
        }

        /// <summary>
        /// 执行当前队列中的全部动作，包括执行过程中继续投递的新动作。
        /// </summary>
        public void RunAll()
        {
            while (true)
            {
                Action? action;

                lock (syncRoot)
                {
                    action = pendingActions.Count == 0
                        ? null
                        : pendingActions.Dequeue();
                }

                if (action is null)
                {
                    return;
                }

                action();
            }
        }
    }
}
