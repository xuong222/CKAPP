using System.IO;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CH32UpperComputer.App.ViewModels;
using Microsoft.Win32;

namespace CH32UpperComputer.App.Views
{
    /// <summary>
    /// 提供串口助手统一收发画布的文件选择和自动滚动界面行为。
    /// </summary>
    public partial class SerialAssistantView : UserControl
    {
        /// <summary>
        /// 当前已订阅画布集合变化的 ViewModel；卸载或切换上下文时必须解除订阅。
        /// </summary>
        private SerialAssistantViewModel? observedViewModel;

        /// <summary>
        /// 初始化串口助手视图和 XAML 控件树。
        /// </summary>
        public SerialAssistantView()
        {
            InitializeComponent();
            DataContextChanged += HandleDataContextChanged;
            Loaded += HandleLoaded;
            Unloaded += HandleUnloaded;
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
        /// 在视图加载时订阅当前 TX/RX 消息集合，支持首次创建和重新挂载。
        /// </summary>
        /// <param name="sender">已经加载到可视树的串口助手视图。</param>
        /// <param name="eventArgs">加载路由事件参数。</param>
        private void HandleLoaded(
            object sender,
            RoutedEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            ObserveViewModel(DataContext as SerialAssistantViewModel);
        }

        /// <summary>
        /// 在视图离开可视树时解除集合订阅，避免后台会话长期保留页面实例。
        /// </summary>
        /// <param name="sender">即将卸载的串口助手视图。</param>
        /// <param name="eventArgs">卸载路由事件参数。</param>
        private void HandleUnloaded(
            object sender,
            RoutedEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            ObserveViewModel(null);
        }

        /// <summary>
        /// 数据上下文切换时迁移 TX/RX 消息集合订阅。
        /// </summary>
        /// <param name="sender">发生数据上下文变化的串口助手视图。</param>
        /// <param name="eventArgs">包含新旧数据上下文的依赖属性事件参数。</param>
        private void HandleDataContextChanged(
            object sender,
            DependencyPropertyChangedEventArgs eventArgs)
        {
            _ = sender;
            ObserveViewModel(eventArgs.NewValue as SerialAssistantViewModel);
        }

        /// <summary>
        /// 把集合变化订阅绑定到唯一当前 ViewModel。
        /// </summary>
        /// <param name="viewModel">新的串口助手 ViewModel；传空表示解除订阅。</param>
        private void ObserveViewModel(SerialAssistantViewModel? viewModel)
        {
            if (ReferenceEquals(observedViewModel, viewModel))
            {
                return;
            }

            if (observedViewModel is not null)
            {
                ((INotifyCollectionChanged)observedViewModel.TrafficRecords).CollectionChanged -=
                    HandleTrafficRecordsChanged;
            }

            observedViewModel = viewModel;

            if (observedViewModel is not null)
            {
                ((INotifyCollectionChanged)observedViewModel.TrafficRecords).CollectionChanged +=
                    HandleTrafficRecordsChanged;
            }
        }

        /// <summary>
        /// 消息集合增量或完整重绘后，在布局完成阶段安排一次自动滚动。
        /// </summary>
        /// <param name="sender">发生变化的只读 TX/RX 消息集合。</param>
        /// <param name="eventArgs">集合新增、清空或 Reset 的变化详情。</param>
        private void HandleTrafficRecordsChanged(
            object? sender,
            NotifyCollectionChangedEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(ScrollTrafficCanvasToEnd));
        }

        /// <summary>
        /// 自动滚动启用且接收未暂停时，把虚拟化列表最后一项带入视区。
        /// </summary>
        private void ScrollTrafficCanvasToEnd()
        {
            SerialAssistantViewModel? viewModel = observedViewModel;

            if (viewModel is null ||
                !viewModel.AutoScroll ||
                viewModel.IsPaused ||
                viewModel.TrafficRecords.Count == 0)
            {
                return;
            }

            TrafficCanvasListBox.ScrollIntoView(viewModel.TrafficRecords[^1]);
        }

        /// <summary>
        /// 让用户选择文本文件，并把当前 TX/RX 方向、显示模式和时间戳效果原样保存为 UTF-8 BOM。
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
                FileName = $"serial-traffic-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                InitialDirectory = Directory.Exists(viewModel.SaveDirectory)
                    ? viewModel.SaveDirectory
                    : string.Empty,
                OverwritePrompt = true,
                Title = "保存串口助手当前收发画布",
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
                    $"保存串口收发画布失败：{exception.Message}",
                    "串口助手",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
