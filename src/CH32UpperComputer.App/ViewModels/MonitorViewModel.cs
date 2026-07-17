using CH32UpperComputer.App.Services;
using CH32UpperComputer.Core.Registers;
using CommunityToolkit.Mvvm.ComponentModel;

using System.Collections.ObjectModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 将设备快照映射为工业仪表盘六张数据卡和右侧明确报警列表。
    /// </summary>
    public sealed partial class MonitorViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 统一执行服务，用于接收有效快照更新通知。
        /// </summary>
        private readonly ModbusOperationService operationService;

        /// <summary>
        /// 将后台事务完成回调切换到 WPF 界面线程。
        /// </summary>
        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// 指示监控 ViewModel 已经释放并停止接收事件。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 当前综合报警状态文本。
        /// </summary>
        [ObservableProperty]
        private string overallStateText = "等待设备数据";

        /// <summary>
        /// 当前报警摘要文本。
        /// </summary>
        [ObservableProperty]
        private string alarmSummary = "尚未读取 40009/40010";

        /// <summary>
        /// 最近一次有效数据更新时间。
        /// </summary>
        [ObservableProperty]
        private string lastUpdatedText = "--";

        /// <summary>
        /// 当前是否存在任一有效报警位。
        /// </summary>
        [ObservableProperty]
        private bool hasAlarm;

        /// <summary>
        /// 初始化监控页固定卡片和报警项，并订阅设备快照更新。
        /// </summary>
        /// <param name="operationService">提供共享设备快照及更新通知的操作服务。</param>
        /// <param name="dispatcher">负责切换到界面线程的调度器抽象。</param>
        public MonitorViewModel(
            ModbusOperationService operationService,
            IUiDispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(operationService);
            ArgumentNullException.ThrowIfNull(dispatcher);
            this.operationService = operationService;
            this.dispatcher = dispatcher;
            SensorCards = new ReadOnlyObservableCollection<SensorCardViewModel>(
                new ObservableCollection<SensorCardViewModel>(CreateSensorCards()));
            AlarmItems = new ReadOnlyObservableCollection<AlarmItemViewModel>(
                new ObservableCollection<AlarmItemViewModel>(CreateAlarmItems()));
            operationService.SnapshotUpdated += HandleSnapshotUpdated;
            RefreshFromSnapshot();
        }

        /// <summary>
        /// 获取按两行三列顺序排列的六张传感器数据卡。
        /// </summary>
        public ReadOnlyObservableCollection<SensorCardViewModel> SensorCards { get; }

        /// <summary>
        /// 获取固定顺序的六项具体报警状态。
        /// </summary>
        public ReadOnlyObservableCollection<AlarmItemViewModel> AlarmItems { get; }

        /// <summary>
        /// 取消快照事件订阅，防止窗口关闭后继续更新界面对象。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            operationService.SnapshotUpdated -= HandleSnapshotUpdated;
        }

        /// <summary>
        /// 将当前设备快照重新映射到全部数据卡和报警项。
        /// </summary>
        public void RefreshFromSnapshot()
        {
            IReadOnlyDictionary<ushort, RegisterValue> values = operationService.DeviceSnapshot.Values;

            foreach (SensorCardViewModel card in SensorCards)
            {
                if (values.TryGetValue(card.ProtocolAddress, out RegisterValue? value))
                {
                    card.UpdateValue(
                        value.FormattedValue,
                        value.IsWithinExpectedRange,
                        value.Diagnostic);
                }
            }

            UpdateAlarmState(values);
            LastUpdatedText = operationService.DeviceSnapshot.LastUpdatedAt?.ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss") ?? "--";
        }

        /// <summary>
        /// 创建工业仪表盘固定的六项显示寄存器映射。
        /// </summary>
        /// <returns>按温度、烟雾、PM2.5、CO、CO2、SO2 排列的新数组。</returns>
        private static SensorCardViewModel[] CreateSensorCards()
        {
            return
            [
                new SensorCardViewModel("温度", 40003, 0x0002, "℃", "TEMP"),
                new SensorCardViewModel("烟雾", 40004, 0x0003, "MQ2", "SMOKE"),
                new SensorCardViewModel("PM2.5", 40005, 0x0004, "μg/m³", "PM"),
                new SensorCardViewModel("一氧化碳", 40006, 0x0005, "ppm", "CO"),
                new SensorCardViewModel("二氧化碳", 40007, 0x0006, "ppm", "CO₂"),
                new SensorCardViewModel("二氧化硫", 40008, 0x0007, "ppm", "SO₂"),
            ];
        }

        /// <summary>
        /// 创建与报警掩码低六位一一对应的固定报警项。
        /// </summary>
        /// <returns>按位序号零至五排列的新数组。</returns>
        private static AlarmItemViewModel[] CreateAlarmItems()
        {
            return
            [
                new AlarmItemViewModel(AlarmChannel.Temperature, "温度报警", 0),
                new AlarmItemViewModel(AlarmChannel.Smoke, "烟雾报警", 1),
                new AlarmItemViewModel(AlarmChannel.Pm25, "PM2.5 报警", 2),
                new AlarmItemViewModel(AlarmChannel.CarbonMonoxide, "CO 报警", 3),
                new AlarmItemViewModel(AlarmChannel.CarbonDioxide, "CO2 报警", 4),
                new AlarmItemViewModel(AlarmChannel.SulfurDioxide, "SO2 报警", 5),
            ];
        }

        /// <summary>
        /// 根据快照中的 40009、40010 和可选 40023 更新综合及逐项报警状态。
        /// </summary>
        /// <param name="values">当前设备快照的独立只读副本。</param>
        private void UpdateAlarmState(IReadOnlyDictionary<ushort, RegisterValue> values)
        {
            if (!values.TryGetValue(0x0008, out RegisterValue? relayValue) ||
                !values.TryGetValue(0x0009, out RegisterValue? activeMaskValue))
            {
                OverallStateText = "等待报警状态";
                AlarmSummary = "尚未读取 40009/40010";
                HasAlarm = false;
                return;
            }

            ushort enableMask = values.TryGetValue(0x0016, out RegisterValue? enableMaskValue)
                ? enableMaskValue.RawWord
                : (ushort)0x003F;
            AlarmState alarmState = AlarmState.FromRegisterWords(
                relayValue.RawWord,
                activeMaskValue.RawWord,
                enableMask);

            foreach (AlarmItemViewModel item in AlarmItems)
            {
                item.Update(alarmState.GetItem(item.Channel));
            }

            HasAlarm = alarmState.Items.Any(item => item.IsActive);
            OverallStateText = alarmState.CombinedRelayActive || HasAlarm
                ? "设备报警"
                : "系统正常";
            string[] activeNames = alarmState.Items
                .Where(item => item.IsActive)
                .Select(item => item.DisplayName)
                .ToArray();
            AlarmSummary = activeNames.Length == 0
                ? "当前无报警项"
                : string.Join("、", activeNames);

            if (!string.IsNullOrWhiteSpace(alarmState.Diagnostic))
            {
                AlarmSummary = $"{AlarmSummary}；{alarmState.Diagnostic}";
            }
        }

        /// <summary>
        /// 接收后台快照通知并在界面线程刷新全部监控显示。
        /// </summary>
        private void HandleSnapshotUpdated()
        {
            if (isDisposed)
            {
                return;
            }

            dispatcher.Post(RefreshFromSnapshot);
        }
    }
}
