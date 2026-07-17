using CH32UpperComputer.App.ViewModels;

using System.ComponentModel;
using System.IO;
using System.Windows;
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

            shutdownRequested = true;
            IsEnabled = false;
            Title = "CH32 工业监控上位机 · 正在安全退出";

            try
            {
                if (Application.Current is App app)
                {
                    await app.ShutdownRuntimeAsync().ConfigureAwait(true);
                }
            }
            finally
            {
                shutdownCompleted = true;
                Close();
            }
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
