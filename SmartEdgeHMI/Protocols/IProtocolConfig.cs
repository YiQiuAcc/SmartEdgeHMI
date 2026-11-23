using System.ComponentModel;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Protocols;

/// <summary>协议层读取的配置视图, 用于解耦 Service 层对 ConnectionViewModel 的直接依赖</summary>
/// <remarks>实现须在 <see cref="SelectedProtocol"/> 变化时触发 <see cref="INotifyPropertyChanged.PropertyChanged"/>, 协议解析器据此启停轮询</remarks>
public interface IProtocolConfig : INotifyPropertyChanged
{
    /// <summary>当前生效的协议; 该值变化会立即影响已连接端口的解析与轮询</summary>
    CommunicationProtocol SelectedProtocol { get; }

    /// <summary>当前处于连接状态的端口集合</summary>
    IEnumerable<string> ConnectedPorts { get; }

    /// <summary>界面选中的端口, 未选择时为 null</summary>
    string? SelectedPort { get; }

    /// <summary>Modbus 从站地址, 由界面写入并同步到 settings.json</summary>
    byte SlaveAddress { get; }
}
