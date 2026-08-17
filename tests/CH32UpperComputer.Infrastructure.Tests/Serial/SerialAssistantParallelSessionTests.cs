using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证普通串口助手与 Modbus 使用两套独立传输时不会串流或互相断开。
    /// </summary>
    [TestFixture]
    public sealed class SerialAssistantParallelSessionTests
    {
        /// <summary>
        /// 验证两套独立端口可以同时打开、各自收发并单独断开。
        /// </summary>
        [Test]
        public async Task DifferentPorts_CanRunInParallelWithoutCrossTalk()
        {
            ManualTimeProvider timeProvider = new();
            FakeSerialTransport modbusTransport = new(timeProvider);
            FakeSerialTransport assistantTransport = new(timeProvider);
            await modbusTransport.OpenAsync(
                SerialSettings.CreateDefault("COM_MODBUS"),
                CancellationToken.None);
            await using SerialAssistantSessionService assistantService = new(
                assistantTransport,
                timeProvider);
            await assistantService.ConnectAsync(
                SerialLineSettings.CreateAssistantDefault("COM_ASSISTANT"),
                CancellationToken.None);

            await assistantService.SendAsync(
                new byte[] { 0xAA },
                CancellationToken.None);
            await modbusTransport.WriteAsync(
                new byte[] { 0x01, 0x02 },
                static () => true,
                CancellationToken.None);
            await assistantService.DisconnectAsync(CancellationToken.None);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(modbusTransport.IsOpen, Is.True);
                    Assert.That(modbusTransport.WrittenFrames.Single().ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02 }));
                    Assert.That(assistantTransport.WrittenFrames.Single().ToArray(), Is.EqualTo(new byte[] { 0xAA }));
                    Assert.That(assistantTransport.IsOpen, Is.False);
                }));

            await modbusTransport.DisposeAsync();
        }
    }
}
