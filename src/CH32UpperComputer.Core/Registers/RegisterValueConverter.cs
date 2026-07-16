using System.Globalization;

namespace CH32UpperComputer.Core.Registers
{
    /// <summary>
    /// 表示一个寄存器线路字经该寄存器自身定义解释后的不可变值。
    /// </summary>
    public sealed class RegisterValue
    {
        /// <summary>
        /// 初始化一个已经完成有符号解释、精确缩放和范围诊断的寄存器值。
        /// </summary>
        /// <param name="definition">决定该线路字解释方式的寄存器定义。</param>
        /// <param name="rawWord">响应帧中未修改的 16 位线路字。</param>
        /// <param name="interpretedRawValue">依据定义解释的有符号或无符号原始整数。</param>
        /// <param name="displayValue">依据定义十进制缩放后的显示数值。</param>
        /// <param name="formattedValue">依据定义精度格式化且不包含单位的显示文本。</param>
        /// <param name="isWithinExpectedRange">指示解释值是否位于定义的读取预期范围。</param>
        /// <param name="diagnostic">越界诊断；预期范围内为 <see langword="null"/>。</param>
        internal RegisterValue(
            RegisterDefinition definition,
            ushort rawWord,
            int interpretedRawValue,
            decimal displayValue,
            string formattedValue,
            bool isWithinExpectedRange,
            string? diagnostic)
        {
            Definition = definition;
            RawWord = rawWord;
            InterpretedRawValue = interpretedRawValue;
            DisplayValue = displayValue;
            FormattedValue = formattedValue;
            IsWithinExpectedRange = isWithinExpectedRange;
            Diagnostic = diagnostic;
        }

        /// <summary>
        /// 获取决定该线路字解释方式的寄存器定义。
        /// </summary>
        public RegisterDefinition Definition { get; }

        /// <summary>
        /// 获取响应帧中未钳位、未重写的 16 位线路字。
        /// </summary>
        public ushort RawWord { get; }

        /// <summary>
        /// 获取依据定义独立解释的有符号或无符号原始整数。
        /// </summary>
        public int InterpretedRawValue { get; }

        /// <summary>
        /// 获取依据定义十进制缩放后的显示数值。
        /// </summary>
        public decimal DisplayValue { get; }

        /// <summary>
        /// 获取依据定义精度格式化且不包含单位的显示文本。
        /// </summary>
        public string FormattedValue { get; }

        /// <summary>
        /// 获取指示解释值是否位于定义的读取预期范围内的值。
        /// </summary>
        public bool IsWithinExpectedRange { get; }

        /// <summary>
        /// 获取越界诊断；读取值处于预期范围时为 <see langword="null"/>。
        /// </summary>
        public string? Diagnostic { get; }
    }

    /// <summary>
    /// 表示界面显示数值转换为单个 Modbus 寄存器线路字的不可变结果。
    /// </summary>
    public sealed class RegisterWriteResult
    {
        /// <summary>
        /// 初始化一项写入编码结果。
        /// </summary>
        /// <param name="isSuccess">指示编码是否满足只读、步进和值域约束。</param>
        /// <param name="rawWord">成功时的 16 位线路字；失败时为空。</param>
        /// <param name="errorMessage">失败原因；成功时为空。</param>
        private RegisterWriteResult(
            bool isSuccess,
            ushort? rawWord,
            string? errorMessage)
        {
            IsSuccess = isSuccess;
            RawWord = rawWord;
            ErrorMessage = errorMessage;
        }

        /// <summary>
        /// 获取指示写入数值是否成功编码的值。
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// 获取成功编码的 16 位线路字；失败时为 <see langword="null"/>。
        /// </summary>
        public ushort? RawWord { get; }

        /// <summary>
        /// 获取失败原因；成功时为 <see langword="null"/>。
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// 创建包含可发送线路字的成功结果。
        /// </summary>
        /// <param name="rawWord">已经过范围和类型检查的 16 位线路字。</param>
        /// <returns>不包含错误信息的成功结果。</returns>
        internal static RegisterWriteResult Success(ushort rawWord)
        {
            return new RegisterWriteResult(true, rawWord, null);
        }

        /// <summary>
        /// 创建不包含线路字的失败结果。
        /// </summary>
        /// <param name="errorMessage">清晰说明拒绝原因的非空错误信息。</param>
        /// <returns>不能用于创建 Modbus 写请求的失败结果。</returns>
        internal static RegisterWriteResult Failure(string errorMessage)
        {
            return new RegisterWriteResult(false, null, errorMessage);
        }
    }

    /// <summary>
    /// 提供由单个寄存器定义驱动的线路字解释和写入编码功能。
    /// </summary>
    public static class RegisterValueConverter
    {
        /// <summary>
        /// 依据指定寄存器定义解释一个 16 位线路字，并保留越界原始事实。
        /// </summary>
        /// <param name="definition">明确给出有符号性、缩放和预期范围的寄存器定义。</param>
        /// <param name="rawWord">响应帧中的原始 16 位线路字。</param>
        /// <returns>包含原始字、解释整数、显示值和可选诊断的不可变值。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="definition"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">定义包含未知原始编码时抛出。</exception>
        public static RegisterValue Decode(
            RegisterDefinition definition,
            ushort rawWord)
        {
            ArgumentNullException.ThrowIfNull(definition);

            int interpretedRawValue = InterpretRawWord(definition, rawWord);
            decimal displayValue = interpretedRawValue * definition.DisplayScale;
            string format = $"F{definition.DecimalPlaces}";
            string formattedValue = displayValue.ToString(format, CultureInfo.InvariantCulture);
            bool isWithinExpectedRange =
                interpretedRawValue >= definition.ExpectedRawMinimum &&
                interpretedRawValue <= definition.ExpectedRawMaximum;
            string? diagnostic = isWithinExpectedRange
                ? null
                : $"{definition.Name} 原始解释值 {interpretedRawValue} 超出预期范围 " +
                    $"[{definition.ExpectedRawMinimum}, {definition.ExpectedRawMaximum}]；已保留原始字 0x{rawWord:X4}。";

            return new RegisterValue(
                definition,
                rawWord,
                interpretedRawValue,
                displayValue,
                formattedValue,
                isWithinExpectedRange,
                diagnostic);
        }

        /// <summary>
        /// 将界面显示数值按指定寄存器定义反向缩放并编码为线路字。
        /// </summary>
        /// <param name="definition">明确给出可写范围、有符号性和缩放的寄存器定义。</param>
        /// <param name="displayValue">用户确认后准备写入的显示数值。</param>
        /// <returns>成功时包含线路字，失败时包含只读、步进或范围错误。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="definition"/> 为空时抛出。</exception>
        public static RegisterWriteResult EncodeWrite(
            RegisterDefinition definition,
            decimal displayValue)
        {
            ArgumentNullException.ThrowIfNull(definition);

            if (!definition.IsWritable)
            {
                return RegisterWriteResult.Failure($"寄存器 {definition.DocumentAddress}（{definition.Name}）为只读，不能写入。");
            }

            decimal rawDecimal = displayValue / definition.DisplayScale;

            if (rawDecimal != decimal.Truncate(rawDecimal))
            {
                return RegisterWriteResult.Failure(
                    $"{definition.Name} 的输入 {displayValue} 不符合显示步进 {definition.DisplayScale}。");
            }

            int minimum = definition.WritableRawMinimum!.Value;
            int maximum = definition.WritableRawMaximum!.Value;

            if (rawDecimal < minimum || rawDecimal > maximum)
            {
                return RegisterWriteResult.Failure(
                    $"{definition.Name} 的原始写入值 {rawDecimal} 超出允许范围 [{minimum}, {maximum}]。");
            }

            int rawValue = decimal.ToInt32(rawDecimal);
            ushort rawWord = EncodeInterpretedValue(definition, rawValue);

            return RegisterWriteResult.Success(rawWord);
        }

        /// <summary>
        /// 依据单个定义明确指定的编码解释 16 位线路字。
        /// </summary>
        /// <param name="definition">决定当前线路字有符号性和业务语义的寄存器定义。</param>
        /// <param name="rawWord">待解释的未修改 16 位线路字。</param>
        /// <returns>UInt16 定义返回 0 至 65535；Int16 定义按二进制补码返回 -32768 至 32767。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="definition"/> 为空时抛出。</exception>
        /// <exception cref="ArgumentOutOfRangeException">定义包含未知原始编码时抛出。</exception>
        public static int InterpretRawWord(
            RegisterDefinition definition,
            ushort rawWord)
        {
            ArgumentNullException.ThrowIfNull(definition);

            return definition.RawEncoding switch
            {
                RegisterRawEncoding.UInt16Address => rawWord,
                RegisterRawEncoding.UInt16Enumeration => rawWord,
                RegisterRawEncoding.Int16Measurement => unchecked((short)rawWord),
                RegisterRawEncoding.UInt16Boolean => rawWord,
                RegisterRawEncoding.UInt16BitMask => rawWord,
                RegisterRawEncoding.Int16Threshold => unchecked((short)rawWord),
                RegisterRawEncoding.Int16Hysteresis => unchecked((short)rawWord),
                RegisterRawEncoding.Int16Compensation => unchecked((short)rawWord),
                RegisterRawEncoding.UInt16Command => rawWord,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    definition.RawEncoding,
                    "寄存器定义包含未知的原始值编码。"),
            };
        }

        /// <summary>
        /// 依据单个定义明确指定的编码把已验证原始整数转换为线路字。
        /// </summary>
        /// <param name="definition">决定当前整数有符号性和业务语义的寄存器定义。</param>
        /// <param name="rawValue">已通过该定义写入范围验证的原始整数。</param>
        /// <returns>可直接放入 Modbus 写请求数据域的 16 位线路字。</returns>
        /// <exception cref="ArgumentOutOfRangeException">定义包含未知原始编码时抛出。</exception>
        private static ushort EncodeInterpretedValue(
            RegisterDefinition definition,
            int rawValue)
        {
            return definition.RawEncoding switch
            {
                RegisterRawEncoding.UInt16Address => checked((ushort)rawValue),
                RegisterRawEncoding.UInt16Enumeration => checked((ushort)rawValue),
                RegisterRawEncoding.Int16Measurement => unchecked((ushort)checked((short)rawValue)),
                RegisterRawEncoding.UInt16Boolean => checked((ushort)rawValue),
                RegisterRawEncoding.UInt16BitMask => checked((ushort)rawValue),
                RegisterRawEncoding.Int16Threshold => unchecked((ushort)checked((short)rawValue)),
                RegisterRawEncoding.Int16Hysteresis => unchecked((ushort)checked((short)rawValue)),
                RegisterRawEncoding.Int16Compensation => unchecked((ushort)checked((short)rawValue)),
                RegisterRawEncoding.UInt16Command => checked((ushort)rawValue),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(definition),
                    definition.RawEncoding,
                    "寄存器定义包含未知的原始值编码。"),
            };
        }
    }
}
