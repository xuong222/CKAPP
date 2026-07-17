namespace CH32UpperComputer.Infrastructure.Transactions
{
    /// <summary>
    /// 定义单个软件事务通过唯一原子完成门可进入的状态。
    /// </summary>
    public enum TransactionCompletionState
    {
        /// <summary>
        /// 事务仍在等待首个终止事件。
        /// </summary>
        Pending = 0,

        /// <summary>
        /// 标准响应通过结构、CRC 和完整请求签名校验。
        /// </summary>
        Succeeded = 1,

        /// <summary>
        /// 原始调试事务捕获到非空原始响应。
        /// </summary>
        RawCaptured = 2,

        /// <summary>
        /// 设备返回与当前请求匹配的标准 Modbus 异常响应。
        /// </summary>
        ModbusException = 3,

        /// <summary>
        /// 响应总截止时间到达且没有可完成事务的响应。
        /// </summary>
        TimedOut = 4,

        /// <summary>
        /// 当前事务被调用方取消。
        /// </summary>
        Cancelled = 5,

        /// <summary>
        /// 串口仍属于当前会话，但异步写入失败。
        /// </summary>
        WriteFailed = 6,

        /// <summary>
        /// 串口会话断开或端口代次在事务期间失效。
        /// </summary>
        Disconnected = 7,

        /// <summary>
        /// 标准接收缓存达到有界上限，当前事务不能继续解析。
        /// </summary>
        ReceiveOverflow = 8,

        /// <summary>
        /// 应用退出正在终止当前事务。
        /// </summary>
        ApplicationStopping = 9,
    }
}
