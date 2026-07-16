using System.Globalization;
using System.Text;

namespace CH32UpperComputer.Core.Protocol
{
    /// <summary>
    /// 表示一次十六进制帧文本解析的不可变结果。
    /// </summary>
    public sealed class HexFrameParseResult
    {
        /// <summary>
        /// 成功结果内部持有的私有字节副本；失败结果持有空数组。
        /// </summary>
        private readonly byte[] bytes;

        /// <summary>
        /// 初始化一项完整的解析结果。
        /// </summary>
        /// <param name="isSuccess">指示解析是否成功。</param>
        /// <param name="bytes">由结果对象独占持有的字节数组。</param>
        /// <param name="errorMessage">解析失败时面向用户的错误信息；成功时为 <see langword="null"/>。</param>
        /// <param name="hasValidTrailingCrc">指示成功解析的字节是否已经带有有效尾随 CRC16。</param>
        private HexFrameParseResult(
            bool isSuccess,
            byte[] bytes,
            string? errorMessage,
            bool hasValidTrailingCrc)
        {
            IsSuccess = isSuccess;
            this.bytes = bytes;
            ErrorMessage = errorMessage;
            HasValidTrailingCrc = hasValidTrailingCrc;
        }

        /// <summary>
        /// 获取指示解析是否成功的值。
        /// </summary>
        public bool IsSuccess { get; }

        /// <summary>
        /// 获取成功解析后的只读字节视图；解析失败时为空。
        /// </summary>
        public ReadOnlyMemory<byte> Bytes => bytes;

        /// <summary>
        /// 获取解析失败时面向用户的错误信息；解析成功时为 <see langword="null"/>。
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// 获取指示成功解析的字节是否已经带有有效 Modbus CRC16 的值。
        /// </summary>
        public bool HasValidTrailingCrc { get; }

        /// <summary>
        /// 使用解析字节的防御性副本创建成功结果。
        /// </summary>
        /// <param name="parsedBytes">成功解析出的字节。</param>
        /// <returns>包含只读字节视图和尾随 CRC16 检测状态的成功结果。</returns>
        internal static HexFrameParseResult CreateSuccess(ReadOnlySpan<byte> parsedBytes)
        {
            byte[] ownedBytes = parsedBytes.ToArray();
            bool hasValidTrailingCrc = ModbusCrc16.IsValid(ownedBytes);

            return new HexFrameParseResult(
                true,
                ownedBytes,
                null,
                hasValidTrailingCrc);
        }

        /// <summary>
        /// 创建不包含解析字节的失败结果。
        /// </summary>
        /// <param name="errorMessage">面向用户且可直接指导修正输入的错误信息。</param>
        /// <returns>包含指定错误信息的失败结果。</returns>
        internal static HexFrameParseResult CreateFailure(string errorMessage)
        {
            return new HexFrameParseResult(
                false,
                Array.Empty<byte>(),
                errorMessage,
                false);
        }
    }

    /// <summary>
    /// 提供十六进制帧文本的无异常校验、解析和规范化格式输出功能。
    /// </summary>
    public static class HexFrameParser
    {
        /// <summary>
        /// 尝试把允许使用常见分隔符的十六进制文本解析为字节序列。
        /// </summary>
        /// <param name="text">待解析文本；允许空格、制表符、回车、换行和短横线作为分隔符。</param>
        /// <param name="maxBytes">允许解析的最大字节数，必须大于零。</param>
        /// <returns>成功时包含只读字节及尾随 CRC16 状态，失败时包含可操作错误信息的不可变结果。</returns>
        public static HexFrameParseResult TryParse(string? text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text))
            {
                return HexFrameParseResult.CreateFailure(
                    "十六进制输入不能为空，请输入至少一个完整字节。");
            }

            if (maxBytes <= 0)
            {
                return HexFrameParseResult.CreateFailure(
                    "最大字节数必须大于 0，请检查输入限制配置。");
            }

            List<byte> parsedBytes = new(Math.Min(maxBytes, text.Length / 2));

            // 保存尚未与低半字节配对的高半字节；null 表示当前位于字节边界。
            int? pendingHighNibble = null;

            // 单独记录有效十六进制数字数量，用于给奇数位输入返回准确提示。
            int hexDigitCount = 0;

            for (int characterIndex = 0; characterIndex < text.Length; characterIndex++)
            {
                char character = text[characterIndex];

                if (IsSeparator(character))
                {
                    continue;
                }

                int nibble = GetHexDigitValue(character);

                if (nibble < 0)
                {
                    return HexFrameParseResult.CreateFailure(
                        $"位置 {characterIndex + 1} 的字符“{character}”不是十六进制数字；" +
                        "仅允许 0-9、A-F、a-f 以及空白或短横线分隔符。");
                }

                hexDigitCount++;

                if (pendingHighNibble is null)
                {
                    pendingHighNibble = nibble;
                    continue;
                }

                if (parsedBytes.Count >= maxBytes)
                {
                    return HexFrameParseResult.CreateFailure(
                        $"输入超过允许的最大长度 {maxBytes} 字节，请缩短报文后重试。");
                }

                parsedBytes.Add((byte)((pendingHighNibble.Value << 4) | nibble));
                pendingHighNibble = null;
            }

            if (hexDigitCount == 0)
            {
                return HexFrameParseResult.CreateFailure(
                    "十六进制输入不能为空，请输入至少一个完整字节。");
            }

            if (pendingHighNibble is not null)
            {
                return HexFrameParseResult.CreateFailure(
                    $"十六进制数字数量必须为偶数，当前为 {hexDigitCount} 个；请补全最后一个字节。");
            }

            return HexFrameParseResult.CreateSuccess(parsedBytes.ToArray());
        }

        /// <summary>
        /// 把字节序列格式化为大写两位十六进制，并使用单个空格分隔相邻字节。
        /// </summary>
        /// <param name="bytes">需要格式化的连续字节。</param>
        /// <returns>规范化的十六进制文本；输入为空时返回空字符串。</returns>
        public static string Format(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return string.Empty;
            }

            StringBuilder builder = new();

            for (int byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                if (byteIndex > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(bytes[byteIndex].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>
        /// 判断字符是否为允许出现在十六进制输入中的分隔符。
        /// </summary>
        /// <param name="character">待判断字符。</param>
        /// <returns>字符为空格、制表符、回车、换行或短横线时返回 <see langword="true"/>。</returns>
        private static bool IsSeparator(char character)
        {
            return character is ' ' or '\t' or '\r' or '\n' or '-';
        }

        /// <summary>
        /// 把单个十六进制字符转换为对应的四位数值。
        /// </summary>
        /// <param name="character">待转换的字符。</param>
        /// <returns>合法字符对应的 0 至 15；字符非法时返回 -1。</returns>
        private static int GetHexDigitValue(char character)
        {
            if (character is >= '0' and <= '9')
            {
                return character - '0';
            }

            if (character is >= 'A' and <= 'F')
            {
                return character - 'A' + 10;
            }

            if (character is >= 'a' and <= 'f')
            {
                return character - 'a' + 10;
            }

            return -1;
        }
    }
}
