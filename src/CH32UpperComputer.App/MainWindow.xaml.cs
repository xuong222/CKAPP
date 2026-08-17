using CH32UpperComputer.App.ViewModels;

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CH32UpperComputer.App
{
    /// <summary>
    /// 承载工业仪表盘第五版主界面，并把窗口关闭转换为确定性异步退出流程。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Windows 通知顶层窗口硬件配置变化的消息编号。
        /// </summary>
        private const int WindowMessageDeviceChange = 0x0219;

        /// <summary>
        /// Windows 通知设备节点集合变化的事件值。
        /// </summary>
        private const long DeviceNodesChanged = 0x0007;

        /// <summary>
        /// Windows 通知设备已经到达并可用的事件值。
        /// </summary>
        private const long DeviceArrival = 0x8000;

        /// <summary>
        /// Windows 通知设备已经完成移除的事件值。
        /// </summary>
        private const long DeviceRemoveComplete = 0x8004;

        /// <summary>
        /// 主窗口对应的 Win32 消息源，用于接收串口热插拔通知。
        /// </summary>
        private HwndSource? windowMessageSource;

        /// <summary>
        /// 防止用户重复点击关闭按钮并发启动多个退出流程。
        /// </summary>
        private bool shutdownRequested;

        /// <summary>
        /// 指示应用后台资源已释放，下一次关闭可以直接完成。
        /// </summary>
        private bool shutdownCompleted;

        /// <summary>
        /// 初始化主窗口及其已由组合根完整构造的数据上下文。
        /// </summary>
        /// <param name="viewModel">聚合顶部状态和五个页面的主窗口 ViewModel。</param>
        public MainWindow(MainWindowViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            InitializeComponent();
            DataContext = viewModel;
        }

        /// <summary>
        /// 主窗口取得 Win32 句柄后安装唯一设备变更消息钩子。
        /// </summary>
        /// <param name="eventArgs">窗口源初始化事件参数。</param>
        protected override void OnSourceInitialized(EventArgs eventArgs)
        {
            base.OnSourceInitialized(eventArgs);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            windowMessageSource = HwndSource.FromHwnd(handle);
            windowMessageSource?.AddHook(HandleWindowMessage);
        }

        /// <summary>
        /// 窗口最终关闭后移除消息钩子，避免 Win32 源继续引用窗口实例。
        /// </summary>
        /// <param name="eventArgs">窗口关闭完成事件参数。</param>
        protected override void OnClosed(EventArgs eventArgs)
        {
            windowMessageSource?.RemoveHook(HandleWindowMessage);
            windowMessageSource = null;
            base.OnClosed(eventArgs);
        }

        /// <summary>
        /// 窗口首次完成布局和绘制后，按可选环境变量保存仅用于自动化视觉验收的自渲染 PNG。
        /// </summary>
        /// <param name="eventArgs">WPF 内容首次完成渲染的事件参数。</param>
        protected override void OnContentRendered(EventArgs eventArgs)
        {
            base.OnContentRendered(eventArgs);
            string? capturePath = Environment.GetEnvironmentVariable("CH32_CAPTURE_PATH");

            if (string.IsNullOrWhiteSpace(capturePath))
            {
                return;
            }

            try
            {
                ApplyAutomatedCaptureWindowSize();
                SelectAutomatedCaptureTab();
                SaveRenderedWindow(capturePath);
            }
            catch (Exception exception)
            {
                Environment.SetEnvironmentVariable("CH32_CAPTURE_ERROR", exception.Message);
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("CH32_CAPTURE_AND_EXIT"),
                "1",
                StringComparison.Ordinal))
            {
                Close();
            }
        }

        /// <summary>
        /// 仅在自动化视觉验收显式指定标签索引时切换页面，正常启动不改变默认实时监控页。
        /// </summary>
        private void SelectAutomatedCaptureTab()
        {
            string? tabIndexText = Environment.GetEnvironmentVariable(
                "CH32_CAPTURE_TAB_INDEX");

            if (!int.TryParse(tabIndexText, out int tabIndex) ||
                tabIndex < 0 ||
                tabIndex >= MainTabs.Items.Count)
            {
                return;
            }

            MainTabs.SelectedIndex = tabIndex;
            MainTabs.UpdateLayout();

            string? registerAddressText = Environment.GetEnvironmentVariable(
                "CH32_CAPTURE_REGISTER_ADDRESS");

            if (int.TryParse(registerAddressText, out int registerAddress))
            {
                ParametersPage.PrepareAutomatedCapture(registerAddress);
                MainTabs.UpdateLayout();
            }

            if (string.Equals(
                Environment.GetEnvironmentVariable("CH32_CAPTURE_SERIAL_DROPDOWN"),
                "1",
                StringComparison.Ordinal))
            {
                SerialSettingsExpander.IsExpanded = true;
                UpdateLayout();
                SerialPortComboBox.IsDropDownOpen = true;
            }
        }

        /// <summary>
        /// 首次关闭时取消同步销毁并等待应用完成安全退出，再触发最终关闭。
        /// </summary>
        /// <param name="eventArgs">WPF 可取消关闭事件参数。</param>
        protected override async void OnClosing(CancelEventArgs eventArgs)
        {
            if (shutdownCompleted)
            {
                base.OnClosing(eventArgs);
                return;
            }

            eventArgs.Cancel = true;

            if (shutdownRequested)
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel &&
                viewModel.FirmwareUpgrade.IsUpgradeActive)
            {
                MessageBoxResult confirmation = MessageBox.Show(
                    this,
                    "固件升级正在进行。关闭窗口会取消升级并保持未完成 App 无效，确定退出吗？",
                    "确认取消固件升级",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (confirmation != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            shutdownRequested = true;
            IsEnabled = false;
            Title = "CH32 工业监控上位机 · 正在安全退出";
            Hide();

            try
            {
                if (Application.Current is App app)
                {
                    try
                    {
                        await app.ShutdownRuntimeAsync()
                            .WaitAsync(TimeSpan.FromSeconds(2))
                            .ConfigureAwait(true);
                    }
                    catch (TimeoutException)
                    {
                        // 可见窗口已立即隐藏；超出总预算的驱动清理由后台线程继续尽力收敛。
                    }
                }
            }
            finally
            {
                shutdownCompleted = true;
                Close();
            }
        }

        /// <summary>
        /// 串口设置区域展开时安排一次防抖设备刷新。
        /// </summary>
        /// <param name="sender">触发展开的串口设置控件。</param>
        /// <param name="eventArgs">展开路由事件参数。</param>
        private void HandleSerialSettingsExpanded(
            object sender,
            RoutedEventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            ScheduleSerialPortRefresh();
        }

        /// <summary>
        /// 用户打开串口下拉框时安排一次防抖设备刷新。
        /// </summary>
        /// <param name="sender">触发下拉打开的串口选择控件。</param>
        /// <param name="eventArgs">下拉打开事件参数。</param>
        private void HandleSerialPortDropDownOpened(
            object sender,
            EventArgs eventArgs)
        {
            _ = sender;
            _ = eventArgs;
            ScheduleSerialPortRefresh();
        }

        /// <summary>
        /// 处理顶层窗口收到的设备节点变化，只安排异步刷新而不执行阻塞设备查询。
        /// </summary>
        /// <param name="windowHandle">接收消息的窗口句柄。</param>
        /// <param name="message">Win32 消息编号。</param>
        /// <param name="wordParameter">设备变更事件类型。</param>
        /// <param name="longParameter">设备特定结构指针，本处理器不解引用。</param>
        /// <param name="handled">是否阻止其他钩子继续处理；本方法始终保持为假。</param>
        /// <returns>本方法不处理消息结果，始终返回零指针。</returns>
        private IntPtr HandleWindowMessage(
            IntPtr windowHandle,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter,
            ref bool handled)
        {
            _ = windowHandle;
            _ = longParameter;
            handled = false;

            if (message != WindowMessageDeviceChange)
            {
                return IntPtr.Zero;
            }

            long eventCode = wordParameter.ToInt64();

            if (eventCode is DeviceNodesChanged or DeviceArrival or DeviceRemoveComplete)
            {
                ScheduleSerialPortRefresh();
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 将一次串口刷新请求同时转交给 Modbus 和串口助手的 250 毫秒防抖入口。
        /// </summary>
        private void ScheduleSerialPortRefresh()
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.SerialConnection.SchedulePortRefresh();
                viewModel.SerialAssistant.SchedulePortRefresh();
            }
        }

        /// <summary>
        /// 仅在视觉验收显式请求时把窗口调整到 XAML 声明的最小尺寸。
        /// </summary>
        private void ApplyAutomatedCaptureWindowSize()
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable("CH32_CAPTURE_MINIMUM_WINDOW"),
                "1",
                StringComparison.Ordinal))
            {
                return;
            }

            Width = MinWidth;
            Height = MinHeight;
            UpdateLayout();
        }

        /// <summary>
        /// 使用 WPF RenderTargetBitmap 保存当前窗口完整视觉树，避免远程桌面屏幕抓取返回黑帧。
        /// </summary>
        /// <param name="capturePath">目标 PNG 的绝对或相对文件路径。</param>
        private void SaveRenderedWindow(string capturePath)
        {
            string fullPath = Path.GetFullPath(capturePath);
            string? directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PresentationSource? source = PresentationSource.FromVisual(this);
            Matrix transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            int pixelWidth = Math.Max(1, checked((int)Math.Ceiling(ActualWidth * transform.M11)));
            int pixelHeight = Math.Max(1, checked((int)Math.Ceiling(ActualHeight * transform.M22)));
            RenderTargetBitmap bitmap = new(
                pixelWidth,
                pixelHeight,
                96d * transform.M11,
                96d * transform.M22,
                PixelFormats.Pbgra32);
            bitmap.Render(this);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using FileStream stream = new(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            encoder.Save(stream);
        }
    }
}
