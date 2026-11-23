using SmartEdgeHMI.Common;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Tests.ViewModels;

public class ConnectionViewModelAutoConnectTests
{
    [Theory]
    [InlineData("Modbus", CommunicationProtocol.Modbus)]
    [InlineData("modbus", CommunicationProtocol.Modbus)]
    [InlineData("MODBUS", CommunicationProtocol.Modbus)]
    [InlineData("JSON", CommunicationProtocol.JSON)]
    [InlineData("json", CommunicationProtocol.JSON)]
    public void ParseProtocol_KnownNames_CaseInsensitive(string stored, CommunicationProtocol expected)
    {
        Assert.Equal(expected, ConnectionViewModel.ParseProtocol(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("1")]
    public void ParseProtocol_MissingOrInvalid_FallsBackToJsonWithoutThrowing(string? stored)
    {
        Assert.Equal(CommunicationProtocol.JSON, ConnectionViewModel.ParseProtocol(stored));
    }

    [Fact]
    public void ShouldAutoConnect_NormalCase_ReturnsTrue()
    {
        Assert.True(ConnectionViewModel.ShouldAutoConnect("COM4", ["COM4", "COM5"], alreadyConnected: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShouldAutoConnect_SavedPortEmpty_ReturnsFalse(string? savedPort)
    {
        Assert.False(ConnectionViewModel.ShouldAutoConnect(savedPort, ["COM4"], alreadyConnected: false));
    }

    [Fact]
    public void ShouldAutoConnect_PortNotPresent_ReturnsFalse()
    {
        Assert.False(ConnectionViewModel.ShouldAutoConnect("COM4", ["COM5"], alreadyConnected: false));
    }

    [Fact]
    public void ShouldAutoConnect_AlreadyConnected_ReturnsFalse()
    {
        Assert.False(ConnectionViewModel.ShouldAutoConnect("COM4", ["COM4"], alreadyConnected: true));
    }
}
