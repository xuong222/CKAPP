using CH32UpperComputer.App.Services;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Core.Registers;
using CH32UpperComputer.Infrastructure.Coordination;
using CH32UpperComputer.Infrastructure.Iap;
using CH32UpperComputer.Infrastructure.Logging;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.App.Tests.TestSupport
{
    /// <summary>
    /// 为应用层 ViewModel 测试构造完整 Fake 串口、协调器、日志、快照和服务依赖。
    /// </summary>
    internal sealed class AppViewModelHarness : IAsyncDisposable
    {
        /// <summary>
        /// 防止测试主动关闭与 <see cref="DisposeAsync"/> 重复执行运行时退出序列。
        /// </summary>
        private int shutdownStarted;

        /// <summary>
        /// 初始化完整应用层测试依赖。
        /// </summary>
        /// <param name="settings">应用层 ViewModel 使用的安全初始设置。</param>
        private AppViewModelHarness(AppSettings settings)
        {
            TimeProvider = new ManualTimeProvider();
            Transport = new FakeSerialTransport(TimeProvider);
            AssistantTransport = new FakeSerialTransport(TimeProvider);
            ApplicationOperationGate = new ApplicationOperationGate();
            Coordinator = new ModbusTransactionCoordinator(
                Transport,
                TimeProvider,
                ApplicationOperationGate);
            Periodic = new PeriodicSendService(
                Coordinator,
                TimeProvider,
                ApplicationOperationGate);
            LogService = new CommunicationLogService(TimeProvider);
            Dispatcher = new ImmediateUiDispatcher();
            PortDiscovery = new FakeSerialPortDiscovery(
                new[]
                {
                    new SerialPortDescriptor(
                        "COM_TEST",
                        "模拟串口 (COM_TEST)",
                        null,
                        false),
                });
            DeviceSnapshot snapshot = new();
            OperationService = new ModbusOperationService(
                Coordinator,
                Transport,
                snapshot,
                LogService,
                TimeProvider);
            SerialConnection = new SerialConnectionViewModel(
                Transport,
                Coordinator,
                PortDiscovery,
                Dispatcher,
                settings,
                ApplicationOperationGate);
            SerialAssistantService = new SerialAssistantSessionService(
                AssistantTransport,
                TimeProvider);
            SerialAssistant = new SerialAssistantViewModel(
                SerialAssistantService,
                PortDiscovery,
                Dispatcher,
                settings.SerialAssistant);
            SpecialConfiguration = new SpecialConfigurationService(
                Coordinator,
                Periodic,
                Transport,
                checked((byte)settings.SlaveAddress),
                SerialConnection.CreateSerialSettings());
            SpecialConfiguration.TransactionRecorded +=
                OperationService.RecordExternalResult;
            CommandConsole = new CommandConsoleViewModel(
                OperationService,
                Periodic,
                SerialConnection,
                Dispatcher,
                settings,
                ApplicationOperationGate);
            Monitor = new MonitorViewModel(OperationService, Dispatcher);
            Parameters = new ParametersViewModel(
                CommandConsole,
                SerialConnection,
                SpecialConfiguration,
                OperationService,
                Dispatcher,
                ApplicationOperationGate);
            CommunicationLog = new CommunicationLogViewModel(
                LogService,
                Dispatcher,
                TimeProvider,
                string.Empty);
            IapLogService = new IapCommunicationLogService(TimeProvider);
            IapTransport = new TcpIapTransport(TimeProvider);
            IapProtocolClient = new IapProtocolClient(
                IapTransport,
                IapLogService,
                TimeProvider);
            IapCoordinator = new IapUpgradeCoordinator(
                IapProtocolClient,
                IapLogService,
                ApplicationOperationGate,
                TimeProvider);
            FirmwareUpgrade = new FirmwareUpgradeViewModel(
                new FirmwareFileService(),
                IapCoordinator,
                IapLogService,
                Periodic,
                ApplicationOperationGate,
                Dispatcher,
                TimeProvider,
                settings);
            Device = new SimulatedModbusDevice(Transport, TimeProvider);
        }

        /// <summary>
        /// 获取手动推进的统一测试时间源。
        /// </summary>
        internal ManualTimeProvider TimeProvider { get; }

        /// <summary>
        /// 获取可注入响应且记录完整写帧的模拟串口。
        /// </summary>
        internal FakeSerialTransport Transport { get; }

        /// <summary>
        /// 获取与 Modbus 模拟传输完全独立的串口助手模拟传输。
        /// </summary>
        internal FakeSerialTransport AssistantTransport { get; }

        /// <summary>
        /// 获取测试环境共享的 Modbus/IAP 应用操作门。
        /// </summary>
        internal ApplicationOperationGate ApplicationOperationGate { get; }

        /// <summary>
        /// 获取无队列事务协调器。
        /// </summary>
        internal ModbusTransactionCoordinator Coordinator { get; }

        /// <summary>
        /// 获取默认关闭的定时发送服务。
        /// </summary>
        internal PeriodicSendService Periodic { get; }

        /// <summary>
        /// 获取有界通信日志服务。
        /// </summary>
        internal CommunicationLogService LogService { get; }

        /// <summary>
        /// 获取同步测试调度器。
        /// </summary>
        internal ImmediateUiDispatcher Dispatcher { get; }

        /// <summary>
        /// 获取不访问操作系统设备管理器的模拟串口发现服务。
        /// </summary>
        internal FakeSerialPortDiscovery PortDiscovery { get; }

        /// <summary>
        /// 获取统一事务、快照、日志和统计服务。
        /// </summary>
        internal ModbusOperationService OperationService { get; }

        /// <summary>
        /// 获取串口连接 ViewModel。
        /// </summary>
        internal SerialConnectionViewModel SerialConnection { get; }

        /// <summary>
        /// 获取独占第二套模拟传输的助手会话服务。
        /// </summary>
        internal SerialAssistantSessionService SerialAssistantService { get; }

        /// <summary>
        /// 获取普通串口助手 ViewModel。
        /// </summary>
        internal SerialAssistantViewModel SerialAssistant { get; }

        /// <summary>
        /// 获取紧凑收发区 ViewModel。
        /// </summary>
        internal CommandConsoleViewModel CommandConsole { get; }

        /// <summary>
        /// 获取监控页 ViewModel。
        /// </summary>
        internal MonitorViewModel Monitor { get; }

        /// <summary>
        /// 获取参数页 ViewModel。
        /// </summary>
        internal ParametersViewModel Parameters { get; }

        /// <summary>
        /// 获取与实时监控“数据收发”区域共用的通信日志投影。
        /// </summary>
        internal CommunicationLogViewModel CommunicationLog { get; }

        /// <summary>
        /// 获取独立 IAP 日志服务。
        /// </summary>
        internal IapCommunicationLogService IapLogService { get; }

        /// <summary>
        /// 获取测试使用的未主动联网 TCP IAP 传输。
        /// </summary>
        internal TcpIapTransport IapTransport { get; }

        /// <summary>
        /// 获取 IAP 顺序协议客户端。
        /// </summary>
        internal IapProtocolClient IapProtocolClient { get; }

        /// <summary>
        /// 获取完整 IAP 升级协调器。
        /// </summary>
        internal IapUpgradeCoordinator IapCoordinator { get; }

        /// <summary>
        /// 获取固件升级页面 ViewModel。
        /// </summary>
        internal FirmwareUpgradeViewModel FirmwareUpgrade { get; }

        /// <summary>
        /// 获取安全配置服务。
        /// </summary>
        internal SpecialConfigurationService SpecialConfiguration { get; }

        /// <summary>
        /// 获取可脚本化模拟 Modbus 设备。
        /// </summary>
        internal SimulatedModbusDevice Device { get; }

        /// <summary>
        /// 创建采用 COM_TEST、9600-8-N-1 和关闭自动发送的测试依赖。
        /// </summary>
        /// <returns>尚未连接且没有任何线路写入的新测试环境。</returns>
        internal static AppViewModelHarness Create()
        {
            AppSettings settings = new()
            {
                PortName = "COM_TEST",
                BaudRate = 9600,
                DataBits = 8,
                Parity = System.IO.Ports.Parity.None,
                StopBits = System.IO.Ports.StopBits.One,
                SlaveAddress = 1,
                ResponseTimeoutMilliseconds = 1000,
                RawInterByteTimeoutMilliseconds = 20,
                PeriodicIntervalMilliseconds = 1000,
                AutoAppendCrc = true,
                AutomaticSendOnConnect = false,
                PeriodicSendEnabled = false,
                SerialAssistant = new SerialAssistantPreferences
                {
                    PortName = "COM_ASSISTANT",
                },
            };
            return new AppViewModelHarness(settings);
        }

        /// <summary>
        /// 通过串口 ViewModel 连接模拟传输并启动唯一接收循环。
        /// </summary>
        /// <returns>连接命令完成后的任务。</returns>
        internal async Task ConnectAsync()
        {
            await SerialConnection.ConnectCommand.ExecuteAsync(null).ConfigureAwait(false);
        }

        /// <summary>
        /// 等待下一帧写入模拟串口，再让模拟设备生成对应响应。
        /// </summary>
        /// <param name="expectedWriteCount">等待写历史达到的数量。</param>
        /// <returns>模拟响应注入完成后的任务。</returns>
        internal async Task RespondToWriteAsync(int expectedWriteCount)
        {
            ReadOnlyMemory<byte> frame = await WaitForWriteAsync(expectedWriteCount)
                .ConfigureAwait(false);
            await Device.HandleWriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// 等待模拟串口写历史达到指定数量并返回对应完整帧。
        /// </summary>
        /// <param name="expectedWriteCount">从一开始计数的目标写帧数量。</param>
        /// <returns>目标位置完整写帧的防御性副本。</returns>
        internal async Task<ReadOnlyMemory<byte>> WaitForWriteAsync(int expectedWriteCount)
        {
            if (expectedWriteCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedWriteCount));
            }

            await WaitUntilAsync(
                () => Transport.WrittenFrames.Count >= expectedWriteCount,
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await WaitUntilAsync(
                () => Coordinator.State == TransactionCoordinatorState.WaitingResponse,
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return Transport.WrittenFrames[expectedWriteCount - 1];
        }

        /// <summary>
        /// 等待协调器完成指定接收块序号的组帧、匹配和诊断路由。
        /// </summary>
        /// <param name="minimumSequence">必须已经处理完成的最小接收块序号。</param>
        /// <returns>目标序号完成处理后的任务。</returns>
        internal async Task WaitForReceiveSequenceAsync(long minimumSequence)
        {
            if (minimumSequence <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumSequence));
            }

            await WaitUntilAsync(
                () => Coordinator.LastProcessedReceiveSequence >= minimumSequence,
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        /// <summary>
        /// 按生产组合根顺序停止定时发送、完成活动事务、退出接收循环并永久释放传输。
        /// </summary>
        /// <returns>整个退出序列首次执行完成后的任务；重复调用立即返回。</returns>
        internal async Task ShutdownRuntimeAsync()
        {
            if (Interlocked.CompareExchange(ref shutdownStarted, 1, 0) != 0)
            {
                return;
            }

            await Periodic.StopAsync(CancellationToken.None).ConfigureAwait(false);
            SerialAssistant.BeginShutdown();
            await SerialAssistantService.StopPeriodicSendingAsync().ConfigureAwait(false);
            await SerialAssistantService.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await IapCoordinator.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            await Coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Coordinator.WaitForBackgroundOperationsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            await OperationService.WaitForOperationsAsync(CancellationToken.None)
                .ConfigureAwait(false);
            CommunicationLog.Dispose();
            SerialAssistant.Dispose();
            FirmwareUpgrade.Dispose();
            Parameters.Dispose();
            Monitor.Dispose();
            CommandConsole.Dispose();
            SerialConnection.Dispose();
            OperationService.Dispose();
            await Periodic.DisposeAsync().ConfigureAwait(false);
            await Coordinator.DisposeAsync().ConfigureAwait(false);
            await Transport.DisposeAsync().ConfigureAwait(false);
            await SerialAssistantService.DisposeAsync().ConfigureAwait(false);
            await IapCoordinator.DisposeAsync().ConfigureAwait(false);
            LogService.Dispose();
        }

        /// <summary>
        /// 释放所有 ViewModel、定时服务、协调器、模拟串口和日志服务。
        /// </summary>
        /// <returns>全部后台读取与调度任务退出后的任务。</returns>
        public async ValueTask DisposeAsync()
        {
            await ShutdownRuntimeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 在短超时内轮询一个只依赖内存状态的条件。
        /// </summary>
        /// <param name="condition">成功时返回真的无副作用条件。</param>
        /// <param name="timeout">允许等待的最长真实时间，仅用于测试协调。</param>
        /// <returns>条件成立后的任务。</returns>
        /// <exception cref="TimeoutException">超时内条件仍未成立时抛出。</exception>
        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

            while (!condition())
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException("等待模拟串口状态超时。");
                }

                await Task.Yield();
            }
        }
    }
}
