using CH32UpperComputer.App.ViewModels;

using CH32UpperComputer.App.Views;

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
        /// 顶栏彩色引导线从左向右绘入的时长，与最终 HTML 原型保持一致。
        /// </summary>
        private static readonly TimeSpan TopAccentMotionDuration =
            TimeSpan.FromMilliseconds(760);

        /// <summary>
        /// 顶栏彩色引导线在窗口稳定呈现后的短暂起始延迟。
        /// </summary>
        private static readonly TimeSpan TopAccentMotionDelay =
            TimeSpan.FromMilliseconds(80);

        /// <summary>
        /// 左侧深墨活动带移动到目标标签的时长。
        /// </summary>
        private static readonly TimeSpan NavigationIndicatorMotionDuration =
            TimeSpan.FromMilliseconds(420);

        /// <summary>
        /// 导航标题在选中或悬停反馈中横向落定的时长。
        /// </summary>
        private static readonly TimeSpan NavigationTextMotionDuration =
            TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// 导航箭头跟随标题向右收束的时长。
        /// </summary>
        private static readonly TimeSpan NavigationChevronMotionDuration =
            TimeSpan.FromMilliseconds(260);

        /// <summary>
        /// 当前导航标题相对普通状态向右强调的距离。
        /// </summary>
        private const double SelectedNavigationTextOffset = 4d;

        /// <summary>
        /// 悬停导航标题相对普通状态向右反馈的距离。
        /// </summary>
        private const double HoverNavigationTextOffset = 3d;

        /// <summary>
        /// 当前导航箭头相对普通状态向右收束的距离。
        /// </summary>
        private const double SelectedNavigationChevronOffset = 3d;

        /// <summary>
        /// 页面根层从轻雾透明度恢复为完整终态的时长。
        /// </summary>
        private static readonly TimeSpan PageRootMotionDuration =
            TimeSpan.FromMilliseconds(420);

        /// <summary>
        /// 页面主要内容组向上落定并显现的时长。
        /// </summary>
        private static readonly TimeSpan PageGroupMotionDuration =
            TimeSpan.FromMilliseconds(520);

        /// <summary>
        /// 第二个页面内容组相对首组的短暂节奏差。
        /// </summary>
        private static readonly TimeSpan SecondPageGroupMotionDelay =
            TimeSpan.FromMilliseconds(45);

        /// <summary>
        /// 页面内容组入场时自下向上的安全位移量；只改变渲染，不参与布局测量。
        /// </summary>
        private const double PageGroupMotionOffset = 10d;

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
        /// 指示窗口已经完成首帧布局，后续导航选择可以播放交互动画。
        /// </summary>
        private bool interfaceMotionInitialized;

        /// <summary>
        /// 记录最后一次待处理的导航索引，快速连续切换时只落实最终请求。
        /// </summary>
        private int pendingNavigationIndex = -1;

        /// <summary>
        /// 防止同一调度器周期内为连续选择重复排入多个页面动画回调。
        /// </summary>
        private bool navigationMotionDispatchPending;

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
            InitializeInterfaceMotion(capturePath is null);

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

            ModeNavigation.SelectedIndex = tabIndex;
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
            Title = "CKAPP · 正在安全退出";
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
        /// 在首帧布局稳定后同步导航活动带、顶部引导线和当前页面的入场终态。
        /// </summary>
        /// <param name="allowAnimation">是否允许播放非必要界面动画；自动截图时传入假。</param>
        private void InitializeInterfaceMotion(bool allowAnimation)
        {
            interfaceMotionInitialized = true;
            PositionNavigationIndicator(ModeNavigation.SelectedIndex, false);
            UpdateNavigationItemOffsets(false);
            PlayInitialChromeMotion(allowAnimation);

            if (MainTabs.SelectedContent is FrameworkElement selectedPage)
            {
                PlayPageEntryMotion(selectedPage, allowAnimation);
            }
        }

        /// <summary>
        /// 首次呈现时按最终 HTML 原型的 760 毫秒节奏绘入顶部细色线。
        /// </summary>
        /// <param name="allowAnimation">是否允许播放一次性绘入动画；自动截图时传入假。</param>
        private void PlayInitialChromeMotion(bool allowAnimation)
        {
            TopAccentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TopAccentScale.ScaleX = 1d;

            if (!allowAnimation || !ShouldAnimateInterface())
            {
                return;
            }

            DoubleAnimationUsingKeyFrames accentAnimation = CreateInterfaceSplineAnimation(
                0d,
                1d,
                TopAccentMotionDuration,
                TopAccentMotionDelay);
            TopAccentScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                accentAnimation,
                HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>
        /// 左栏选择发生变化时只登记最后一次请求，并把动画安排到绑定和布局完成之后。
        /// </summary>
        /// <param name="sender">发生选择变化的左侧导航列表。</param>
        /// <param name="eventArgs">包含本次新增和移除选择项的路由事件参数。</param>
        private void HandleNavigationSelectionChanged(
            object sender,
            SelectionChangedEventArgs eventArgs)
        {
            _ = eventArgs;

            if (sender is not ListBox navigation)
            {
                return;
            }

            pendingNavigationIndex = navigation.SelectedIndex;

            if (navigationMotionDispatchPending)
            {
                return;
            }

            navigationMotionDispatchPending = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(ApplyPendingNavigationMotion));
        }

        /// <summary>
        /// 在 TabControl 已跟随左栏索引更新后，落实最终一次活动带和页面入场动画。
        /// </summary>
        private void ApplyPendingNavigationMotion()
        {
            navigationMotionDispatchPending = false;
            int selectedIndex = pendingNavigationIndex;

            if (selectedIndex < 0 || selectedIndex != ModeNavigation.SelectedIndex)
            {
                return;
            }

            bool allowAnimation = interfaceMotionInitialized && ShouldAnimateInterface();
            PositionNavigationIndicator(selectedIndex, allowAnimation);
            UpdateNavigationItemOffsets(allowAnimation);
            MainTabs.UpdateLayout();

            if (MainTabs.SelectedIndex == selectedIndex &&
                MainTabs.SelectedContent is FrameworkElement selectedPage)
            {
                PlayPageEntryMotion(selectedPage, allowAnimation);
            }
        }

        /// <summary>
        /// 把深墨活动带定位到指定导航项；连续点击时从当前屏幕位置平滑接续到新目标。
        /// </summary>
        /// <param name="selectedIndex">需要对齐的导航项索引。</param>
        /// <param name="allowAnimation">是否播放 420 毫秒活动带移动；初始化和减少动态效果时传入假。</param>
        private void PositionNavigationIndicator(
            int selectedIndex,
            bool allowAnimation)
        {
            if (selectedIndex < 0 ||
                ModeNavigation.ItemContainerGenerator.ContainerFromIndex(selectedIndex)
                    is not ListBoxItem selectedItem)
            {
                return;
            }

            ModeNavigation.UpdateLayout();
            Point targetPoint = selectedItem.TransformToAncestor(NavigationAnimationHost)
                .Transform(new Point());
            double targetY = targetPoint.Y;
            double currentY = NavigationIndicatorTranslate.Y;

            NavigationIndicatorTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                null);
            NavigationIndicatorTranslate.Y = targetY;
            NavigationIndicator.Height = selectedItem.ActualHeight > 0d
                ? selectedItem.ActualHeight
                : 68d;

            if (!allowAnimation || Math.Abs(currentY - targetY) < 0.1d)
            {
                return;
            }

            DoubleAnimationUsingKeyFrames indicatorAnimation =
                CreateInterfaceSplineAnimation(
                    currentY,
                    targetY,
                    NavigationIndicatorMotionDuration,
                    TimeSpan.Zero);
            NavigationIndicatorTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                indicatorAnimation,
                HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>
        /// 鼠标进入导航卡片时为标题提供轻微右移反馈，当前项仍保持更明确的选中位移。
        /// </summary>
        /// <param name="sender">鼠标当前进入的导航项容器。</param>
        /// <param name="eventArgs">本次鼠标进入事件参数。</param>
        private void HandleNavigationItemMouseEnter(
            object sender,
            MouseEventArgs eventArgs)
        {
            _ = eventArgs;

            if (sender is not ListBoxItem navigationItem)
            {
                return;
            }

            double textTarget = navigationItem.IsSelected
                ? SelectedNavigationTextOffset
                : HoverNavigationTextOffset;
            double chevronTarget = navigationItem.IsSelected
                ? SelectedNavigationChevronOffset
                : 0d;
            AnimateNavigationItemOffsets(
                navigationItem,
                textTarget,
                chevronTarget,
                ShouldAnimateInterface());
        }

        /// <summary>
        /// 鼠标离开导航卡片时恢复普通位置，或保留当前项的选中强调位置。
        /// </summary>
        /// <param name="sender">鼠标当前离开的导航项容器。</param>
        /// <param name="eventArgs">本次鼠标离开事件参数。</param>
        private void HandleNavigationItemMouseLeave(
            object sender,
            MouseEventArgs eventArgs)
        {
            _ = eventArgs;

            if (sender is not ListBoxItem navigationItem)
            {
                return;
            }

            double textTarget = navigationItem.IsSelected
                ? SelectedNavigationTextOffset
                : 0d;
            double chevronTarget = navigationItem.IsSelected
                ? SelectedNavigationChevronOffset
                : 0d;
            AnimateNavigationItemOffsets(
                navigationItem,
                textTarget,
                chevronTarget,
                ShouldAnimateInterface());
        }

        /// <summary>
        /// 同步五个导航项的标题和箭头终态，并在用户切换时只动画发生变化的项。
        /// </summary>
        /// <param name="allowAnimation">是否允许按原型时长播放横向落定动画。</param>
        private void UpdateNavigationItemOffsets(bool allowAnimation)
        {
            for (int itemIndex = 0;
                 itemIndex < ModeNavigation.Items.Count;
                 itemIndex++)
            {
                if (ModeNavigation.ItemContainerGenerator.ContainerFromIndex(itemIndex)
                    is not ListBoxItem navigationItem)
                {
                    continue;
                }

                double textTarget = navigationItem.IsSelected
                    ? SelectedNavigationTextOffset
                    : 0d;
                double chevronTarget = navigationItem.IsSelected
                    ? SelectedNavigationChevronOffset
                    : 0d;
                AnimateNavigationItemOffsets(
                    navigationItem,
                    textTarget,
                    chevronTarget,
                    allowAnimation);
            }
        }

        /// <summary>
        /// 将单个导航项模板内的标题和箭头分别平滑移动到给定目标位置。
        /// </summary>
        /// <param name="navigationItem">需要更新模板视觉的导航项。</param>
        /// <param name="textTarget">标题 TranslateTransform 的目标横坐标。</param>
        /// <param name="chevronTarget">箭头 TranslateTransform 的目标横坐标。</param>
        /// <param name="allowAnimation">是否播放过渡；减少动态效果时传入假。</param>
        private static void AnimateNavigationItemOffsets(
            ListBoxItem navigationItem,
            double textTarget,
            double chevronTarget,
            bool allowAnimation)
        {
            if (!TryGetNavigationItemTransforms(
                    navigationItem,
                    out TranslateTransform? textTranslation,
                    out TranslateTransform? chevronTranslation))
            {
                return;
            }

            AnimateHorizontalTranslation(
                textTranslation!,
                textTarget,
                NavigationTextMotionDuration,
                allowAnimation);
            AnimateHorizontalTranslation(
                chevronTranslation!,
                chevronTarget,
                NavigationChevronMotionDuration,
                allowAnimation);
        }

        /// <summary>
        /// 从导航项模板中取得标题和箭头的独立平移变换。
        /// </summary>
        /// <param name="navigationItem">需要展开并检索其控件模板的导航项。</param>
        /// <param name="textTranslation">成功时返回标题平移变换。</param>
        /// <param name="chevronTranslation">成功时返回箭头平移变换。</param>
        /// <returns>两个模板变换均存在时返回真。</returns>
        private static bool TryGetNavigationItemTransforms(
            ListBoxItem navigationItem,
            out TranslateTransform? textTranslation,
            out TranslateTransform? chevronTranslation)
        {
            navigationItem.ApplyTemplate();
            textTranslation = navigationItem.Template.FindName(
                "NavigationTextTranslate",
                navigationItem) as TranslateTransform;
            chevronTranslation = navigationItem.Template.FindName(
                "NavigationChevronTranslate",
                navigationItem) as TranslateTransform;
            return textTranslation is not null && chevronTranslation is not null;
        }

        /// <summary>
        /// 从当前屏幕位置接续横向平移，避免快速悬停或切换时发生回跳。
        /// </summary>
        /// <param name="translation">需要控制其 X 属性的平移变换。</param>
        /// <param name="targetX">本次交互期望的稳定横坐标。</param>
        /// <param name="duration">从当前位置到目标位置的过渡时长。</param>
        /// <param name="allowAnimation">是否播放过渡；为假时立即设置终态。</param>
        private static void AnimateHorizontalTranslation(
            TranslateTransform translation,
            double targetX,
            TimeSpan duration,
            bool allowAnimation)
        {
            double currentX = translation.X;
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            translation.X = targetX;

            if (!allowAnimation || Math.Abs(currentX - targetX) < 0.05d)
            {
                return;
            }

            translation.BeginAnimation(
                TranslateTransform.XProperty,
                CreateInterfaceSplineAnimation(
                    currentX,
                    targetX,
                    duration,
                    TimeSpan.Zero),
                HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>
        /// 重置全部页面的旧动画时钟后，为当前页播放原型中的根层淡入和内容组落定。
        /// </summary>
        /// <param name="selectedPage">当前 TabControl 已经显示的页面根元素。</param>
        /// <param name="allowAnimation">是否允许页面与监测卡片播放入场动画。</param>
        private void PlayPageEntryMotion(
            FrameworkElement selectedPage,
            bool allowAnimation)
        {
            ResetPageEntryMotions();

            if (!allowAnimation || !ShouldAnimateInterface())
            {
                return;
            }

            selectedPage.Opacity = 1d;
            selectedPage.BeginAnimation(
                UIElement.OpacityProperty,
                CreateInterfaceSplineAnimation(
                    0.62d,
                    1d,
                    PageRootMotionDuration,
                    TimeSpan.Zero),
                HandoffBehavior.SnapshotAndReplace);

            IReadOnlyList<FrameworkElement> pageGroups = GetPageEntryGroups(selectedPage);

            for (int groupIndex = 0; groupIndex < pageGroups.Count; groupIndex++)
            {
                FrameworkElement pageGroup = pageGroups[groupIndex];
                TimeSpan delay = groupIndex == 1
                    ? SecondPageGroupMotionDelay
                    : TimeSpan.Zero;
                TranslateTransform translation = GetOrCreateTranslation(pageGroup);
                pageGroup.Opacity = 1d;
                translation.Y = 0d;
                pageGroup.BeginAnimation(
                    UIElement.OpacityProperty,
                    CreateInterfaceSplineAnimation(
                        0d,
                        1d,
                        PageGroupMotionDuration,
                        delay),
                    HandoffBehavior.SnapshotAndReplace);
                translation.BeginAnimation(
                    TranslateTransform.YProperty,
                    CreateInterfaceSplineAnimation(
                        PageGroupMotionOffset,
                        0d,
                        PageGroupMotionDuration,
                        delay),
                    HandoffBehavior.SnapshotAndReplace);
            }

            if (selectedPage is MonitorView monitorView)
            {
                monitorView.PlaySensorEntryMotion();
            }
        }

        /// <summary>
        /// 清除五个页面遗留的透明度和位移动画，并恢复稳定终态，供快速切换安全复用。
        /// </summary>
        private void ResetPageEntryMotions()
        {
            foreach (object tabObject in MainTabs.Items)
            {
                if (tabObject is not TabItem tabItem ||
                    tabItem.Content is not FrameworkElement page)
                {
                    continue;
                }

                page.BeginAnimation(UIElement.OpacityProperty, null);
                page.Opacity = 1d;

                foreach (FrameworkElement pageGroup in GetPageEntryGroups(page))
                {
                    pageGroup.BeginAnimation(UIElement.OpacityProperty, null);
                    pageGroup.Opacity = 1d;
                    TranslateTransform translation = GetOrCreateTranslation(pageGroup);
                    translation.BeginAnimation(TranslateTransform.YProperty, null);
                    translation.Y = 0d;
                }

                if (page is MonitorView monitorView)
                {
                    monitorView.ResetSensorEntryMotion();
                }
            }
        }

        /// <summary>
        /// 提取页面根面板中的主要直系内容组，并忽略只承担布局调节的分隔器。
        /// </summary>
        /// <param name="page">需要分析其直系视觉内容的页面根元素。</param>
        /// <returns>按 XAML 顺序排列的可动画内容组；无面板时返回空集合。</returns>
        private static IReadOnlyList<FrameworkElement> GetPageEntryGroups(
            FrameworkElement page)
        {
            if (page is ContentControl contentControl &&
                contentControl.Content is Panel rootPanel)
            {
                return rootPanel.Children
                    .OfType<FrameworkElement>()
                    .Where(element => element is not GridSplitter)
                    .ToArray();
            }

            return Array.Empty<FrameworkElement>();
        }

        /// <summary>
        /// 获取元素上专用于界面入场的平移变换；已有其他变换时用组合容器无损保留。
        /// </summary>
        /// <param name="element">需要执行非布局位移动画的页面内容组。</param>
        /// <returns>可以独立控制纵向位移的 TranslateTransform。</returns>
        private static TranslateTransform GetOrCreateTranslation(
            FrameworkElement element)
        {
            if (element.RenderTransform is TranslateTransform translation)
            {
                return translation;
            }

            if (element.RenderTransform is TransformGroup existingGroup &&
                existingGroup.Children.OfType<TranslateTransform>().FirstOrDefault()
                    is TranslateTransform existingTranslation)
            {
                return existingTranslation;
            }

            TranslateTransform newTranslation = new();

            if (element.RenderTransform is null ||
                element.RenderTransform == Transform.Identity)
            {
                element.RenderTransform = newTranslation;
                return newTranslation;
            }

            TransformGroup transformGroup = new();
            transformGroup.Children.Add(element.RenderTransform);
            transformGroup.Children.Add(newTranslation);
            element.RenderTransform = transformGroup;
            return newTranslation;
        }

        /// <summary>
        /// 创建与 CSS cubic-bezier(.22, 1, .36, 1) 等价的 WPF 属性动画。
        /// </summary>
        /// <param name="from">动画启动瞬间的属性值。</param>
        /// <param name="to">动画结束后的视觉终值。</param>
        /// <param name="duration">从起点运行到终点所需的持续时间。</param>
        /// <param name="beginTime">动画相对当前时刻的延迟时间。</param>
        /// <returns>仅作用于合成属性、不触发布局重排的样条关键帧动画。</returns>
        private static DoubleAnimationUsingKeyFrames CreateInterfaceSplineAnimation(
            double from,
            double to,
            TimeSpan duration,
            TimeSpan beginTime)
        {
            DoubleAnimationUsingKeyFrames animation = new()
            {
                BeginTime = beginTime,
                Duration = duration,
                FillBehavior = FillBehavior.Stop,
            };
            animation.KeyFrames.Add(
                new DiscreteDoubleKeyFrame(
                    from,
                    KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(
                new SplineDoubleKeyFrame(
                    to,
                    KeyTime.FromTimeSpan(duration),
                    new KeySpline(0.22d, 1d, 0.36d, 1d)));
            return animation;
        }

        /// <summary>
        /// 统一判断当前 Windows 设置是否允许非必要界面动画。
        /// </summary>
        /// <returns>系统允许客户端区域动画且当前不是自动截图流程时返回真。</returns>
        private static bool ShouldAnimateInterface()
        {
            return SystemParameters.ClientAreaAnimation &&
                string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("CH32_CAPTURE_PATH"));
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
