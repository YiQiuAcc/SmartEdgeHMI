using System.ComponentModel;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Communication;

/// <summary>通信协议配置抽象层 解耦 Service 层对 ConnectionViewModel 的直接依赖</summary>
public interface IProtocolConfig : INotifyPropertyChanged
{
    CommunicationProtocol SelectedProtocol { get; }

    IEnumerable<string> ConnectedPorts { get; }

    string? SelectedPort { get; }
}
