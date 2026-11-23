using System.Collections;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartEdgeHMI.ViewModels;

/// <summary>视图模型基类: 提供后台线程回 UI 线程的统一入口</summary>
public abstract class ViewModelBase : ObservableObject
{
    /// <summary>在 UI 线程上执行动作; 已在 UI 线程时同步执行, 否则投递到 Dispatcher 队列</summary>
    /// <remarks>
    /// 无 <see cref="Application.Current"/>(单元测试、设计器) 时直接丢弃, 静默返回。
    /// </remarks>
    protected static void DispatchToUI(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    /// <summary>允许绑定引擎跨线程读取集合</summary>
    /// <remarks>
    /// 这里的锁对象是一次性的, 只交给 WPF 在枚举集合时加锁, 不用于本类内部的互斥;
    /// 因此集合本身的增删仍需通过 <see cref="DispatchToUI"/> 回到 UI 线程, 不能依赖此方法做线程安全。
    /// </remarks>
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
