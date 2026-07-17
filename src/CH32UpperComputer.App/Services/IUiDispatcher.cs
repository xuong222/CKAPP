namespace CH32UpperComputer.App.Services
{
    /// <summary>
    /// 抽象 WPF Dispatcher，使后台通信回调能够批量、安全地更新界面并可在测试中替换。
    /// </summary>
    public interface IUiDispatcher
    {
        /// <summary>
        /// 获取当前线程是否已经拥有界面调度器访问权。
        /// </summary>
        bool CheckAccess { get; }

        /// <summary>
        /// 将一个短小的界面更新动作投递到调度器。
        /// </summary>
        /// <param name="action">不得为空且不得执行长时间阻塞工作的界面更新动作。</param>
        void Post(Action action);
    }
}
