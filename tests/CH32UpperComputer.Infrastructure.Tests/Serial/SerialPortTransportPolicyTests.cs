using CH32UpperComputer.Infrastructure.Serial;

namespace CH32UpperComputer.Infrastructure.Tests.Serial
{
    /// <summary>
    /// 通过源代码架构约束防止生产串口重新混入第二种读取模型或轮询路径。
    /// </summary>
    [TestFixture]
    public sealed class SerialPortTransportPolicyTests
    {
        /// <summary>
        /// 验证生产传输只经基础流和唯一异步读取泵接收数据。
        /// </summary>
        [Test]
        public void Source_UsesOnlyBaseStreamReadPumpPolicy()
        {
            string repositoryRoot = FindRepositoryRoot(TestContext.CurrentContext.TestDirectory);
            string sourcePath = Path.Combine(
                repositoryRoot,
                "src",
                "CH32UpperComputer.Infrastructure",
                "Serial",
                "SerialPortTransport.cs");
            string source = File.ReadAllText(sourcePath);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(source, Does.Contain("serialPort.BaseStream"));
                Assert.That(source, Does.Contain("SerialReadPump.RunAsync"));
                Assert.That(source, Does.Contain("session.StartPhysicalCleanup()"));
                Assert.That(source, Does.Contain("session.DisposeCancellation();"));
                Assert.That(source, Does.Contain("session.DisposePortLease();"));
                Assert.That(source, Does.Contain("Task.Run(DisposePhysicalResourcesCore)"));
                Assert.That(source, Does.Contain("ForegroundCloseTimeout"));
                Assert.That(source, Does.Not.Contain(".DataReceived +="));
                Assert.That(source, Does.Not.Contain("BytesToRead"));
                Assert.That(source, Does.Not.Contain("BaseStream.Read("));
                Assert.That(source, Does.Not.Contain("serialPort.Read("));
                Assert.That(source, Does.Not.Contain("BaseStream.ReadByte("));
                Assert.That(source, Does.Not.Contain("serialPort.ReadByte("));
            }));
        }

        /// <summary>
        /// 验证端口租约晚于物理清理完成、且早于向界面发布清理完成状态释放。
        /// </summary>
        [Test]
        public void Source_ReleasesPortLeaseAfterPhysicalCleanupBeforeClearingPendingState()
        {
            string repositoryRoot = FindRepositoryRoot(TestContext.CurrentContext.TestDirectory);
            string sourcePath = Path.Combine(
                repositoryRoot,
                "src",
                "CH32UpperComputer.Infrastructure",
                "Serial",
                "SerialPortTransport.cs");
            string source = File.ReadAllText(sourcePath);
            int awaitedCleanup = source.IndexOf(
                "await physicalCleanup.ConfigureAwait(false);",
                StringComparison.Ordinal);
            int leaseRelease = source.IndexOf(
                "session.DisposePortLease();",
                awaitedCleanup,
                StringComparison.Ordinal);
            int pendingCleared = source.IndexOf(
                "SetCleanupPending(false);",
                leaseRelease,
                StringComparison.Ordinal);

            Assert.Multiple(
                (Action)(() =>
                {
                    Assert.That(awaitedCleanup, Is.GreaterThanOrEqualTo(0));
                    Assert.That(leaseRelease, Is.GreaterThan(awaitedCleanup));
                    Assert.That(pendingCleared, Is.GreaterThan(leaseRelease));
                }));
        }

        /// <summary>
        /// 验证读取泵源代码只使用内存异步读取，并固定采用 512 字节数组池缓冲。
        /// </summary>
        [Test]
        public void ReadPumpSource_UsesReadAsyncAndFixedPooledBuffer()
        {
            string repositoryRoot = FindRepositoryRoot(TestContext.CurrentContext.TestDirectory);
            string sourcePath = Path.Combine(
                repositoryRoot,
                "src",
                "CH32UpperComputer.Infrastructure",
                "Serial",
                "SerialReadPump.cs");
            string source = File.ReadAllText(sourcePath);

            Assert.Multiple((Action)(() =>
            {
                Assert.That(source, Does.Contain("ReadAsync("));
                Assert.That(source, Does.Contain("ArrayPool<byte>.Shared.Rent"));
                Assert.That(source, Does.Contain("ReadBufferBytes = 512"));
                Assert.That(source, Does.Not.Contain(".Read("));
                Assert.That(source, Does.Not.Contain(".ReadByte("));
            }));
        }

        /// <summary>
        /// 从测试输出目录向上查找包含解决方案文件的仓库根目录。
        /// </summary>
        /// <param name="startDirectory">开始向父级搜索的现有目录。</param>
        /// <returns>包含解决方案文件的绝对仓库根目录。</returns>
        /// <exception cref="DirectoryNotFoundException">遍历至文件系统根仍未找到仓库时抛出。</exception>
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
