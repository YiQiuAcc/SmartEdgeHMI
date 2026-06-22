namespace SmartEdgeHMI.Communication;

public interface IDeviceCommunicationCoordinator
{
    Task SendThresholdAsync(double value);

    Task ResetDeviceAsync();
}
