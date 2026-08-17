using System.Globalization;

namespace CH32UpperComputer.Core.Iap
{
    /// <summary>
    /// 把当前固件和文档扩展错误码转换为包含定位上下文的中文提示。
    /// </summary>
    public static class IapErrorTranslator
    {
        /// <summary>
        /// 翻译一条 IAP NACK。
        /// </summary>
        /// <param name="command">返回 NACK 的命令。</param>
        /// <param name="sequence">返回 NACK 的请求序号。</param>
        /// <param name="detail">当前固件错误码、扩展错误码或 DATA 期望偏移。</param>
        /// <returns>包含命令、sequence、原始 detail 和建议操作的中文信息。</returns>
        public static string Translate(
            IapCommand command,
            uint sequence,
            uint detail)
        {
            string reason = TranslateReason(command, detail);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{command.ToString().ToUpperInvariant()} sequence={sequence} 返回 NACK，detail={detail} (0x{detail:X8})：{reason}");
        }

        /// <summary>
        /// 根据当前固件和命令上下文解释 detail。
        /// </summary>
        /// <param name="command">发生错误的命令。</param>
        /// <param name="detail">Bootloader 返回的原始值。</param>
        /// <returns>错误原因和建议操作。</returns>
        private static string TranslateReason(
            IapCommand command,
            uint detail)
        {
            return detail switch
            {
                1U => "参数、对齐或帧形状非法，请检查协议字段。",
                2U => "地址或长度超出 App 区，请重新选择合法固件。",
                3U => "当前 Bootloader 状态不允许该命令，请重新连接并从 HELLO 开始。",
                4U => "Flash 擦除或写入失败，请检查供电和 Flash 状态。",
                5U => command == IapCommand.Data
                    ? "DATA 分片 CRC 校验失败，可按规则重发当前分片。"
                    : "整体镜像 CRC 校验失败，请重新执行完整升级。",
                6U => command == IapCommand.Data
                    ? "DATA offset 与设备期望不一致，必须重新执行完整升级。"
                    : "App 无有效入口或当前 App 无效，不能运行。",
                7U => "DATA 分片 CRC 错误，请重发当前分片。",
                8U => "App 区擦除失败，请检查 Flash 和供电。",
                9U => "Flash 写入失败，请检查 Flash 和供电。",
                10U => "整体镜像 CRC32 校验失败，必须重新升级。",
                11U => "当前 App 无效，不能运行。",
                12U => "当前升级状态不允许执行该命令。",
                13U => "App 基址不匹配，禁止升级。",
                14U => "App 大小与 BEGIN 声明不一致。",
                15U => "Bootloader 内部错误，请复位设备后重试。",
                _ => "未知错误，请保留原始值并检查 Bootloader 日志。",
            };
        }
    }
}
