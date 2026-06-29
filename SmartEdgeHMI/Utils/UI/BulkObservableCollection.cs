using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace SmartEdgeHMI.Utils.UI;

public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

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
