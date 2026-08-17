using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.Views;
using CH32UpperComputer.Infrastructure.Settings;

namespace CH32UpperComputer.App.Tests.Views
{
    /// <summary>
    /// 验证串口助手模式下拉框实际显示规范名称并回写持久化枚举值。
    /// </summary>
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    [NonParallelizable]
    public sealed class SerialAssistantModeSelectorTests
    {
        /// <summary>
        /// 验证接收模式下拉框显示 UTF-8，而不是选项对象类型名称。
        /// </summary>
        [Test]
        public void ReceiveModeSelector_DefaultSelectionDisplaysUtf8()
        {
            EnsureApplicationResources();
            AppViewModelHarness harness = AppViewModelHarness.Create();
            Window? window = null;

            try
            {
                SerialAssistantView view = new()
                {
                    DataContext = harness.SerialAssistant,
                };
                window = new Window
                {
                    Width = 1180,
                    Height = 720,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = view,
                };
                window.Show();
                FlushDispatcher();
                ComboBox[] selectors = FindVisualDescendants<ComboBox>(view)
                    .Where(selector => selector.ItemsSource == harness.SerialAssistant.DataModeOptions)
                    .ToArray();

                Assert.Multiple(
                    (Action)(() =>
                    {
                        Assert.That(selectors, Has.Length.EqualTo(2));
                        Assert.That(selectors[0].Text, Is.EqualTo("UTF-8"));
                        Assert.That(selectors[1].Text, Is.EqualTo("UTF-8"));
                        Assert.That(selectors[0].SelectedValue, Is.EqualTo(SerialAssistantDataMode.Utf8));
                        Assert.That(selectors[0].ItemTemplate, Is.Not.Null);
                        Assert.That(selectors[1].ItemTemplate, Is.Not.Null);
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
        /// 确保测试视图加载生产应用相同的颜色、排版和控件资源。
        /// </summary>
        private static void EnsureApplicationResources()
        {
            _ = WpfApplicationHost.EnsureCreatedWithProductionResources();
        }

        /// <summary>
        /// 递归枚举指定视觉树中的全部目标类型元素。
        /// </summary>
        /// <typeparam name="TElement">需要查找的 WPF 元素类型。</typeparam>
        /// <param name="root">开始搜索的视觉树根节点。</param>
        /// <returns>按视觉树顺序返回的全部目标元素。</returns>
        private static IEnumerable<TElement> FindVisualDescendants<TElement>(
            DependencyObject root)
            where TElement : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);

                if (child is TElement target)
                {
                    yield return target;
                }

                foreach (TElement nested in FindVisualDescendants<TElement>(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>
        /// 处理当前 STA 调度器中已经排队的布局、绑定和控件生成任务。
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
