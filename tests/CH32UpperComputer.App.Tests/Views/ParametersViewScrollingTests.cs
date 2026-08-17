using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.App.Views;

using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace CH32UpperComputer.App.Tests.Views
{
    /// <summary>
    /// 验证参数表在真实 WPF 滚动和标签切换后仍以行 ViewModel 保存选择状态。
    /// </summary>
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public sealed class ParametersViewScrollingTests
    {
        /// <summary>
        /// 验证多个远距离选择在反复滚动和离开页面后不会被容器回收覆盖。
        /// </summary>
        [Test]
        public void SelectedRows_SurviveScrollingAndTabRoundTrips()
        {
            EnsureApplicationResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            Window? window = null;

            try
            {
                ParametersView parametersView = new()
                {
                    DataContext = harness.Parameters,
                };
                TabControl tabs = new();
                TabItem parametersTab = new()
                {
                    Header = "参数设置",
                    Content = parametersView,
                };
                TabItem otherTab = new()
                {
                    Header = "其他页面",
                    Content = new TextBlock
                    {
                        Text = "占位页面",
                    },
                };
                tabs.Items.Add(parametersTab);
                tabs.Items.Add(otherTab);
                tabs.SelectedIndex = 0;
                window = new Window
                {
                    Width = 1500,
                    Height = 760,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = tabs,
                };
                window.Show();
                FlushDispatcher();

                DataGrid grid = (DataGrid)parametersView.FindName("RegistersGrid");
                ParameterItemViewModel[] selected =
                [
                    FindItem(harness, 40003),
                    FindItem(harness, 40017),
                    FindItem(harness, 40035),
                ];

                foreach (ParameterItemViewModel item in selected)
                {
                    item.IsSelected = true;
                    grid.ScrollIntoView(item);
                    FlushDispatcher();
                    CheckBox checkBox = GetSelectionCheckBox(grid, item);
                    Assert.That(checkBox.IsChecked, Is.True);
                }

                for (int cycle = 0; cycle < 4; cycle++)
                {
                    grid.ScrollIntoView(harness.Parameters.Registers[^1]);
                    FlushDispatcher();
                    grid.ScrollIntoView(harness.Parameters.Registers[0]);
                    FlushDispatcher();
                    tabs.SelectedIndex = 1;
                    FlushDispatcher();
                    tabs.SelectedIndex = 0;
                    FlushDispatcher();

                    foreach (ParameterItemViewModel item in selected)
                    {
                        grid.ScrollIntoView(item);
                        FlushDispatcher();
                        CheckBox checkBox = GetSelectionCheckBox(grid, item);
                        BindingExpression? bindingExpression =
                            BindingOperations.GetBindingExpression(
                                checkBox,
                                CheckBox.IsCheckedProperty);
                        Assert.Multiple((Action)(() =>
                        {
                            Assert.That(item.IsSelected, Is.True);
                            Assert.That(checkBox.IsChecked, Is.True);
                            Assert.That(bindingExpression, Is.Not.Null);
                            Assert.That(
                                bindingExpression!.ParentBinding.Mode,
                                Is.EqualTo(BindingMode.TwoWay));
                            Assert.That(
                                bindingExpression.ParentBinding.UpdateSourceTrigger,
                                Is.EqualTo(UpdateSourceTrigger.PropertyChanged));
                        }));
                    }
                }

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(grid.EnableRowVirtualization, Is.False);
                    Assert.That(grid.EnableColumnVirtualization, Is.False);
                    Assert.That(
                        VirtualizingPanel.GetIsVirtualizing(grid),
                        Is.False);
                    Assert.That(
                        harness.Parameters.Registers.Count(item => item.IsSelected),
                        Is.EqualTo(selected.Length));
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
        /// 确保测试窗口加载与生产应用相同的颜色、排版和控件资源。
        /// </summary>
        private static void EnsureApplicationResources()
        {
            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
        }

        /// <summary>
        /// 根据文档地址取得测试参数行。
        /// </summary>
        /// <param name="harness">包含完整参数页的应用测试环境。</param>
        /// <param name="documentAddress">需要取得的四万区文档地址。</param>
        /// <returns>与文档地址唯一对应的参数行。</returns>
        private static ParameterItemViewModel FindItem(
            AppViewModelHarness harness,
            int documentAddress)
        {
            return harness.Parameters.Registers.Single(
                item => item.Definition.DocumentAddress == documentAddress);
        }

        /// <summary>
        /// 取得指定参数行当前可视的选择复选框。
        /// </summary>
        /// <param name="grid">承载统一寄存器表的 DataGrid。</param>
        /// <param name="item">已经滚动进入视口的参数行。</param>
        /// <returns>选择列内与该参数行绑定的 CheckBox。</returns>
        private static CheckBox GetSelectionCheckBox(
            DataGrid grid,
            ParameterItemViewModel item)
        {
            FrameworkElement? content = grid.Columns[0].GetCellContent(item);
            CheckBox? checkBox = content is null
                ? null
                : FindVisualDescendant<CheckBox>(content);
            return checkBox ?? throw new AssertionException(
                $"未生成 {item.Definition.DocumentAddress} 的选择复选框。");
        }

        /// <summary>
        /// 在指定视觉树中查找第一个目标类型后代。
        /// </summary>
        /// <typeparam name="TElement">需要查找的 WPF 元素类型。</typeparam>
        /// <param name="root">开始搜索的视觉树根节点。</param>
        /// <returns>找到的首个目标元素；不存在时返回空。</returns>
        private static TElement? FindVisualDescendant<TElement>(
            DependencyObject root)
            where TElement : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);

                if (child is TElement target)
                {
                    return target;
                }

                TElement? nested = FindVisualDescendant<TElement>(child);

                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        /// <summary>
        /// 处理当前 STA 调度器中已排队的布局、绑定和控件生成工作。
        /// </summary>
        private static void FlushDispatcher()
        {
            Dispatcher.CurrentDispatcher.Invoke(
                static () =>
                {
                },
                DispatcherPriority.ApplicationIdle);
        }
    }
}
