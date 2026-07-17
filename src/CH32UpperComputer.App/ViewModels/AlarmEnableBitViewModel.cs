using CommunityToolkit.Mvvm.ComponentModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示 40023 报警使能掩码低六位中的一个可编辑开关。
    /// </summary>
    public sealed partial class AlarmEnableBitViewModel : ObservableObject
    {
        /// <summary>
        /// 当前位是否准备写入一。
        /// </summary>
        [ObservableProperty]
        private bool isEnabled;

        /// <summary>
        /// 初始化一个固定报警使能位。
        /// </summary>
        /// <param name="displayName">界面显示的传感器名称。</param>
        /// <param name="bitIndex">低六位中的零基位序号。</param>
        public AlarmEnableBitViewModel(
            string displayName,
            int bitIndex)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

            if (bitIndex is < 0 or > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(bitIndex));
            }

            DisplayName = displayName;
            BitIndex = bitIndex;
        }

        /// <summary>
        /// 获取界面显示的传感器名称。
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// 获取 40023 低六位中的零基位序号。
        /// </summary>
        public int BitIndex { get; }
    }
}
