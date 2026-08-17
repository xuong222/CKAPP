namespace CH32UpperComputer.Infrastructure.Coordination
{
    /// <summary>
    /// 表示应用级 Modbus 或 IAP 独占资格。
    /// </summary>
    public interface IApplicationOperationLease : IDisposable
    {
        /// <summary>
        /// 获取该租约是否代表 IAP 独占流程。
        /// </summary>
        bool IsIapLease { get; }
    }

    /// <summary>
    /// 在 Modbus 活动计数和唯一 IAP 流程之间提供原子互斥。
    /// </summary>
    public interface IApplicationOperationGate
    {
        /// <summary>
        /// 在 IAP 活动状态变化后发布新值。
        /// </summary>
        event Action<bool>? IapActivityChanged;

        /// <summary>
        /// 在活动 Modbus 操作数量变化后发布新计数。
        /// </summary>
        event Action<int>? ModbusActivityChanged;

        /// <summary>
        /// 获取当前是否有 IAP 流程持有独占租约。
        /// </summary>
        bool IsIapActive { get; }

        /// <summary>
        /// 获取当前已进入的 Modbus 操作数量。
        /// </summary>
        int ActiveModbusOperationCount { get; }

        /// <summary>
        /// 尝试进入一项 Modbus 操作；IAP 活动时立即返回空值。
        /// </summary>
        /// <returns>成功时返回必须释放的 Modbus 租约，否则为空。</returns>
        IApplicationOperationLease? TryEnterModbus();

        /// <summary>
        /// 尝试进入唯一 IAP 流程；存在 Modbus 或另一 IAP 时立即返回空值。
        /// </summary>
        /// <returns>成功时返回必须释放的 IAP 租约，否则为空。</returns>
        IApplicationOperationLease? TryEnterIap();
    }

    /// <summary>
    /// 使用单同步门实现无等待、无队列的应用操作互斥。
    /// </summary>
    public sealed class ApplicationOperationGate : IApplicationOperationGate
    {
        /// <summary>
        /// 保护 IAP 标志和 Modbus 活动计数。
        /// </summary>
        private readonly object sync = new();

        /// <summary>
        /// 当前 Modbus 租约数量。
        /// </summary>
        private int activeModbusOperationCount;

        /// <summary>
        /// 当前是否存在 IAP 租约。
        /// </summary>
        private bool isIapActive;

        /// <summary>
        /// 在 IAP 活动状态变化后发布新值。
        /// </summary>
        public event Action<bool>? IapActivityChanged;

        /// <summary>
        /// 在活动 Modbus 操作数量变化后发布新计数。
        /// </summary>
        public event Action<int>? ModbusActivityChanged;

        /// <summary>
        /// 获取当前是否存在 IAP 租约。
        /// </summary>
        public bool IsIapActive
        {
            get
            {
                lock (sync)
                {
                    return isIapActive;
                }
            }
        }

        /// <summary>
        /// 获取当前 Modbus 租约数量。
        /// </summary>
        public int ActiveModbusOperationCount
        {
            get
            {
                lock (sync)
                {
                    return activeModbusOperationCount;
                }
            }
        }

        /// <summary>
        /// 在 IAP 未活动时原子增加 Modbus 活动计数。
        /// </summary>
        /// <returns>成功时返回 Modbus 租约，否则为空。</returns>
        public IApplicationOperationLease? TryEnterModbus()
        {
            Lease lease;
            int newCount;

            lock (sync)
            {
                if (isIapActive)
                {
                    return null;
                }

                activeModbusOperationCount = checked(activeModbusOperationCount + 1);
                newCount = activeModbusOperationCount;
                lease = new Lease(this, false);
            }

            PublishModbusActivityChanged(newCount);
            return lease;
        }

        /// <summary>
        /// 仅在没有 Modbus 和 IAP 活动时取得 IAP 独占租约。
        /// </summary>
        /// <returns>成功时返回 IAP 租约，否则为空。</returns>
        public IApplicationOperationLease? TryEnterIap()
        {
            Lease? lease;

            lock (sync)
            {
                if (isIapActive || activeModbusOperationCount != 0)
                {
                    return null;
                }

                isIapActive = true;
                lease = new Lease(this, true);
            }

            PublishIapActivityChanged(true);
            return lease;
        }

        /// <summary>
        /// 释放一个由本实例创建的租约。
        /// </summary>
        /// <param name="isIapLease">指示释放 IAP 还是 Modbus 资格。</param>
        private void Release(bool isIapLease)
        {
            bool publishIapInactive = false;
            int? newModbusCount = null;

            lock (sync)
            {
                if (isIapLease)
                {
                    if (!isIapActive)
                    {
                        throw new InvalidOperationException("IAP 应用操作租约已经释放。");
                    }

                    isIapActive = false;
                    publishIapInactive = true;
                }
                else
                {
                    activeModbusOperationCount--;

                    if (activeModbusOperationCount < 0)
                    {
                        throw new InvalidOperationException("Modbus 应用操作计数不能为负数。");
                    }

                    newModbusCount = activeModbusOperationCount;
                }
            }

            if (publishIapInactive)
            {
                PublishIapActivityChanged(false);
            }

            if (newModbusCount.HasValue)
            {
                PublishModbusActivityChanged(newModbusCount.Value);
            }
        }

        /// <summary>
        /// 逐个发布 IAP 活动变化并隔离观察者异常。
        /// </summary>
        /// <param name="isActive">新的 IAP 活动状态。</param>
        private void PublishIapActivityChanged(bool isActive)
        {
            Action<bool>? observers = IapActivityChanged;

            if (observers is null)
            {
                return;
            }

            foreach (Action<bool> observer in observers.GetInvocationList().Cast<Action<bool>>())
            {
                try
                {
                    observer(isActive);
                }
                catch (Exception)
                {
                    // 界面观察者异常不得破坏应用级互斥。
                }
            }
        }

        /// <summary>
        /// 逐个发布活动 Modbus 计数并隔离观察者异常。
        /// </summary>
        /// <param name="activeCount">新的活动 Modbus 操作数量。</param>
        private void PublishModbusActivityChanged(int activeCount)
        {
            Action<int>? observers = ModbusActivityChanged;

            if (observers is null)
            {
                return;
            }

            foreach (Action<int> observer in observers.GetInvocationList().Cast<Action<int>>())
            {
                try
                {
                    observer(activeCount);
                }
                catch (Exception)
                {
                    // 界面观察者异常不得破坏应用级互斥。
                }
            }
        }

        /// <summary>
        /// 确保每项应用操作资格只释放一次。
        /// </summary>
        private sealed class Lease : IApplicationOperationLease
        {
            /// <summary>
            /// 创建该租约的应用操作门。
            /// </summary>
            private ApplicationOperationGate? owner;

            /// <summary>
            /// 初始化一项 Modbus 或 IAP 租约。
            /// </summary>
            /// <param name="owner">创建和释放租约的应用操作门。</param>
            /// <param name="isIapLease">是否代表 IAP 独占流程。</param>
            internal Lease(
                ApplicationOperationGate owner,
                bool isIapLease)
            {
                this.owner = owner;
                IsIapLease = isIapLease;
            }

            /// <summary>
            /// 获取是否代表 IAP 独占流程。
            /// </summary>
            public bool IsIapLease { get; }

            /// <summary>
            /// 幂等释放应用操作资格。
            /// </summary>
            public void Dispose()
            {
                ApplicationOperationGate? capturedOwner =
                    Interlocked.Exchange(ref owner, null);
                capturedOwner?.Release(IsIapLease);
            }
        }
    }
}
