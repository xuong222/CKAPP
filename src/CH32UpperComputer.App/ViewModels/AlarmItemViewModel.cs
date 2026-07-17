using CH32UpperComputer.Core.Registers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示右侧报警栏中的一个固定传感器报警通道。
    /// </summary>
    public sealed partial class AlarmItemViewModel : ObservableObject
    {
        /// <summary>
        /// 当前通道是否正在报警。
        /// </summary>
        [ObservableProperty]
        private bool isActive;

        /// <summary>
        /// 当前报警通道是否已由设备使能。
        /// </summary>
        [ObservableProperty]
        private bool isEnabled = true;

        /// <summary>
        /// 当前通道面向用户的简短状态文本。
        /// </summary>
        [ObservableProperty]
        private string stateText = "正常";

        /// <summary>
        /// 初始化一个固定报警通道。
        /// </summary>
        /// <param name="channel">报警位定义对应的通道枚举。</param>
        /// <param name="displayName">界面显示名称。</param>
        /// <param name="bitIndex">40010 和 40023 中对应的位序号。</param>
        public AlarmItemViewModel(
            AlarmChannel channel,
            string displayName,
            int bitIndex)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
            Channel = channel;
            DisplayName = displayName;
            BitIndex = bitIndex;
        }

        /// <summary>
        /// 获取报警通道枚举。
        /// </summary>
        public AlarmChannel Channel { get; }

        /// <summary>
        /// 获取界面显示名称。
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// 获取报警掩码中的位序号。
        /// </summary>
        public int BitIndex { get; }

        /// <summary>
        /// 使用设备报警模型更新当前通道状态。
        /// </summary>
        /// <param name="state">由 40009、40010 和 40023 原始字生成的通道状态。</param>
        public void Update(AlarmItemState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            IsActive = state.IsActive;
            IsEnabled = state.IsEnabled;
            StateText = !state.IsEnabled
                ? "未使能"
                : state.IsActive
                    ? "报警"
                    : "正常";
        }
    }
}
