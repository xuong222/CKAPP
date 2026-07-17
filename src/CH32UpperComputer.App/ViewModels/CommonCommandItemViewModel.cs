using CommunityToolkit.Mvvm.Input;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示收发区右侧单列显示的一项常用指令模板。
    /// </summary>
    public sealed class CommonCommandItemViewModel
    {
        /// <summary>
        /// 初始化一项直接提交事务的常用指令。
        /// </summary>
        /// <param name="title">按钮主标题。</param>
        /// <param name="description">寄存器范围或用途说明。</param>
        /// <param name="sendAsync">用户点击后生成当前地址帧并完成一次发送事务的异步动作。</param>
        /// <param name="canSend">判断当前连接和忙状态是否允许发送的函数。</param>
        public CommonCommandItemViewModel(
            string title,
            string description,
            Func<Task> sendAsync,
            Func<bool> canSend)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
            ArgumentNullException.ThrowIfNull(sendAsync);
            ArgumentNullException.ThrowIfNull(canSend);
            Title = title;
            Description = description;
            SelectCommand = new AsyncRelayCommand(sendAsync, canSend);
        }

        /// <summary>
        /// 获取按钮主标题。
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// 获取寄存器范围或用途说明。
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// 获取把模板显示到输入框并直接提交一次事务的异步命令。
        /// </summary>
        public IAsyncRelayCommand SelectCommand { get; }

        /// <summary>
        /// 通知常用指令按钮重新计算连接、忙状态和自身执行状态。
        /// </summary>
        public void NotifyCanExecuteChanged()
        {
            SelectCommand.NotifyCanExecuteChanged();
        }
    }
}
