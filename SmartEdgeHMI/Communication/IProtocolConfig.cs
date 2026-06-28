using System.ComponentModel;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Communication;

/// <summary>通信协议配置抽象层 解耦 Service 层对 ConnectionViewModel 的直接依赖</summary>
public interface IProtocolConfig : INotifyPropertyChanged
{
    /// <summary>当前选中的通信协议</summary>
    CommunicationProtocol SelectedProtocol { get; }

    /// <summary>已连接的串口端口列表</summary>
    IEnumerable<string> ConnectedPorts { get; }

    /// <summary>当前选中的端口名</summary>
    string? SelectedPort { get; }

    /// <summary>Modbus 从机地址</summary>
    byte SlaveAddress { get; }
}
