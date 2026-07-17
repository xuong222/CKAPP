using CH32UpperComputer.App.Services;

namespace CH32UpperComputer.App.Tests.TestSupport
{
    /// <summary>
    /// 在当前测试线程同步执行界面投递动作，避免依赖真实 WPF 消息泵。
    /// </summary>
    internal sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        /// <summary>
        /// 测试调用线程始终被视为拥有界面访问权。
        /// </summary>
        public bool CheckAccess => true;

        /// <summary>
        /// 立即执行指定界面更新动作。
        /// </summary>
        /// <param name="action">需要同步执行的非空动作。</param>
        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }
    }
}
