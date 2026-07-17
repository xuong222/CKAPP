using CH32UpperComputer.Core.Registers;
using CommunityToolkit.Mvvm.ComponentModel;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示参数页中一个由独立寄存器定义驱动的可写参数行。
    /// </summary>
    public sealed partial class ParameterItemViewModel : ObservableObject
    {
        /// <summary>
        /// 防止掩码文本与六个开关互相更新时递归。
        /// </summary>
        private bool isSynchronizingMask;

        /// <summary>
        /// 用户准备写入的显示值或十六进制掩码文本。
        /// </summary>
        [ObservableProperty]
        private string inputText = string.Empty;

        /// <summary>
        /// 最近一次有效读取值的显示文本。
        /// </summary>
        [ObservableProperty]
        private string currentValueText = "--";

        /// <summary>
        /// 当前输入经过完整校验后将写入的原始线路字预览。
        /// </summary>
        [ObservableProperty]
        private string rawPreview = "--";

        /// <summary>
        /// 当前输入的范围、步进或格式错误。
        /// </summary>
        [ObservableProperty]
        private string validationMessage = string.Empty;

        /// <summary>
        /// 初始化一项严格由寄存器定义驱动的参数行。
        /// </summary>
        /// <param name="definition">包含原始编码、缩放、范围、权限和单位的寄存器定义。</param>
        public ParameterItemViewModel(RegisterDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);

            if (!definition.IsWritable)
            {
                throw new ArgumentException("参数页只能创建固件明确允许写入的寄存器。", nameof(definition));
            }

            Definition = definition;
            AlarmBits = new ObservableCollection<AlarmEnableBitViewModel>();

            if (definition.DocumentAddress == 40023)
            {
                string[] names = ["温度", "烟雾", "PM2.5", "CO", "CO2", "SO2"];

                for (int index = 0; index < names.Length; index++)
                {
                    AlarmEnableBitViewModel bit = new(names[index], index);
                    bit.PropertyChanged += HandleAlarmBitChanged;
                    AlarmBits.Add(bit);
                }

                InputText = "0x003F";
            }
        }

        /// <summary>
        /// 获取当前参数唯一寄存器定义。
        /// </summary>
        public RegisterDefinition Definition { get; }

        /// <summary>
        /// 获取 40023 使用的六个报警使能开关；其他参数为空集合。
        /// </summary>
        public ObservableCollection<AlarmEnableBitViewModel> AlarmBits { get; }

        /// <summary>
        /// 获取参数是否为需要同时显示十六进制和六个开关的 40023 掩码。
        /// </summary>
        public bool IsAlarmMask => Definition.DocumentAddress == 40023;

        /// <summary>
        /// 获取面向用户的四万区地址文本。
        /// </summary>
        public string AddressText => Definition.DocumentAddress.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 使用最近一次有效寄存器值更新当前值和默认编辑值。
        /// </summary>
        /// <param name="value">由当前寄存器定义解释得到的快照值。</param>
        public void ApplySnapshotValue(RegisterValue value)
        {
            ArgumentNullException.ThrowIfNull(value);
            CurrentValueText = IsAlarmMask
                ? $"0x{value.RawWord:X4}"
                : $"{value.FormattedValue} {Definition.Unit}".Trim();

            if (string.IsNullOrWhiteSpace(InputText) || InputText == "0x003F")
            {
                InputText = IsAlarmMask
                    ? $"0x{value.RawWord:X4}"
                    : value.FormattedValue;
            }

            if (IsAlarmMask)
            {
                SynchronizeBits(value.RawWord);
            }
        }

        /// <summary>
        /// 完整解析并校验当前输入，成功时返回准备写入的原始线路字。
        /// </summary>
        /// <param name="rawWord">成功时接收由寄存器定义编码的原始 16 位字。</param>
        /// <returns>格式、范围、步进和低六位规则均满足时返回真。</returns>
        public bool TryGetRawWord(out ushort rawWord)
        {
            if (!TryParseDisplayValue(InputText, out decimal displayValue, out string? parseError))
            {
                rawWord = 0;
                RawPreview = "--";
                ValidationMessage = parseError!;
                return false;
            }

            RegisterWriteResult result = RegisterValueConverter.EncodeWrite(Definition, displayValue);

            if (!result.IsSuccess || !result.RawWord.HasValue)
            {
                rawWord = 0;
                RawPreview = "--";
                ValidationMessage = result.ErrorMessage ?? "参数无法编码为原始线路字。";
                return false;
            }

            rawWord = result.RawWord.Value;

            if (IsAlarmMask && (rawWord & 0xFFC0) != 0)
            {
                RawPreview = "--";
                ValidationMessage = "报警使能掩码只允许低六位，范围为 0x0000 至 0x003F。";
                return false;
            }

            RawPreview = $"0x{rawWord:X4}";
            ValidationMessage = string.Empty;
            return true;
        }

        /// <summary>
        /// 解析普通十进制显示值，或为 40023 解析 0x 前缀十六进制掩码。
        /// </summary>
        /// <param name="text">用户当前输入文本。</param>
        /// <param name="displayValue">成功时接收用于寄存器定义反向缩放的显示值。</param>
        /// <param name="errorMessage">失败时接收可直接显示的格式错误。</param>
        /// <returns>输入能够转换为十进制数值时返回真。</returns>
        private bool TryParseDisplayValue(
            string text,
            out decimal displayValue,
            out string? errorMessage)
        {
            string normalized = text?.Trim() ?? string.Empty;

            if (IsAlarmMask && normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (ushort.TryParse(
                    normalized.AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out ushort mask))
                {
                    displayValue = mask;
                    errorMessage = null;
                    return true;
                }

                displayValue = 0;
                errorMessage = "报警掩码必须是 0x0000 至 0x003F 的十六进制值。";
                return false;
            }

            if (decimal.TryParse(
                normalized,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out displayValue))
            {
                errorMessage = null;
                return true;
            }

            errorMessage = $"{Definition.Name} 必须输入有效数字。";
            return false;
        }

        /// <summary>
        /// 在输入改变时立即刷新原始字预览和掩码开关。
        /// </summary>
        /// <param name="value">新的用户输入文本。</param>
        partial void OnInputTextChanged(string value)
        {
            _ = TryGetRawWord(out ushort rawWord);

            if (IsAlarmMask && string.IsNullOrWhiteSpace(ValidationMessage))
            {
                SynchronizeBits(rawWord);
            }
        }

        /// <summary>
        /// 接收任一报警使能开关变化并重建低六位十六进制输入。
        /// </summary>
        /// <param name="sender">发生变化的报警使能位。</param>
        /// <param name="eventArgs">发生变化的属性名称。</param>
        private void HandleAlarmBitChanged(
            object? sender,
            PropertyChangedEventArgs eventArgs)
        {
            if (isSynchronizingMask || eventArgs.PropertyName != nameof(AlarmEnableBitViewModel.IsEnabled))
            {
                return;
            }

            ushort mask = 0;

            foreach (AlarmEnableBitViewModel bit in AlarmBits)
            {
                if (bit.IsEnabled)
                {
                    mask |= checked((ushort)(1 << bit.BitIndex));
                }
            }

            InputText = $"0x{mask:X4}";
        }

        /// <summary>
        /// 使用一个原始低六位掩码同步六个使能开关而不触发递归重建。
        /// </summary>
        /// <param name="mask">40023 当前或准备写入的原始线路字。</param>
        private void SynchronizeBits(ushort mask)
        {
            isSynchronizingMask = true;

            try
            {
                foreach (AlarmEnableBitViewModel bit in AlarmBits)
                {
                    bit.IsEnabled = (mask & (1 << bit.BitIndex)) != 0;
                }
            }
            finally
            {
                isSynchronizingMask = false;
            }
        }
    }
}
