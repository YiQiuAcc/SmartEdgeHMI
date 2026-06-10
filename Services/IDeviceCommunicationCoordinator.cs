namespace SmartEdgeHMI.Services;

public interface IDeviceCommunicationCoordinator
{
    Task SendThresholdAsync(double value);

    Task ResetDeviceAsync();
}
