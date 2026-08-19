using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.App.Views;
using CH32UpperComputer.Infrastructure.Serial;
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
        /// 验证 TX 与 RX 双行消息使用不同方向色，且秒级标题和缩进正文实际进入 WPF 模板。
        /// </summary>
        [Test]
        public async Task TrafficCanvas_TxAndRxUseDistinctDirectionBrushes()
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
                await harness.SerialAssistant.ConnectCommand.ExecuteAsync(null);
                harness.SerialAssistant.ShowTimestamps = true;
                harness.SerialAssistant.SendText = "1411";
                await harness.SerialAssistant.SendCommand.ExecuteAsync(null);
                long previousRevision = harness.SerialAssistantService
                    .Statistics
                    .StatisticsRevision;
                TaskCompletionSource receiveProcessed = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void HandleStatistics(SerialAssistantStatistics statistics)
                {
                    if (statistics.StatisticsRevision > previousRevision)
                    {
                        receiveProcessed.TrySetResult();
                    }
                }

                harness.SerialAssistantService.StatisticsChanged += HandleStatistics;

                try
                {
                    await harness.AssistantTransport.InjectReceiveAsync("reply"u8.ToArray());
                    await receiveProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    harness.SerialAssistantService.StatisticsChanged -= HandleStatistics;
                }

                harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(51));
                await WaitUntilAsync(
                    () => harness.SerialAssistant.TrafficRecords.Count == 2);
                FlushDispatcher();
                ListBox canvas = (ListBox)view.FindName("TrafficCanvasListBox");
                canvas.UpdateLayout();
                Border txRecord = CreateDirectionBorder(
                    canvas,
                    harness.SerialAssistant.TrafficRecords[0]);
                Border rxRecord = CreateDirectionBorder(
                    canvas,
                    harness.SerialAssistant.TrafficRecords[1]);
                TextBlock[] txText = FindVisualDescendants<TextBlock>(txRecord).ToArray();
                TextBlock[] rxText = FindVisualDescendants<TextBlock>(rxRecord).ToArray();
                SolidColorBrush txExpected = (SolidColorBrush)Application.Current.FindResource(
                    "Brush.ConsoleTx");
                SolidColorBrush rxExpected = (SolidColorBrush)Application.Current.FindResource(
                    "Brush.ConsoleRx");

                Assert.Multiple(
                    (Action)(() =>
                    {
                        Assert.That(canvas.Items, Has.Count.EqualTo(2));
                        Assert.That(txText[0].Text, Does.Match("^\\[\\d{2}:\\d{2}:\\d{2}\\]  TX$"));
                        Assert.That(txText[1].Text, Is.EqualTo("1411"));
                        Assert.That(rxText[0].Text, Does.Match("^\\[\\d{2}:\\d{2}:\\d{2}\\]  RX$"));
                        Assert.That(rxText[1].Text, Is.EqualTo("reply"));
                        Assert.That(
                            ((SolidColorBrush)txRecord.BorderBrush).Color,
                            Is.EqualTo(txExpected.Color));
                        Assert.That(
                            ((SolidColorBrush)rxRecord.BorderBrush).Color,
                            Is.EqualTo(rxExpected.Color));
                        Assert.That(txExpected.Color, Is.Not.EqualTo(rxExpected.Color));
                    }));
            }
            finally
            {
                window?.Close();
                FlushDispatcher();
                await harness.DisposeAsync();
            }
        }

        /// <summary>
        /// 从生产 DataTemplate 创建一项承担 TX/RX 方向色的消息边框。
        /// </summary>
        /// <param name="canvas">提供生产消息模板的串口画布列表。</param>
        /// <param name="record">需要应用到模板的 TX 或 RX 显示记录。</param>
        /// <returns>绑定方向画刷的消息边框。</returns>
        private static Border CreateDirectionBorder(
            ListBox canvas,
            SerialAssistantTrafficDisplayRecord record)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            ArgumentNullException.ThrowIfNull(record);
            Border? border = canvas.ItemTemplate.LoadContent() as Border;

            if (border is null)
            {
                throw new InvalidOperationException(
                    "串口画布生产 DataTemplate 的根元素不是消息 Border。");
            }

            border.DataContext = record;
            border.Measure(new Size(900d, 200d));
            border.Arrange(new Rect(border.DesiredSize));
            border.UpdateLayout();
            FlushDispatcher();
            return border;
        }

        /// <summary>
        /// 在短真实超时内等待后台收发记录到达界面集合。
        /// </summary>
        /// <param name="condition">记录准备完成时返回真的线程安全条件。</param>
        /// <returns>条件满足后的任务。</returns>
        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            ArgumentNullException.ThrowIfNull(condition);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));

            while (!condition())
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(1, timeout.Token);
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
