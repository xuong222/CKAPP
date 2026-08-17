using CH32UpperComputer.App.ViewModels;

using Microsoft.Win32;

using System.Windows;
using System.Windows.Controls;

namespace CH32UpperComputer.App.Views
{
    /// <summary>
    /// 承载固件升级页面并只负责 Windows 文件选择对话框。
    /// </summary>
    public partial class FirmwareUpgradeView : UserControl
    {
        /// <summary>
        /// 初始化固件升级页面。
        /// </summary>
        public FirmwareUpgradeView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 打开只允许选择 .bin 的系统文件对话框，并把路径交给 ViewModel 校验。
        /// </summary>
        /// <param name="sender">触发浏览操作的按钮。</param>
        /// <param name="eventArgs">按钮点击事件参数。</param>
        private async void HandleBrowseFirmware(
            object sender,
            RoutedEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;

            if (DataContext is not FirmwareUpgradeViewModel viewModel ||
                !viewModel.CanSelectFirmware)
            {
                return;
            }

            OpenFileDialog dialog = new()
            {
                AddExtension = true,
                CheckFileExists = true,
                CheckPathExists = true,
                DefaultExt = ".bin",
                Filter = "原始固件 (*.bin)|*.bin",
                Multiselect = false,
                Title = "选择 CH32V317 App 固件",
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                await viewModel
                    .SelectFirmwareAsync(dialog.FileName)
                    .ConfigureAwait(true);
            }
        }
    }
}
