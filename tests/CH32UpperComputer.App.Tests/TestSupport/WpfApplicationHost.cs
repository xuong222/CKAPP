using System.Windows;

namespace CH32UpperComputer.App.Tests.TestSupport
{
    /// <summary>
    /// 为同一测试 AppDomain 中的多个 STA 夹具保存唯一 WPF Application 和生产资源。
    /// </summary>
    internal static class WpfApplicationHost
    {
        /// <summary>
        /// 保护唯一 Application 创建和资源装载的同步门。
        /// </summary>
        private static readonly object SyncRoot = new();

        /// <summary>
        /// 强引用保存的唯一 WPF Application，避免测试结束后被视为可重新创建。
        /// </summary>
        private static Application? application;

        /// <summary>
        /// 获取或创建当前 AppDomain 唯一的 WPF Application，并加载生产资源字典。
        /// </summary>
        /// <returns>供全部 STA 视觉测试共享的 WPF Application。</returns>
        internal static Application EnsureCreatedWithProductionResources()
        {
            lock (SyncRoot)
            {
                application ??= Application.Current ?? new Application();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                if (!application.Resources.Contains("Card"))
                {
                    foreach (string resourcePath in new[]
                    {
                        "/CKAPP;component/Resources/Colors.xaml",
                        "/CKAPP;component/Resources/Typography.xaml",
                        "/CKAPP;component/Resources/Controls.xaml",
                    })
                    {
                        application.Resources.MergedDictionaries.Add(
                            new ResourceDictionary
                            {
                                Source = new Uri(resourcePath, UriKind.Relative),
                            });
                    }
                }

                return application;
            }
        }
    }
}
