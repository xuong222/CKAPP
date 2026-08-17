using System.IO;
using System.Windows;
using System.Windows.Controls;
using CH32UpperComputer.App.ViewModels;
using Microsoft.Win32;

namespace CH32UpperComputer.App.Views
{
    /// <summary>
    /// 提供串口助手视图专属的文件选择和自动滚动界面行为。
    /// </summary>
    public partial class SerialAssistantView : UserControl
    {
        /// <summary>
        /// 初始化串口助手视图和 XAML 控件树。
        /// </summary>
        public SerialAssistantView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 下拉列表打开时请求一次即时系统串口刷新。
        /// </summary>
        /// <param name="sender">打开下拉列表的串口选择框。</param>
        /// <param name="eventArgs">路由事件参数。</param>
        private void HandleSerialPortDropDownOpened(
            object sender,
            EventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;

            if (DataContext is SerialAssistantViewModel viewModel &&
                viewModel.RefreshPortsCommand.CanExecute(null))
            {
                viewModel.RefreshPortsCommand.Execute(null);
            }
        }

        /// <summary>
        /// 在接收视图变化且自动滚动启用、未暂停时滚动到底部。
        /// </summary>
        /// <param name="sender">发生文本变化的只读接收框。</param>
        /// <param name="eventArgs">文本变化路由事件参数。</param>
        private void HandleReceiveTextChanged(
            object sender,
            TextChangedEventArgs eventArgs)
        {
            _ = eventArgs;

            if (sender is TextBox textBox &&
                DataContext is SerialAssistantViewModel viewModel &&
                viewModel.AutoScroll &&
                !viewModel.IsPaused)
            {
                textBox.ScrollToEnd();
            }
        }

        /// <summary>
        /// 让用户选择文本文件，并把当前显示模式和时间戳效果原样保存为 UTF-8 BOM。
        /// </summary>
        /// <param name="sender">发起保存的按钮。</param>
        /// <param name="eventArgs">按钮点击路由事件参数。</param>
        private async void HandleSaveClick(
            object sender,
            RoutedEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;

            if (DataContext is not SerialAssistantViewModel viewModel)
            {
                return;
            }

            SaveFileDialog dialog = new()
            {
                AddExtension = true,
                DefaultExt = ".txt",
                FileName = $"serial-receive-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                InitialDirectory = Directory.Exists(viewModel.SaveDirectory)
                    ? viewModel.SaveDirectory
                    : string.Empty,
                OverwritePrompt = true,
                Title = "保存串口助手当前接收视图",
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            try
            {
                await viewModel.SaveCurrentViewAsync(
                    dialog.FileName,
                    CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"保存串口接收视图失败：{exception.Message}",
                    "串口助手",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
