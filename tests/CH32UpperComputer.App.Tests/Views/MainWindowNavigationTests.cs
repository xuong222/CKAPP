using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.App.Views;

using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace CH32UpperComputer.App.Tests.Views
{
    /// <summary>
    /// 验证左侧导航与无标题页签在真实 WPF 加载周期中的初始选择和页面呈现。
    /// </summary>
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public sealed class MainWindowNavigationTests
    {
        /// <summary>
        /// 验证主窗口首次显示后立即选中实时监控页，防止页签索引为 -1 时吞掉全部页面内容。
        /// </summary>
        [Test]
        public void InitialLoad_SelectsAndDisplaysMonitorPage()
        {
            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            MainWindowViewModel viewModel = CreateMainWindowViewModel(harness);
            MainWindow? window = null;

            try
            {
                window = new MainWindow(viewModel)
                {
                    ShowInTaskbar = false,
                };
                window.Show();
                FlushDispatcher();

                ListBox navigation = (ListBox)window.FindName("ModeNavigation");
                TabControl tabs = (TabControl)window.FindName("MainTabs");

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(navigation.SelectedIndex, Is.Zero);
                    Assert.That(tabs.SelectedIndex, Is.Zero);
                    Assert.That(tabs.SelectedContent, Is.TypeOf<MonitorView>());
                    Assert.That(((MonitorView)tabs.SelectedContent).IsVisible, Is.True);
                }));
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// 验证左侧五个入口逐项切换时，主标签索引和实际页面类型始终一一对应。
        /// </summary>
        [Test]
        public void NavigationSelection_DisplaysEveryRegisteredPage()
        {
            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            MainWindowViewModel viewModel = CreateMainWindowViewModel(harness);
            MainWindow? window = null;

            try
            {
                window = new MainWindow(viewModel)
                {
                    ShowInTaskbar = false,
                };
                window.Show();
                FlushDispatcher();

                ListBox navigation = (ListBox)window.FindName("ModeNavigation");
                TabControl tabs = (TabControl)window.FindName("MainTabs");
                Type[] expectedPageTypes =
                [
                    typeof(MonitorView),
                    typeof(ParametersView),
                    typeof(CommunicationLogView),
                    typeof(FirmwareUpgradeView),
                    typeof(SerialAssistantView),
                ];

                for (int index = 0; index < expectedPageTypes.Length; index++)
                {
                    navigation.SelectedIndex = index;
                    FlushDispatcher();

                    Assert.Multiple((Action)(() =>
                    {
                        Assert.That(navigation.SelectedIndex, Is.EqualTo(index));
                        Assert.That(tabs.SelectedIndex, Is.EqualTo(index));
                        Assert.That(tabs.SelectedContent, Is.TypeOf(expectedPageTypes[index]));
                        Assert.That(((FrameworkElement)tabs.SelectedContent).IsVisible, Is.True);
                    }));
                }
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// 验证快速切换后独立深墨活动带准确落到最终标签，且当前页面始终保持可见。
        /// </summary>
        [Test]
        public void RapidNavigation_IndicatorTracksSelectionAndPageRemainsVisible()
        {
            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            MainWindowViewModel viewModel = CreateMainWindowViewModel(harness);
            MainWindow? window = null;

            try
            {
                window = new MainWindow(viewModel)
                {
                    ShowInTaskbar = false,
                };
                window.Show();
                FlushDispatcher();

                ListBox navigation = (ListBox)window.FindName("ModeNavigation");
                TabControl tabs = (TabControl)window.FindName("MainTabs");
                int[] navigationSequence = [4, 1, 3, 2, 0];

                foreach (int index in navigationSequence)
                {
                    navigation.SelectedIndex = index;
                }

                FlushDispatcher();
                Thread.Sleep(520);
                FlushDispatcher();
                ListBoxItem selectedItem =
                    (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(0);
                Grid? navigationHost = window.FindName("NavigationAnimationHost") as Grid;
                Border? indicator = window.FindName("NavigationIndicator") as Border;
                TranslateTransform? indicatorTransform =
                    indicator?.RenderTransform as TranslateTransform;
                double expectedIndicatorY = navigationHost is null
                    ? double.NaN
                    : selectedItem.TransformToAncestor(navigationHost)
                        .Transform(new Point()).Y;
                FrameworkElement selectedPage =
                    (FrameworkElement)tabs.SelectedContent;

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(navigation.SelectedIndex, Is.Zero);
                    Assert.That(tabs.SelectedIndex, Is.Zero);
                    Assert.That(tabs.SelectedContent, Is.TypeOf<MonitorView>());
                    Assert.That(navigationHost, Is.Not.Null);
                    Assert.That(indicator, Is.Not.Null);
                    Assert.That(HasVisibleBrush(indicator?.Background), Is.True);
                    Assert.That(indicatorTransform, Is.Not.Null);
                    Assert.That(
                        indicatorTransform?.Y,
                        Is.EqualTo(expectedIndicatorY).Within(0.5d));
                    Assert.That(selectedPage.IsVisible, Is.True);
                    Assert.That(selectedPage.Opacity, Is.EqualTo(1d).Within(0.001d));
                }));
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// 验证左栏是唯一选择状态源，页面只能单向跟随，避免两个选择器双向重入后出现残留状态。
        /// </summary>
        [Test]
        public void NavigationSelection_UsesListBoxAsSingleOneWaySource()
        {
            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            MainWindowViewModel viewModel = CreateMainWindowViewModel(harness);
            MainWindow? window = null;

            try
            {
                window = new MainWindow(viewModel)
                {
                    ShowInTaskbar = false,
                };
                window.Show();
                FlushDispatcher();

                ListBox navigation = (ListBox)window.FindName("ModeNavigation");
                TabControl tabs = (TabControl)window.FindName("MainTabs");
                BindingBase? navigationBinding = BindingOperations.GetBindingBase(
                    navigation,
                    ListBox.SelectedIndexProperty);
                Binding? tabBinding = BindingOperations.GetBinding(
                    tabs,
                    TabControl.SelectedIndexProperty);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(navigationBinding, Is.Null);
                    Assert.That(navigation.SelectedIndex, Is.Zero);
                    Assert.That(tabBinding, Is.Not.Null);
                    Assert.That(tabBinding?.Mode, Is.EqualTo(BindingMode.OneWay));
                    Assert.That(tabBinding?.ElementName, Is.EqualTo("ModeNavigation"));
                }));
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// 验证固定五标签导航不含可滚动视口，并且焦点切换不会把任一标签裁出左栏。
        /// </summary>
        [Test]
        public void NavigationLayout_HasNoScrollableViewportAndKeepsEveryTabVisible()
        {
            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            MainWindowViewModel viewModel = CreateMainWindowViewModel(harness);
            MainWindow? window = null;

            try
            {
                window = new MainWindow(viewModel)
                {
                    ShowInTaskbar = false,
                };
                window.Show();
                FlushDispatcher();

                ListBox navigation = (ListBox)window.FindName("ModeNavigation");
                ScrollViewer? scrollViewer =
                    FindVisualDescendant<ScrollViewer>(navigation);

                Assert.That(
                    scrollViewer,
                    Is.Null,
                    "固定导航不应使用会响应 BringIntoView 的内部滚动视口。");

                Size[] windowSizes =
                [
                    new Size(1440d, 900d),
                    new Size(1180d, 720d),
                ];
                int[] focusSequence = [4, 2, 0, 3, 1];

                foreach (Size windowSize in windowSizes)
                {
                    window.Width = windowSize.Width;
                    window.Height = windowSize.Height;
                    FlushDispatcher();

                    foreach (int selectedIndex in focusSequence)
                    {
                        ListBoxItem selectedItem =
                            (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(
                                selectedIndex);
                        selectedItem.IsSelected = true;
                        _ = selectedItem.Focus();
                        selectedItem.BringIntoView();
                        FlushDispatcher();

                        for (int itemIndex = 0;
                             itemIndex < navigation.Items.Count;
                             itemIndex++)
                        {
                            ListBoxItem navigationItem =
                                (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(
                                    itemIndex);
                            Rect itemBounds = navigationItem.TransformToAncestor(navigation)
                                .TransformBounds(new Rect(navigationItem.RenderSize));
                            string layoutContext =
                                $"窗口 {windowSize.Width:0}×{windowSize.Height:0}、选中索引 " +
                                $"{selectedIndex} 后，索引 {itemIndex}";

                            Assert.Multiple((Action)(() =>
                            {
                                Assert.That(
                                    itemBounds.Top,
                                    Is.GreaterThanOrEqualTo(-0.5d),
                                    $"{layoutContext} 被裁到导航顶部之外。");
                                Assert.That(
                                    itemBounds.Bottom,
                                    Is.LessThanOrEqualTo(navigation.ActualHeight + 0.5d),
                                    $"{layoutContext} 被裁到导航底部之外。");
                            }));
                        }
                    }
                }
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// 验证系统允许动画时，深墨活动带和页面表现层获得动画，但页面可见性与高度不参与动画。
        /// </summary>
        [Test]
        public void NavigationSelection_AnimatesIndicatorAndPageWithoutAnimatingLayoutState()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                Assert.Ignore("当前 Windows 已关闭客户端区域动画。此模式由减少动态效果设置覆盖。");
            }

            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            MainWindowViewModel viewModel = CreateMainWindowViewModel(harness);
            MainWindow? window = null;

            try
            {
                window = new MainWindow(viewModel)
                {
                    ShowInTaskbar = false,
                };
                window.Show();
                FlushDispatcher();

                ListBox navigation = (ListBox)window.FindName("ModeNavigation");
                TabControl tabs = (TabControl)window.FindName("MainTabs");
                navigation.SelectedIndex = 4;
                FlushDispatcher();

                Border? indicator = window.FindName("NavigationIndicator") as Border;
                TranslateTransform? indicatorTransform =
                    indicator?.RenderTransform as TranslateTransform;
                FrameworkElement selectedPage =
                    (FrameworkElement)tabs.SelectedContent;
                ValueSource visibilityValueSource = DependencyPropertyHelper.GetValueSource(
                    selectedPage,
                    UIElement.VisibilityProperty);
                ValueSource heightValueSource = DependencyPropertyHelper.GetValueSource(
                    selectedPage,
                    FrameworkElement.HeightProperty);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(tabs.SelectedIndex, Is.EqualTo(4));
                    Assert.That(indicator, Is.Not.Null);
                    Assert.That(indicatorTransform, Is.Not.Null);
                    Assert.That(indicatorTransform?.HasAnimatedProperties, Is.True);
                    Assert.That(selectedPage.HasAnimatedProperties, Is.True);
                    Assert.That(selectedPage.IsVisible, Is.True);
                    Assert.That(visibilityValueSource.IsAnimated, Is.False);
                    Assert.That(heightValueSource.IsAnimated, Is.False);
                    Assert.That(tabs.HasAnimatedProperties, Is.False);
                }));
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// 验证鼠标进入数据模板生成的传感器卡时，即使原始 Transform 已被冻结也不会抛出异常，
        /// 并且卡片能够完成两像素上浮与归位动画。
        /// </summary>
        [Test]
        public void SensorCardHover_ClonesFrozenTransformAndCompletesLiftMotion()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                Assert.Ignore("当前 Windows 已关闭客户端区域动画，无法断言悬停动画时钟。");
            }

            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            MainWindowViewModel viewModel = CreateMainWindowViewModel(harness);
            MainWindow? window = null;

            try
            {
                window = new MainWindow(viewModel)
                {
                    ShowInTaskbar = false,
                };
                window.Show();
                FlushDispatcher();

                TabControl tabs = (TabControl)window.FindName("MainTabs");
                MonitorView monitorView = (MonitorView)tabs.SelectedContent;
                ItemsControl sensorItems = (ItemsControl)monitorView.FindName(
                    "SensorCardsItemsControl");
                sensorItems.UpdateLayout();
                ContentPresenter firstContainer = (ContentPresenter)sensorItems
                    .ItemContainerGenerator.ContainerFromIndex(0);
                Border? sensorCard = FindVisualDescendant<Border>(
                    firstContainer,
                    border => Math.Abs(border.BorderThickness.Top - 3d) < 0.01d);

                Assert.That(sensorCard, Is.Not.Null);
                Assert.That(sensorCard!.RenderTransform, Is.TypeOf<TranslateTransform>());
                TranslateTransform frozenTranslation = new();
                frozenTranslation.Freeze();
                Assert.That(frozenTranslation.IsFrozen, Is.True);
                Border hoverCard = new()
                {
                    RenderTransform = frozenTranslation,
                };
                MethodInfo? animateHover = typeof(MonitorView).GetMethod(
                    "AnimateSensorCardHover",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(animateHover, Is.Not.Null);

                _ = animateHover!.Invoke(
                    null,
                    new object[]
                    {
                        hoverCard,
                        -2d,
                    });
                TranslateTransform? hoverTransform =
                    hoverCard.RenderTransform as TranslateTransform;

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(hoverTransform, Is.Not.Null);
                    Assert.That(hoverTransform?.IsFrozen, Is.False);
                    Assert.That(hoverTransform?.HasAnimatedProperties, Is.True);
                }));

                PumpDispatcherFor(TimeSpan.FromMilliseconds(320));
                Assert.That(hoverTransform?.Y, Is.EqualTo(-2d).Within(0.05d));

                _ = animateHover.Invoke(
                    null,
                    new object[]
                    {
                        hoverCard,
                        0d,
                    });
                PumpDispatcherFor(TimeSpan.FromMilliseconds(320));
                Assert.That(hoverTransform?.Y, Is.EqualTo(0d).Within(0.05d));
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// 使用测试环境中已经构造的全部页面依赖创建生产主窗口 ViewModel。
        /// </summary>
        /// <param name="harness">包含模拟串口、页面 ViewModel 和应用服务的测试环境。</param>
        /// <returns>仅由测试依赖组成、尚未连接设备的主窗口 ViewModel。</returns>
        private static MainWindowViewModel CreateMainWindowViewModel(
            AppViewModelHarness harness)
        {
            return new MainWindowViewModel(
                harness.SerialConnection,
                harness.CommandConsole,
                harness.Monitor,
                harness.Parameters,
                harness.FirmwareUpgrade,
                harness.CommunicationLog,
                harness.SerialAssistant,
                harness.OperationService,
                harness.Dispatcher);
        }

        /// <summary>
        /// 判断画刷在自身透明度和颜色 Alpha 通道共同作用后是否仍能形成可见背景。
        /// </summary>
        /// <param name="brush">需要检查的选中卡片背景画刷；允许为空。</param>
        /// <returns>画刷至少包含一种非透明颜色且整体透明度大于零时返回真。</returns>
        private static bool HasVisibleBrush(Brush? brush)
        {
            if (brush is null || brush.Opacity <= 0d)
            {
                return false;
            }

            return brush switch
            {
                SolidColorBrush solidColorBrush => solidColorBrush.Color.A > 0,
                GradientBrush gradientBrush => gradientBrush.GradientStops.Any(
                    gradientStop => gradientStop.Color.A > 0),
                _ => true,
            };
        }

        /// <summary>
        /// 深度优先查找指定可视根节点下第一个目标类型元素。
        /// </summary>
        /// <typeparam name="TElement">需要查找的 WPF 可视元素类型。</typeparam>
        /// <param name="root">开始遍历的可视树根节点。</param>
        /// <returns>找到时返回第一个匹配元素；不存在时返回空。</returns>
        private static TElement? FindVisualDescendant<TElement>(DependencyObject root)
            where TElement : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);

            for (int childIndex = 0; childIndex < childCount; childIndex++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);

                if (child is TElement matchingElement)
                {
                    return matchingElement;
                }

                TElement? nestedElement = FindVisualDescendant<TElement>(child);

                if (nestedElement is not null)
                {
                    return nestedElement;
                }
            }

            return null;
        }

        /// <summary>
        /// 深度优先查找指定可视根节点下第一个同时满足类型和谓词的元素。
        /// </summary>
        /// <typeparam name="TElement">需要查找的 WPF 可视元素类型。</typeparam>
        /// <param name="root">开始遍历的可视树根节点。</param>
        /// <param name="predicate">用于排除同类型非目标元素的精确筛选条件。</param>
        /// <returns>找到时返回第一个匹配元素；不存在时返回空。</returns>
        private static TElement? FindVisualDescendant<TElement>(
            DependencyObject root,
            Func<TElement, bool> predicate)
            where TElement : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);

            for (int childIndex = 0; childIndex < childCount; childIndex++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);

                if (child is TElement matchingElement && predicate(matchingElement))
                {
                    return matchingElement;
                }

                TElement? nestedElement = FindVisualDescendant(child, predicate);

                if (nestedElement is not null)
                {
                    return nestedElement;
                }
            }

            return null;
        }

        /// <summary>
        /// 等待当前 STA 调度器完成加载、绑定和布局队列，确保断言观察稳定界面状态。
        /// </summary>
        private static void FlushDispatcher()
        {
            Dispatcher.CurrentDispatcher.Invoke(
                static () =>
                {
                },
                DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 在指定时长内持续处理当前 STA 调度器，使 WPF 动画时钟能够真实推进到目标帧。
        /// </summary>
        /// <param name="duration">需要持续处理布局、输入和渲染队列的时间。</param>
        private static void PumpDispatcherFor(TimeSpan duration)
        {
            DispatcherFrame frame = new();
            DispatcherTimer timer = new(DispatcherPriority.Background)
            {
                Interval = duration,
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
