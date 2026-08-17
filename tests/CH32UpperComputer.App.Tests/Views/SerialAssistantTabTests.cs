using System.Threading;
using System.IO;

namespace CH32UpperComputer.App.Tests.Views
{
    /// <summary>
    /// 验证主界面第五个标签页的索引和标题保持稳定。
    /// </summary>
    [TestFixture]
    [Apartment(ApartmentState.STA)]
    public sealed class SerialAssistantTabTests
    {
        /// <summary>
        /// 验证既有四个标签索引不变，串口助手固定追加在索引 4。
        /// </summary>
        [Test]
        public void MainWindowXaml_AppendsSerialAssistantAtIndexFour()
        {
            string repositoryRoot = FindRepositoryRoot(TestContext.CurrentContext.TestDirectory);
            string xamlPath = Path.Combine(
                repositoryRoot,
                "src",
                "CH32UpperComputer.App",
                "MainWindow.xaml");
            string xaml = File.ReadAllText(xamlPath);
            string[] expectedHeaders =
            [
                "实时监控",
                "参数设置",
                "通信日志",
                "固件升级",
                "串口助手",
            ];

            int previousIndex = -1;

            foreach (string header in expectedHeaders)
            {
                int currentIndex = xaml.IndexOf(
                    $"Header=\"{header}\"",
                    StringComparison.Ordinal);
                Assert.That(currentIndex, Is.GreaterThan(previousIndex));
                previousIndex = currentIndex;
            }

            Assert.That(
                xaml,
                Does.Contain("<views:SerialAssistantView DataContext=\"{Binding SerialAssistant}\" />"));
        }

        /// <summary>
        /// 从测试输出目录向上查找包含解决方案文件的仓库根目录。
        /// </summary>
        /// <param name="startDirectory">开始向父级搜索的现有目录。</param>
        /// <returns>包含解决方案文件的绝对仓库根目录。</returns>
        private static string FindRepositoryRoot(string startDirectory)
        {
            DirectoryInfo? directory = new(startDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CH32UpperComputer.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("未找到 CH32UpperComputer.sln 所在仓库根目录。");
        }
    }
}
