using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 验证进程内串口占用登记的互斥、大小写规则和租约生命周期。
    /// </summary>
    [TestFixture]
    public sealed class SerialPortUsageRegistryTests
    {
        /// <summary>
        /// 验证同一端口的不同大小写名称仍被识别为同一物理资源。
        /// </summary>
        [Test]
        public void Acquire_WithCaseInsensitiveDuplicate_ReportsCurrentOwnerInChinese()
        {
            SerialPortUsageRegistry registry = new();
            using SerialPortLease firstLease = registry.Acquire("COM7", "Modbus 串口");

            SerialPortInUseException? exception = Assert.Throws<SerialPortInUseException>(
                (Action)(() =>
                {
                    _ = registry.Acquire("com7", "串口助手");
                }));

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(exception!.PortName, Is.EqualTo("com7"));
                    Assert.That(exception.CurrentOwner, Is.EqualTo("Modbus 串口"));
                    Assert.That(exception.Message, Does.Contain("COM7").IgnoreCase);
                    Assert.That(exception.Message, Does.Contain("Modbus 串口"));
                }));
        }

        /// <summary>
        /// 验证租约释放后另一页面可以取得同一端口。
        /// </summary>
        [Test]
        public void DisposeLease_ReleasesPortForAnotherOwner()
        {
            SerialPortUsageRegistry registry = new();
            SerialPortLease firstLease = registry.Acquire("COM8", "Modbus 串口");

            firstLease.Dispose();

            using SerialPortLease secondLease = registry.Acquire("com8", "串口助手");
            Assert.That(secondLease.OwnerName, Is.EqualTo("串口助手"));
        }

        /// <summary>
        /// 验证两个并发打开尝试中只会有一个取得租约。
        /// </summary>
        [Test]
        public async Task Acquire_FromConcurrentCallers_AllowsExactlyOneOwner()
        {
            SerialPortUsageRegistry registry = new();
            Barrier barrier = new(2);
            List<SerialPortLease> leases = [];
            List<Exception> failures = [];
            object resultSyncRoot = new();

            string[] ownerNames = ["Modbus 串口", "串口助手"];
            Task[] attempts = ownerNames
                .Select(
                    owner => Task.Run(
                        () =>
                        {
                            barrier.SignalAndWait();

                            try
                            {
                                SerialPortLease lease = registry.Acquire("COM10", owner);

                                lock (resultSyncRoot)
                                {
                                    leases.Add(lease);
                                }
                            }
                            catch (Exception exception)
                            {
                                lock (resultSyncRoot)
                                {
                                    failures.Add(exception);
                                }
                            }
                        }))
                .ToArray();

            await Task.WhenAll(attempts);

            try
            {
                Assert.Multiple(
                    (Action)(() =>
                    {
                        Assert.That(leases, Has.Count.EqualTo(1));
                        Assert.That(failures, Has.Count.EqualTo(1));
                        Assert.That(failures[0], Is.TypeOf<SerialPortInUseException>());
                    }));
            }
            finally
            {
                foreach (SerialPortLease lease in leases)
                {
                    lease.Dispose();
                }

                barrier.Dispose();
            }
        }
    }
}
