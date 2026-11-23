using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace SmartEdgeHMI.Utils.UI;

/// <summary>支持批量追加的 ObservableCollection, 整批只发一次 Reset 通知</summary>
/// <remarks>
/// 逐条 <see cref="ObservableCollection{T}.Add"/> 会让绑定的 ListView 每追加一条数据就触发一次布局,
/// 批量导入历史记录时开销随条数线性增长, 故在 <see cref="AddRange"/> 期间抑制逐条通知。
/// 只能从 UI 线程调用; Reset 通知会使虚拟化容器整体重建, 已选项与滚动位置不保留。
/// </remarks>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    /// <summary>追加一批元素, 结束后统一触发一次 Reset</summary>
    public void AddRange(IEnumerable<T> items)
    {
        _suppressNotification = true;
        try
        {
            foreach (var item in items)
                Add(item);
        }
        finally
        {
            _suppressNotification = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }
}
