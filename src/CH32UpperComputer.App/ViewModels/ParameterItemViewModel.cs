using CH32UpperComputer.Core.Registers;
using CommunityToolkit.Mvvm.ComponentModel;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示统一参数表中的一项寄存器定义、当前原始值、选择状态和可选写入值。
    /// </summary>
    public sealed partial class ParameterItemViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// 防止报警掩码文本和六个开关互相更新时递归。
        /// </summary>
        private bool isSynchronizingMask;

        /// <summary>
        /// 指示报警位事件订阅是否已经释放。
        /// </summary>
        private bool isDisposed;

        /// <summary>
        /// 最近一次成功读取的原始线路字。
        /// </summary>
        private ushort? currentRawWord;

        /// <summary>
        /// 当前行是否被批量读取或修改操作选中。
        /// </summary>
        [ObservableProperty]
        private bool isSelected;

        /// <summary>
        /// 用户准备写入的显示值或十六进制掩码文本。
        /// </summary>
        [ObservableProperty]
        private string inputText = string.Empty;

        /// <summary>
        /// 最近一次有效读取值的定义化显示文本。
        /// </summary>
        [ObservableProperty]
        private string currentValueText = "--";

        /// <summary>
        /// 最近一次原始线路字按 Int16 显示的文本。
        /// </summary>
        [ObservableProperty]
        private string signedValueText = "--";

        /// <summary>
        /// 最近一次原始线路字按 UInt16 显示的文本。
        /// </summary>
        [ObservableProperty]
        private string unsignedValueText = "--";

        /// <summary>
        /// 最近一次原始线路字按十六进制显示的文本。
        /// </summary>
        [ObservableProperty]
        private string hexadecimalValueText = "--";

        /// <summary>
        /// 当前输入经过完整校验后将写入的原始线路字预览。
        /// </summary>
        [ObservableProperty]
        private string rawPreview = "--";

        /// <summary>
        /// 当前输入的权限、范围、步进或格式错误。
        /// </summary>
        [ObservableProperty]
        private string validationMessage = string.Empty;

        /// <summary>
        /// 初始化一项严格由单独寄存器定义驱动的统一表格行。
        /// </summary>
        /// <param name="definition">包含原始编码、缩放、范围、权限和单位的寄存器定义。</param>
        public ParameterItemViewModel(RegisterDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
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
        /// 获取当前行唯一寄存器定义。
        /// </summary>
        public RegisterDefinition Definition { get; }

        /// <summary>
        /// 获取 40023 使用的六个报警使能开关；其他寄存器为空集合。
        /// </summary>
        public ObservableCollection<AlarmEnableBitViewModel> AlarmBits { get; }

        /// <summary>
        /// 获取最近一次成功读取的原始线路字；尚未读取时为空。
        /// </summary>
        public ushort? CurrentRawWord => currentRawWord;

        /// <summary>
        /// 获取参数是否为需要同时显示十六进制和六个开关的 40023 掩码。
        /// </summary>
        public bool IsAlarmMask => Definition.DocumentAddress == 40023;

        /// <summary>
        /// 获取寄存器是否属于允许普通 0x06/0x10 写入的报警或补偿参数组。
        /// </summary>
        public bool IsOrdinaryWritable =>
            Definition.DocumentAddress is >= 40011 and <= 40023 or
                >= 40030 and <= 40035;

        /// <summary>
        /// 获取寄存器是否必须使用地址、波特率或恢复出厂专用安全流程。
        /// </summary>
        public bool IsSpecialOperation =>
            Definition.DocumentAddress is 40001 or 40002 or 40036;

        /// <summary>
        /// 获取统一表中是否允许编辑待写值。
        /// </summary>
        public bool CanEdit => IsOrdinaryWritable;

        /// <summary>
        /// 获取面向用户的四万区地址文本。
        /// </summary>
        public string AddressText =>
            Definition.DocumentAddress.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 获取协议层零基地址文本。
        /// </summary>
        public string ProtocolAddressText => $"0x{Definition.ProtocolAddress:X4}";

        /// <summary>
        /// 获取寄存器在统一表中的权限说明。
        /// </summary>
        public string AccessText => IsSpecialOperation
            ? "专用流程"
            : IsOrdinaryWritable
                ? "读 / 写"
                : "只读";

        /// <summary>
        /// 获取输入框不可编辑时显示的明确提示。
        /// </summary>
        public string InputHint => IsSpecialOperation
            ? "请使用下方专用流程"
            : IsOrdinaryWritable
                ? Definition.ValueDescription
                : "只读寄存器";

        /// <summary>
        /// 使用最近一次有效寄存器值更新定义化显示、三种原始视图和默认编辑值。
        /// </summary>
        /// <param name="value">由当前寄存器自身定义解释得到的快照值。</param>
        public void ApplySnapshotValue(RegisterValue value)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Definition.ProtocolAddress != Definition.ProtocolAddress)
            {
                throw new ArgumentException("快照值与参数行的寄存器定义不一致。", nameof(value));
            }

            currentRawWord = value.RawWord;
            CurrentValueText = IsAlarmMask
                ? $"0x{value.RawWord:X4}"
                : $"{value.FormattedValue} {Definition.Unit}".Trim();
            SignedValueText = unchecked((short)value.RawWord)
                .ToString(CultureInfo.InvariantCulture);
            UnsignedValueText = value.RawWord.ToString(CultureInfo.InvariantCulture);
            HexadecimalValueText = $"0x{value.RawWord:X4}";

            if (IsOrdinaryWritable &&
                (string.IsNullOrWhiteSpace(InputText) ||
                 (IsAlarmMask && InputText == "0x003F")))
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
        /// <param name="rawWord">成功时接收由当前寄存器定义编码的原始 16 位字。</param>
        /// <returns>权限、格式、范围、步进和低六位规则均满足时返回真。</returns>
        public bool TryGetRawWord(out ushort rawWord)
        {
            if (!IsOrdinaryWritable)
            {
                rawWord = 0;
                RawPreview = "--";
                ValidationMessage = IsSpecialOperation
                    ? $"{Definition.Name} 必须使用专用安全流程。"
                    : $"{Definition.Name} 为只读寄存器。";
                return false;
            }

            if (!TryParseDisplayValue(InputText, out decimal displayValue, out string? parseError))
            {
                rawWord = 0;
                RawPreview = "--";
                ValidationMessage = parseError!;
                return false;
            }

            RegisterWriteResult result = RegisterValueConverter.EncodeWrite(
                Definition,
                displayValue);

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
        /// 释放报警使能位事件订阅。
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;

            foreach (AlarmEnableBitViewModel bit in AlarmBits)
            {
                bit.PropertyChanged -= HandleAlarmBitChanged;
            }
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

            if (IsAlarmMask &&
                normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
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
            if (!IsOrdinaryWritable)
            {
                return;
            }

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
            if (isSynchronizingMask ||
                eventArgs.PropertyName != nameof(AlarmEnableBitViewModel.IsEnabled))
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
