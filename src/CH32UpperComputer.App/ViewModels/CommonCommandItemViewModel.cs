using CommunityToolkit.Mvvm.Input;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示收发区右侧单列显示的一项常用指令模板。
    /// </summary>
    public sealed class CommonCommandItemViewModel
    {
        /// <summary>
        /// 初始化一项可一键填入输入框的常用指令。
        /// </summary>
        /// <param name="title">按钮主标题。</param>
        /// <param name="description">寄存器范围或用途说明。</param>
        /// <param name="selectAction">用户点击后生成并填入当前地址帧的动作。</param>
        public CommonCommandItemViewModel(
            string title,
            string description,
            Action selectAction)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
            ArgumentNullException.ThrowIfNull(selectAction);
            Title = title;
            Description = description;
            SelectCommand = new RelayCommand(selectAction);
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
        /// 获取把模板填入当前指令输入框的命令。
        /// </summary>
        public IRelayCommand SelectCommand { get; }
    }
}
