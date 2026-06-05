namespace SmartEdgeHMI.Constants;

/// <summary>HMI 下发给边缘网关的主动控制动作</summary>
public enum DeviceAction
{
    Start = 1,
    Stop = 2,
    Enable = 3,
    Disable = 4,
    Reset = 5,
    Configure = 6,  // 例如：下发配置参数，配合 Command.Payload 使用
    TriggerSample = 7 // 例如：手动触发一次采样
}
