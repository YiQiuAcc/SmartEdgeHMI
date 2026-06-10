using System.Collections;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartEdgeHMI.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    protected static void DispatchToUI(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    protected static void EnableCollectionSynchronization(IEnumerable collection)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess())
            BindingOperations.EnableCollectionSynchronization(collection, new object());
        else
            dispatcher.Invoke(() => BindingOperations.EnableCollectionSynchronization(collection, new object()));
    }
}
