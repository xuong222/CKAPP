using System.Windows.Threading;

namespace CH32UpperComputer.App.Services
{
    /// <summary>
    /// 使用指定 WPF Dispatcher 实现统一的界面线程投递边界。
    /// </summary>
    public sealed class WpfUiDispatcher : IUiDispatcher
    {
        /// <summary>
        /// 应用主界面所属的 WPF 调度器。
        /// </summary>
        private readonly Dispatcher dispatcher;

        /// <summary>
        /// 初始化 WPF 界面调度器适配器。
        /// </summary>
        /// <param name="dispatcher">主窗口或应用当前使用的非空 WPF Dispatcher。</param>
        public WpfUiDispatcher(Dispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            this.dispatcher = dispatcher;
        }

        /// <summary>
        /// 获取当前线程是否已经拥有界面调度器访问权。
        /// </summary>
        public bool CheckAccess => dispatcher.CheckAccess();

        /// <summary>
        /// 以数据绑定优先级异步投递一个短小界面更新动作。
        /// </summary>
        /// <param name="action">不得为空且不得执行长时间阻塞工作的界面更新动作。</param>
        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                _ = dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
            }
            catch (InvalidOperationException) when (
                dispatcher.HasShutdownStarted ||
                dispatcher.HasShutdownFinished)
            {
                // WPF 生命周期已经结束时丢弃仅用于显示的迟到更新。
            }
        }
    }
}
