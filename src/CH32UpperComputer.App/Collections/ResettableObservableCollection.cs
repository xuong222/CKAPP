using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace CH32UpperComputer.App.Collections
{
    /// <summary>
    /// 提供一次 Reset 通知的批量替换能力，避免串口画布重绘时为每条历史记录触发布局。
    /// </summary>
    /// <typeparam name="TItem">集合保存的显示记录类型。</typeparam>
    internal sealed class ResettableObservableCollection<TItem> : ObservableCollection<TItem>
    {
        /// <summary>
        /// 用调用方给出的完整有序快照替换当前集合，并仅发布一次 Reset 通知。
        /// </summary>
        /// <param name="items">按最终显示顺序提供的新集合内容。</param>
        public void ReplaceAll(IEnumerable<TItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            CheckReentrancy();
            Items.Clear();

            foreach (TItem item in items)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
