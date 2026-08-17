using System.IO.Ports;
using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证通用串口线路参数的固定能力边界。
    /// </summary>
    [TestFixture]
    public sealed class SerialLineSettingsTests
    {
        /// <summary>
        /// 验证串口助手所需的 115200-8-N-1 能通过通用线路校验。
        /// </summary>
        [Test]
        public void Constructor_With115200Baud_CreatesValidatedSettings()
        {
            SerialLineSettings settings = new(
                " COM9 ",
                115200,
                8,
                Parity.None,
                StopBits.One);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(settings.PortName, Is.EqualTo("COM9"));
                    Assert.That(settings.BaudRate, Is.EqualTo(115200));
                    Assert.That(settings.DataBits, Is.EqualTo(8));
                    Assert.That(settings.Parity, Is.EqualTo(Parity.None));
                    Assert.That(settings.StopBits, Is.EqualTo(StopBits.One));
                }));
        }

        /// <summary>
        /// 验证固定集合以外的波特率会在触达驱动前被拒绝。
        /// </summary>
        [Test]
        public void Constructor_WithUnsupportedBaud_ThrowsArgumentOutOfRangeException()
        {
            Assert.That(
                (Action)(() =>
                {
                    _ = new SerialLineSettings(
                    "COM9",
                    230400,
                    8,
                    Parity.None,
                    StopBits.One);
                }),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        /// <summary>
        /// 验证 Modbus 专用设置仍拒绝其既有六档范围以外的 115200。
        /// </summary>
        [Test]
        public void ModbusSettings_With115200Baud_RemainsRejected()
        {
            Assert.That(
                (Action)(() =>
                {
                    _ = new SerialSettings(
                    "COM9",
                    115200,
                    8,
                    Parity.None,
                    StopBits.One,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(20));
                }),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
