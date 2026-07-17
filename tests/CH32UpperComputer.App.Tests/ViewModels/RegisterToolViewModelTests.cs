using CH32UpperComputer.App.ViewModels;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证专家结果始终同时保留 Signed、Unsigned、Hex 三种原始视图。
    /// </summary>
    [TestFixture]
    public sealed class RegisterToolViewModelTests
    {
        /// <summary>
        /// 验证同一 0xFFFF 原始字不会被全局 int16 规则覆盖无符号和十六进制事实。
        /// </summary>
        [Test]
        public void ResultRow_ExposesSignedUnsignedAndHexTogether()
        {
            RegisterResultRowViewModel row = new(40003, 0x0002, 0xFFFF, "温度：-0.01 ℃");

            Assert.Multiple((Action)(() =>
            {
                Assert.That(row.SignedValue, Is.EqualTo("-1"));
                Assert.That(row.UnsignedValue, Is.EqualTo("65535"));
                Assert.That(row.HexValue, Is.EqualTo("0xFFFF"));
                Assert.That(row.SemanticValue, Does.Contain("温度"));
            }));
        }
    }
}
