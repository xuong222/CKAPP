using System.Collections.ObjectModel;

namespace CH32UpperComputer.Core.Registers
{
    /// <summary>
    /// 定义报警位低六位唯一对应的设备传感器通道。
    /// </summary>
    public enum AlarmChannel
    {
        /// <summary>
        /// bit0，温度报警通道。
        /// </summary>
        Temperature,

        /// <summary>
        /// bit1，烟雾报警通道。
        /// </summary>
        Smoke,

        /// <summary>
        /// bit2，PM2.5 报警通道。
        /// </summary>
        Pm25,

        /// <summary>
        /// bit3，CO 报警通道。
        /// </summary>
        CarbonMonoxide,

        /// <summary>
        /// bit4，CO2 报警通道。
        /// </summary>
        CarbonDioxide,

        /// <summary>
        /// bit5，SO2 报警通道。
        /// </summary>
        SulfurDioxide,
    }

    /// <summary>
    /// 表示单个文档规定报警通道的启用状态和当前活动状态。
    /// </summary>
    public sealed class AlarmItemState
    {
        /// <summary>
        /// 初始化一个报警通道不可变状态。
        /// </summary>
        /// <param name="channel">低六位唯一对应的传感器通道。</param>
        /// <param name="displayName">界面使用的通道中文名称。</param>
        /// <param name="bitIndex">该通道在 40010 和 40023 中的零基位序号。</param>
        /// <param name="isActive">40010 对应位是否指示当前正在报警。</param>
        /// <param name="isEnabled">40023 对应位是否允许该通道参与报警。</param>
        internal AlarmItemState(
            AlarmChannel channel,
            string displayName,
            int bitIndex,
            bool isActive,
            bool isEnabled)
        {
            Channel = channel;
            DisplayName = displayName;
            BitIndex = bitIndex;
            IsActive = isActive;
            IsEnabled = isEnabled;
        }

        /// <summary>
        /// 获取低六位唯一对应的传感器通道。
        /// </summary>
        public AlarmChannel Channel { get; }

        /// <summary>
        /// 获取界面使用的通道中文名称。
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// 获取该通道在 40010 和 40023 中的零基位序号。
        /// </summary>
        public int BitIndex { get; }

        /// <summary>
        /// 获取 40010 对应位指示的当前报警活动状态。
        /// </summary>
        public bool IsActive { get; }

        /// <summary>
        /// 获取 40023 对应位指示的报警使能状态。
        /// </summary>
        public bool IsEnabled { get; }
    }

    /// <summary>
    /// 表示 40009、40010 和 40023 三项寄存器共同形成的不可变报警状态。
    /// </summary>
    public sealed class AlarmState
    {
        /// <summary>
        /// 当前固件明确定义的低六位掩码。
        /// </summary>
        private const ushort KnownAlarmMask = 0x003F;

        /// <summary>
        /// 初始化一项已经完成低位拆分和未知位诊断的报警状态。
        /// </summary>
        /// <param name="combinedRelayRawWord">40009 未修改的原始线路字。</param>
        /// <param name="activeMaskRawWord">40010 未修改的原始线路字。</param>
        /// <param name="enableMaskRawWord">40023 未修改的原始线路字。</param>
        /// <param name="items">严格按 bit0 至 bit5 排列的六个通道状态。</param>
        /// <param name="diagnostic">异常继电器值或未知高位的组合诊断。</param>
        private AlarmState(
            ushort combinedRelayRawWord,
            ushort activeMaskRawWord,
            ushort enableMaskRawWord,
            AlarmItemState[] items,
            string? diagnostic)
        {
            CombinedRelayRawWord = combinedRelayRawWord;
            ActiveMaskRawWord = activeMaskRawWord;
            EnableMaskRawWord = enableMaskRawWord;
            Items = Array.AsReadOnly(items);
            Diagnostic = diagnostic;
        }

        /// <summary>
        /// 获取 40009 未修改的综合报警继电器原始线路字。
        /// </summary>
        public ushort CombinedRelayRawWord { get; }

        /// <summary>
        /// 获取指示 40009 是否明确等于一的综合继电器活动状态。
        /// </summary>
        public bool CombinedRelayActive => CombinedRelayRawWord == 1;

        /// <summary>
        /// 获取 40010 未修改的活动报警原始线路字。
        /// </summary>
        public ushort ActiveMaskRawWord { get; }

        /// <summary>
        /// 获取 40023 未修改的报警使能原始线路字。
        /// </summary>
        public ushort EnableMaskRawWord { get; }

        /// <summary>
        /// 获取 40010 中当前文档定义的低六位活动报警掩码。
        /// </summary>
        public ushort ActiveMask => (ushort)(ActiveMaskRawWord & KnownAlarmMask);

        /// <summary>
        /// 获取 40023 中当前文档定义的低六位报警使能掩码。
        /// </summary>
        public ushort EnableMask => (ushort)(EnableMaskRawWord & KnownAlarmMask);

        /// <summary>
        /// 获取 40010 中不属于当前六个传感器定义的原始高位。
        /// </summary>
        public ushort UnknownActiveBits => (ushort)(ActiveMaskRawWord & ~KnownAlarmMask);

        /// <summary>
        /// 获取 40023 中不属于当前六个传感器定义的原始高位。
        /// </summary>
        public ushort UnknownEnableBits => (ushort)(EnableMaskRawWord & ~KnownAlarmMask);

        /// <summary>
        /// 获取严格按 bit0 至 bit5 排列的六个文档规定通道状态只读列表。
        /// </summary>
        public IReadOnlyList<AlarmItemState> Items { get; }

        /// <summary>
        /// 获取异常继电器值或未知高位的组合诊断；所有原始值合法时为空。
        /// </summary>
        public string? Diagnostic { get; }

        /// <summary>
        /// 从 40009、40010 和 40023 三个未修改线路字构造报警状态。
        /// </summary>
        /// <param name="combinedRelayRawWord">40009 综合报警继电器的原始线路字。</param>
        /// <param name="activeMaskRawWord">40010 当前活动报警位的原始线路字。</param>
        /// <param name="enableMaskRawWord">40023 报警使能位的原始线路字。</param>
        /// <returns>始终仅包含六个已知通道，并保留未知原始位诊断的不可变状态。</returns>
        public static AlarmState FromRegisterWords(
            ushort combinedRelayRawWord,
            ushort activeMaskRawWord,
            ushort enableMaskRawWord)
        {
            ushort activeMask = (ushort)(activeMaskRawWord & KnownAlarmMask);
            ushort enableMask = (ushort)(enableMaskRawWord & KnownAlarmMask);
            AlarmItemState[] items =
            [
                CreateItem(AlarmChannel.Temperature, "温度", 0, activeMask, enableMask),
                CreateItem(AlarmChannel.Smoke, "烟雾", 1, activeMask, enableMask),
                CreateItem(AlarmChannel.Pm25, "PM2.5", 2, activeMask, enableMask),
                CreateItem(AlarmChannel.CarbonMonoxide, "CO", 3, activeMask, enableMask),
                CreateItem(AlarmChannel.CarbonDioxide, "CO2", 4, activeMask, enableMask),
                CreateItem(AlarmChannel.SulfurDioxide, "SO2", 5, activeMask, enableMask),
            ];
            string? diagnostic = CreateDiagnostic(
                combinedRelayRawWord,
                activeMaskRawWord,
                enableMaskRawWord);

            return new AlarmState(
                combinedRelayRawWord,
                activeMaskRawWord,
                enableMaskRawWord,
                items,
                diagnostic);
        }

        /// <summary>
        /// 根据传感器通道取得其独立的活动状态和使能状态。
        /// </summary>
        /// <param name="channel">当前固件明确定义的六个报警通道之一。</param>
        /// <returns>与通道唯一对应的不可变状态。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> 不是已定义枚举值时抛出。</exception>
        public AlarmItemState GetItem(AlarmChannel channel)
        {
            int index = channel switch
            {
                AlarmChannel.Temperature => 0,
                AlarmChannel.Smoke => 1,
                AlarmChannel.Pm25 => 2,
                AlarmChannel.CarbonMonoxide => 3,
                AlarmChannel.CarbonDioxide => 4,
                AlarmChannel.SulfurDioxide => 5,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(channel),
                    channel,
                    "未知报警通道不能映射为设备未定义的传感器。"),
            };

            return Items[index];
        }

        /// <summary>
        /// 创建一个低位对应的通道状态，并分别读取活动掩码与使能掩码。
        /// </summary>
        /// <param name="channel">当前位唯一对应的传感器通道。</param>
        /// <param name="displayName">界面使用的通道中文名称。</param>
        /// <param name="bitIndex">当前通道在两个掩码中的零基位序号。</param>
        /// <param name="activeMask">已经去除未知高位的活动报警掩码。</param>
        /// <param name="enableMask">已经去除未知高位的报警使能掩码。</param>
        /// <returns>不会混淆活动和使能事实的不可变通道状态。</returns>
        private static AlarmItemState CreateItem(
            AlarmChannel channel,
            string displayName,
            int bitIndex,
            ushort activeMask,
            ushort enableMask)
        {
            ushort bit = (ushort)(1 << bitIndex);

            return new AlarmItemState(
                channel,
                displayName,
                bitIndex,
                (activeMask & bit) != 0,
                (enableMask & bit) != 0);
        }

        /// <summary>
        /// 为异常继电器值及两个掩码中的未知高位创建可组合诊断。
        /// </summary>
        /// <param name="combinedRelayRawWord">40009 未修改原始字。</param>
        /// <param name="activeMaskRawWord">40010 未修改原始字。</param>
        /// <param name="enableMaskRawWord">40023 未修改原始字。</param>
        /// <returns>存在异常事实时为中文组合诊断，否则为空。</returns>
        private static string? CreateDiagnostic(
            ushort combinedRelayRawWord,
            ushort activeMaskRawWord,
            ushort enableMaskRawWord)
        {
            List<string> diagnostics = [];

            if (combinedRelayRawWord > 1)
            {
                diagnostics.Add($"40009 综合报警继电器值 0x{combinedRelayRawWord:X4} 不是已定义的 0 或 1");
            }

            ushort unknownActiveBits = (ushort)(activeMaskRawWord & ~KnownAlarmMask);

            if (unknownActiveBits != 0)
            {
                diagnostics.Add($"40010 包含未知活动报警高位 0x{unknownActiveBits:X4}");
            }

            ushort unknownEnableBits = (ushort)(enableMaskRawWord & ~KnownAlarmMask);

            if (unknownEnableBits != 0)
            {
                diagnostics.Add($"40023 包含未知报警使能高位 0x{unknownEnableBits:X4}");
            }

            return diagnostics.Count == 0
                ? null
                : string.Join("；", diagnostics) + "；未创建未定义传感器。";
        }
    }
}
