using System.IO;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证任务五质量审查发现的流结束和模拟设备地址边界行为。
    /// </summary>
    [TestFixture]
    public sealed class Task5QualityRegressionTests
    {
        /// <summary>
        /// 单项异步操作允许等待的最大保险时长；该时长只用于识别死锁，不用于驱动业务时序。
        /// </summary>
        private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 验证流结束一经排入即不可撤销，后续读取恒为零且生产方不能再排入任何指令。
        /// </summary>
        [Test]
        public async Task QueueEndOfStream_ShouldBePersistentAndRejectLaterDirectives()
        {
            await using ControllableDuplexStream stream = new();
            byte[] buffer = new byte[8];

            stream.QueueEndOfStream();

            Assert.Multiple((Action)(() =>
            {
                Assert.Throws<InvalidOperationException>(
                    (Action)(() => stream.QueueRead(new byte[] { 0x01 })));
                Assert.Throws<InvalidOperationException>(
                    (Action)(() => stream.QueueIOException(new IOException("不应被排入"))));
                Assert.Throws<InvalidOperationException>(
                    (Action)stream.QueueEndOfStream);
            }));

            int firstRead = await stream
                .ReadAsync(buffer)
                .AsTask()
                .WaitAsync(DeadlockGuard);
            int secondRead = await stream
                .ReadAsync(buffer)
                .AsTask()
                .WaitAsync(DeadlockGuard);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(firstRead, Is.Zero);
                Assert.That(secondRead, Is.Zero);
            }));
        }

        /// <summary>
        /// 验证错误普通从站地址被静默忽略，既不响应也不执行写寄存器副作用。
        /// </summary>
        [Test]
        public async Task WrongOrdinarySlaveAddress_ShouldBeSilentAndNotApplyWrite()
        {
            await using FakeSerialTransport transport = await OpenTransportAsync();
            SimulatedModbusDevice device = CreateDevice(transport);
            device.SetRegister(0x0001, 0x1111);
            await using IAsyncEnumerator<SerialReceiveChunk> reader = transport
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator();
            ValueTask<bool> pendingRead = reader.MoveNextAsync();
            ModbusRequest wrongAddressWrite = ModbusRequestFactory.CreateWriteSingleRegister(
                2,
                0x0001,
                0x2222);

            await device.HandleWriteAsync(wrongAddressWrite.RawFrame);
            await transport.CloseAsync(CancellationToken.None);
            bool received = await pendingRead.AsTask().WaitAsync(DeadlockGuard);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(received, Is.False);
                Assert.That(device.GetRegister(0x0001), Is.EqualTo(0x1111));
                Assert.That(device.SlaveAddress, Is.EqualTo(1));
            }));
        }

        /// <summary>
        /// 验证 0x06 与覆盖地址寄存器的 0x10 都先使用旧地址完成回显，再切换并只接受新地址。
        /// </summary>
        /// <param name="writeMultipleRegisters">为 <see langword="true"/> 时使用覆盖地址寄存器的 0x10 请求。</param>
        [TestCase(false)]
        [TestCase(true)]
        public async Task AddressWrite_ShouldEchoOldAddressBeforeUsingNewAddress(
            bool writeMultipleRegisters)
        {
            await using FakeSerialTransport transport = await OpenTransportAsync();
            SimulatedModbusDevice device = CreateDevice(transport);
            await using IAsyncEnumerator<SerialReceiveChunk> reader = transport
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator();
            ModbusRequest addressWrite = writeMultipleRegisters
                ? ModbusRequestFactory.CreateWriteMultipleRegisters(
                    1,
                    0x0000,
                    new ushort[] { 5, 0x2222 })
                : ModbusRequestFactory.CreateWriteSingleRegister(
                    1,
                    0x0000,
                    5);

            await device.HandleWriteAsync(addressWrite.RawFrame);
            SerialReceiveChunk writeResponse = await ReadNextChunkAsync(reader);

            ModbusRequest newAddressRead = ModbusRequestFactory.CreateReadHoldingRegisters(
                5,
                0x0000,
                1);
            await device.HandleWriteAsync(newAddressRead.RawFrame);
            SerialReceiveChunk newAddressResponse = await ReadNextChunkAsync(reader);

            ValueTask<bool> pendingOldAddressRead = reader.MoveNextAsync();
            ModbusRequest oldAddressRead = ModbusRequestFactory.CreateReadHoldingRegisters(
                1,
                0x0000,
                1);
            await device.HandleWriteAsync(oldAddressRead.RawFrame);
            await transport.CloseAsync(CancellationToken.None);
            bool oldAddressReceived = await pendingOldAddressRead
                .AsTask()
                .WaitAsync(DeadlockGuard);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(writeResponse.Data.Span[0], Is.EqualTo(1));
                Assert.That(newAddressResponse.Data.Span[0], Is.EqualTo(5));
                Assert.That(device.SlaveAddress, Is.EqualTo(5));
                Assert.That(device.GetRegister(0x0000), Is.EqualTo(5));
                Assert.That(
                    device.GetRegister(0x0001),
                    Is.EqualTo(writeMultipleRegisters ? 0x2222 : 0));
                Assert.That(oldAddressReceived, Is.False);
            }));
        }

        /// <summary>
        /// 验证无响应脚本仅丢失响应，合法 0x06 与 0x10 写请求仍执行并完成从站地址切换。
        /// </summary>
        /// <param name="writeMultipleRegisters">为 <see langword="true"/> 时使用覆盖地址寄存器的 0x10 请求。</param>
        [TestCase(false)]
        [TestCase(true)]
        public async Task NoResponseAddressWrite_ShouldApplyWriteWithoutInjectingResponse(
            bool writeMultipleRegisters)
        {
            await using FakeSerialTransport transport = await OpenTransportAsync();
            SimulatedModbusDevice device = CreateDevice(transport);
            await using IAsyncEnumerator<SerialReceiveChunk> reader = transport
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator();
            ValueTask<bool> pendingRead = reader.MoveNextAsync();
            ModbusRequest addressWrite = writeMultipleRegisters
                ? ModbusRequestFactory.CreateWriteMultipleRegisters(
                    1,
                    0x0000,
                    new ushort[] { 7, 0x3333 })
                : ModbusRequestFactory.CreateWriteSingleRegister(
                    1,
                    0x0000,
                    7);

            await device.HandleWriteAsync(
                addressWrite.RawFrame,
                SimulatedModbusBehavior.NoResponse);
            await transport.CloseAsync(CancellationToken.None);
            bool received = await pendingRead.AsTask().WaitAsync(DeadlockGuard);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(received, Is.False);
                Assert.That(device.SlaveAddress, Is.EqualTo(7));
                Assert.That(device.GetRegister(0x0000), Is.EqualTo(7));
                Assert.That(
                    device.GetRegister(0x0001),
                    Is.EqualTo(writeMultipleRegisters ? 0x3333 : 0));
            }));
        }

        /// <summary>
        /// 验证无效地址值产生标准非法数据值异常，且单写与多写均不修改任何设备状态。
        /// </summary>
        [Test]
        public async Task InvalidAddressValue_ShouldReturnExceptionAndKeepWritesAtomic()
        {
            await using FakeSerialTransport transport = await OpenTransportAsync();
            SimulatedModbusDevice device = CreateDevice(transport);
            device.SetRegister(0x0000, 1);
            device.SetRegister(0x0001, 0x1111);
            await using IAsyncEnumerator<SerialReceiveChunk> reader = transport
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator();

            ModbusRequest invalidSingle = ModbusRequestFactory.CreateWriteSingleRegister(
                1,
                0x0000,
                0);
            await device.HandleWriteAsync(invalidSingle.RawFrame);
            SerialReceiveChunk singleResponse = await ReadNextChunkAsync(reader);

            ushort[] invalidMultipleValues = [65, 0x2222];
            ModbusRequest invalidMultiple = ModbusRequestFactory.CreateWriteMultipleRegisters(
                1,
                0x0000,
                invalidMultipleValues);
            await device.HandleWriteAsync(invalidMultiple.RawFrame);
            SerialReceiveChunk multipleResponse = await ReadNextChunkAsync(reader);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(singleResponse.Data.ToArray()[..3], Is.EqualTo(new byte[] { 1, 0x86, 0x03 }));
                Assert.That(multipleResponse.Data.ToArray()[..3], Is.EqualTo(new byte[] { 1, 0x90, 0x03 }));
                Assert.That(device.SlaveAddress, Is.EqualTo(1));
                Assert.That(device.GetRegister(0x0000), Is.EqualTo(1));
                Assert.That(device.GetRegister(0x0001), Is.EqualTo(0x1111));
            }));
        }

        /// <summary>
        /// 创建并打开用于单项回归的内存串口传输。
        /// </summary>
        /// <returns>已经打开且由调用方异步释放的模拟传输。</returns>
        private static async Task<FakeSerialTransport> OpenTransportAsync()
        {
            FakeSerialTransport transport = new();
            await transport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            return transport;
        }

        /// <summary>
        /// 创建无需真实等待的模拟 Modbus 设备。
        /// </summary>
        /// <param name="transport">接收设备响应的已打开模拟传输。</param>
        /// <returns>响应延迟同步完成的模拟设备。</returns>
        private static SimulatedModbusDevice CreateDevice(FakeSerialTransport transport)
        {
            return new SimulatedModbusDevice(
                transport,
                (_, _) => ValueTask.CompletedTask);
        }

        /// <summary>
        /// 读取下一项模拟串口接收块，并使用超时仅防止缺陷导致测试永久挂起。
        /// </summary>
        /// <param name="reader">已经绑定当前模拟串口会话的异步读取器。</param>
        /// <returns>下一项确定性注入的接收块。</returns>
        private static async Task<SerialReceiveChunk> ReadNextChunkAsync(
            IAsyncEnumerator<SerialReceiveChunk> reader)
        {
            bool moved = await reader
                .MoveNextAsync()
                .AsTask()
                .WaitAsync(DeadlockGuard);
            Assert.That(moved, Is.True);
            return reader.Current;
        }
    }
}
