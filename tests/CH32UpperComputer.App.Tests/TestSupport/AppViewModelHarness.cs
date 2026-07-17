using CH32UpperComputer.App.Services;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Core.Registers;
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
        /// 初始化完整应用层测试依赖。
        /// </summary>
        /// <param name="settings">应用层 ViewModel 使用的安全初始设置。</param>
        private AppViewModelHarness(AppSettings settings)
        {
            TimeProvider = new ManualTimeProvider();
            Transport = new FakeSerialTransport(TimeProvider);
            Coordinator = new ModbusTransactionCoordinator(Transport, TimeProvider);
            Periodic = new PeriodicSendService(Coordinator, TimeProvider);
            LogService = new CommunicationLogService(TimeProvider);
            Dispatcher = new ImmediateUiDispatcher();
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
                Dispatcher,
                settings);
            SpecialConfiguration = new SpecialConfigurationService(
                Coordinator,
                Periodic,
                Transport,
                checked((byte)settings.SlaveAddress),
                SerialConnection.CreateSerialSettings());
            CommandConsole = new CommandConsoleViewModel(
                OperationService,
                Periodic,
                SerialConnection,
                Dispatcher,
                settings);
            Monitor = new MonitorViewModel(OperationService, Dispatcher);
            Parameters = new ParametersViewModel(
                CommandConsole,
                SerialConnection,
                SpecialConfiguration,
                OperationService,
                Dispatcher);
            RegisterTool = new RegisterToolViewModel(
                CommandConsole,
                SerialConnection,
                SpecialConfiguration,
                OperationService,
                Dispatcher);
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
        /// 获取统一事务、快照、日志和统计服务。
        /// </summary>
        internal ModbusOperationService OperationService { get; }

        /// <summary>
        /// 获取串口连接 ViewModel。
        /// </summary>
        internal SerialConnectionViewModel SerialConnection { get; }

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
        /// 获取专家寄存器工具 ViewModel。
        /// </summary>
        internal RegisterToolViewModel RegisterTool { get; }

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
            await WaitUntilAsync(
                () => Transport.WrittenFrames.Count >= expectedWriteCount,
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            ReadOnlyMemory<byte> frame = Transport.WrittenFrames[expectedWriteCount - 1];
            await Device.HandleWriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// 释放所有 ViewModel、定时服务、协调器、模拟串口和日志服务。
        /// </summary>
        /// <returns>全部后台读取与调度任务退出后的任务。</returns>
        public async ValueTask DisposeAsync()
        {
            RegisterTool.Dispose();
            Parameters.Dispose();
            Monitor.Dispose();
            CommandConsole.Dispose();
            SerialConnection.Dispose();
            await Periodic.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Coordinator.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await Periodic.DisposeAsync().ConfigureAwait(false);
            await Coordinator.DisposeAsync().ConfigureAwait(false);
            LogService.Dispose();
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
