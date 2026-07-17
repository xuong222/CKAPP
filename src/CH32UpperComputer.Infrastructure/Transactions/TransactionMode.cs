namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 指定事务采用严格标准响应匹配还是原始调试捕获。
    /// </summary>
    public enum TransactionMode
    {
        /// <summary>
        /// 只有结构、CRC、地址、功能码和回显签名均匹配时完成。
        /// </summary>
        Standard = 0,

        /// <summary>
        /// 不要求请求签名，按结构、CRC、静默、总超时或容量边界结束非空捕获。
        /// </summary>
        RawDebug = 1,
    }
}
