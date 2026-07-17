using CH32UpperComputer.App.Tests.TestSupport;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证参数页整组预校验在任一无效值存在时保持零写入。
    /// </summary>
    [TestFixture]
    public sealed class ParametersViewModelTests
    {
        /// <summary>
        /// 验证连续组中任一输入无效时不构建或写入 0x10 请求。
        /// </summary>
        [Test]
        public async Task SaveGroup_InvalidItem_PerformsZeroWrites()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            harness.Parameters.SelectedGroup = harness.Parameters.ParameterGroups[0];
            harness.Parameters.SelectedGroup.Items[0].InputText = "不是数字";

            await harness.Parameters.SaveSelectedGroupCommand.ExecuteAsync(null);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(harness.Transport.WrittenFrames, Is.Empty);
                Assert.That(harness.Parameters.StatusMessage, Does.StartWith("整组预校验失败"));
            }));
        }
    }
}
