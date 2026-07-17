using System.Collections.ObjectModel;

namespace CH32UpperComputer.App.ViewModels
{
    /// <summary>
    /// 表示一次读取或单个 0x10 写入允许处理的连续普通参数组。
    /// </summary>
    public sealed class ParameterGroupViewModel
    {
        /// <summary>
        /// 初始化一个地址严格连续的参数组。
        /// </summary>
        /// <param name="name">界面分组名称。</param>
        /// <param name="items">按协议地址严格递增且连续的参数项。</param>
        public ParameterGroupViewModel(
            string name,
            IEnumerable<ParameterItemViewModel> items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(items);
            ParameterItemViewModel[] array = items.ToArray();

            if (array.Length == 0)
            {
                throw new ArgumentException("参数组至少需要一个寄存器。", nameof(items));
            }

            for (int index = 1; index < array.Length; index++)
            {
                if (array[index].Definition.ProtocolAddress != array[index - 1].Definition.ProtocolAddress + 1)
                {
                    throw new ArgumentException("参数组必须由协议地址连续的寄存器组成。", nameof(items));
                }
            }

            Name = name;
            Items = new ReadOnlyObservableCollection<ParameterItemViewModel>(
                new ObservableCollection<ParameterItemViewModel>(array));
        }

        /// <summary>
        /// 获取界面分组名称。
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// 获取按协议地址连续排列的参数项。
        /// </summary>
        public ReadOnlyObservableCollection<ParameterItemViewModel> Items { get; }

        /// <summary>
        /// 获取组内首个协议寄存器地址。
        /// </summary>
        public ushort StartAddress => Items[0].Definition.ProtocolAddress;

        /// <summary>
        /// 获取组内寄存器数量。
        /// </summary>
        public ushort Quantity => checked((ushort)Items.Count);
    }
}
