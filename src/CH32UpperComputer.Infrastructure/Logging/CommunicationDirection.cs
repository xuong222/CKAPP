namespace CH32UpperComputer.Infrastructure.Logging
{
    /// <summary>
    /// 指定一条通信日志在用户界面和导出文件中的业务方向或诊断类别。
    /// </summary>
    public enum CommunicationDirection
    {
        /// <summary>
        /// 上位机向串口发送的完整线路帧。
        /// </summary>
        Transmit = 0,

        /// <summary>
        /// 串口接收并归属于正常事务的完整线路帧或原始捕获。
        /// </summary>
        Receive = 1,

        /// <summary>
        /// 写入、解析、超时、断开或其他通信错误。
        /// </summary>
        Error = 2,

        /// <summary>
        /// 迟到响应、旧会话数据或无法归属当前事务的线路数据。
        /// </summary>
        LateOrUnsolicited = 3,

        /// <summary>
        /// 连接状态、配置切换、导出等不直接对应线路方向的系统记录。
        /// </summary>
        System = 4,
    }
}
