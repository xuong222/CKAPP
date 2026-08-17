using System.IO;
using System.Net;

using CH32UpperComputer.App.Services;
using CH32UpperComputer.App.Tests.TestSupport;
using CH32UpperComputer.App.ViewModels;
using CH32UpperComputer.Core.Iap;
using CH32UpperComputer.Core.Protocol;
using CH32UpperComputer.Infrastructure.Coordination;
using CH32UpperComputer.Infrastructure.Iap;
using CH32UpperComputer.Infrastructure.Serial;
using CH32UpperComputer.Infrastructure.Settings;
using CH32UpperComputer.Infrastructure.Transactions;
using CH32UpperComputer.Testing;

namespace CH32UpperComputer.App.Tests.ViewModels
{
    /// <summary>
    /// 验证固件升级页与现有 Modbus 页面共享操作门和定时发送生命周期。
    /// </summary>
    [TestFixture]
    public sealed class FirmwareUpgradeViewModelTests
    {
        /// <summary>
        /// 验证 IAP 活动期间全部数据命令禁用，但刷新和手动断开仍可使用。
        /// </summary>
        [Test]
        public async Task IapGate_DisablesModbusDataCommandsButKeepsSafeSerialActions()
        {
            await using AppViewModelHarness harness = AppViewModelHarness.Create();
            await harness.ConnectAsync();
            harness.CommandConsole.InputText = "01 03 00 02 00 08";

            using IApplicationOperationLease iapLease =
                harness.ApplicationOperationGate.TryEnterIap() ??
                throw new AssertionException("空闲状态应允许取得 IAP 门。");

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.CommandConsole.SendCommand.CanExecute(null),
                    Is.False);
                Assert.That(
                    harness.CommandConsole.StartPeriodicCommand.CanExecute(null),
                    Is.False);
                Assert.That(
                    harness.CommandConsole.CommonCommands[0].SelectCommand.CanExecute(null),
                    Is.False);
                Assert.That(
                    harness.Parameters.ReadAllCommand.CanExecute(null),
                    Is.False);
                Assert.That(
                    harness.Parameters.DiscoverAddressCommand.CanExecute(null),
                    Is.False);
                Assert.That(harness.SerialConnection.CanEditSerialSettings, Is.False);
                Assert.That(
                    harness.SerialConnection.DisconnectCommand.CanExecute(null),
                    Is.True);
                Assert.That(
                    harness.SerialConnection.RefreshPortsCommand.CanExecute(null),
                    Is.True);
                Assert.That(
                    harness.CommandConsole.StatusMessage,
                    Does.Contain("固件升级期间"));
            }));

            iapLease.Dispose();

            Assert.Multiple((Action)(() =>
            {
                Assert.That(
                    harness.CommandConsole.SendCommand.CanExecute(null),
                    Is.True);
                Assert.That(
                    harness.Parameters.ReadAllCommand.CanExecute(null),
                    Is.True);
            }));
        }

        /// <summary>
        /// 验证开始升级会立即停止已经开启的定时发送，成功后也不会自动恢复。
        /// </summary>
        [Test]
        public async Task StartUpgrade_StopsPeriodicSendAndDoesNotRestoreIt()
        {
            string firmwarePath = Path.Combine(
                Path.GetTempPath(),
                $"ch32-iap-viewmodel-{Guid.NewGuid():N}.bin");
            await File.WriteAllBytesAsync(
                firmwarePath,
                Enumerable.Range(0, 128)
                    .Select(index => checked((byte)index))
                    .ToArray());
            ManualTimeProvider modbusTimeProvider = new();
            await using FakeSerialTransport serialTransport = new(modbusTimeProvider);
            await serialTransport.OpenAsync(
                SerialSettings.CreateDefault("COM_TEST"),
                CancellationToken.None);
            ApplicationOperationGate operationGate = new();
            await using ModbusTransactionCoordinator modbusCoordinator = new(
                serialTransport,
                modbusTimeProvider,
                operationGate);
            await modbusCoordinator.StartAsync(CancellationToken.None);
            await using PeriodicSendService periodic = new(
                modbusCoordinator,
                modbusTimeProvider,
                operationGate);
            ModbusRequest periodicRead =
                ModbusRequestFactory.CreateReadHoldingRegisters(1, 0, 1);
            periodic.Configure(
                TransactionRequest.CreateStandard(
                    periodicRead,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(20)),
                TimeSpan.FromSeconds(1));
            periodic.Start();
            BootloaderSimulator simulator = new();
            IapCommunicationLogService logService = new(TimeProvider.System);
            IapProtocolClient protocolClient = new(
                simulator,
                logService,
                TimeProvider.System);
            await using IapUpgradeCoordinator iapCoordinator = new(
                protocolClient,
                logService,
                operationGate,
                TimeProvider.System);
            FirmwareUpgradeViewModel viewModel = new(
                new FirmwareFileService(),
                iapCoordinator,
                logService,
                periodic,
                operationGate,
                new ImmediateUiDispatcher(),
                TimeProvider.System,
                new AppSettings
                {
                    IapTargetAddress = IPAddress.Loopback.ToString(),
                    IapTcpPort = 5000,
                });

            try
            {
                await viewModel.SelectFirmwareAsync(firmwarePath);
                viewModel.IsLinkAddressConfirmed = true;
                using IApplicationOperationLease activeModbusLease =
                    operationGate.TryEnterModbus() ??
                    throw new AssertionException("空闲状态应允许模拟 Modbus 操作。");
                Assert.That(
                    viewModel.StartUpgradeCommand.CanExecute(null),
                    Is.False,
                    "活动 Modbus 请求期间开始升级按钮必须同步禁用。");
                activeModbusLease.Dispose();
                Assert.That(
                    viewModel.StartUpgradeCommand.CanExecute(null),
                    Is.True);
                await viewModel.StartUpgradeCommand.ExecuteAsync(null);

                Assert.Multiple((Action)(() =>
                {
                    Assert.That(
                        iapCoordinator.State,
                        Is.EqualTo(IapUpgradeState.UpgradeSucceeded));
                    Assert.That(periodic.IsRunning, Is.False);
                    Assert.That(viewModel.UpgradeStateText, Is.EqualTo("升级成功"));
                    Assert.That(viewModel.ProgressPercent, Is.EqualTo(100d));
                }));

                using IApplicationOperationLease runBlockingModbusLease =
                    operationGate.TryEnterModbus() ??
                    throw new AssertionException("升级完成后应允许模拟 Modbus 操作。");
                Assert.That(
                    viewModel.RunApplicationCommand.CanExecute(null),
                    Is.False,
                    "活动 Modbus 请求期间 RUN 按钮必须同步禁用。");
            }
            finally
            {
                viewModel.Dispose();
                File.Delete(firmwarePath);
            }
        }
    }
}
