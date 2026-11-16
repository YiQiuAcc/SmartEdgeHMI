using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Tests.Common;

public class AppSettingsValidatorTests
{
    [Fact]
    public void Validate_DefaultSettings_ReturnsNoErrors()
    {
        IReadOnlyList<string> errors = AppSettingsValidator.Validate(new AppSettings());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_BaudRateZero_ReturnsSingleErrorContainingFieldName()
    {
        AppSettings settings = new();
        settings.Connection.BaudRate = 0;

        IReadOnlyList<string> errors = AppSettingsValidator.Validate(settings);

        Assert.Single(errors);
        Assert.Contains("BaudRate", errors[0]);
    }

    [Fact]
    public void Validate_ThresholdMinus100_ReturnsError()
    {
        AppSettings settings = new();
        settings.Hardware.DefaultThreshold = -100;

        IReadOnlyList<string> errors = AppSettingsValidator.Validate(settings);

        Assert.Single(errors);
        Assert.Contains("DefaultThreshold", errors[0]);
    }

    [Fact]
    public void Validate_Threshold60_ReturnsNoErrors()
    {
        AppSettings settings = new();
        settings.Hardware.DefaultThreshold = 60;

        Assert.Empty(AppSettingsValidator.Validate(settings));
    }

    [Fact]
    public void Validate_MaxLogEntriesZero_ReturnsError()
    {
        AppSettings settings = new();
        settings.Logging.MaxLogEntries = 0;

        IReadOnlyList<string> errors = AppSettingsValidator.Validate(settings);

        Assert.Single(errors);
        Assert.Contains("MaxLogEntries", errors[0]);
    }

    [Fact]
    public void Validate_ThreeInvalidFields_ReturnsThreeErrorsWithoutShortCircuit()
    {
        AppSettings settings = new();
        settings.Connection.BaudRate = 0;
        settings.Modbus.PollingIntervalMs = 0;
        settings.Logging.MaxLogEntries = 0;

        IReadOnlyList<string> errors = AppSettingsValidator.Validate(settings);

        Assert.Equal(3, errors.Count);
        Assert.Contains("BaudRate", errors[0]);
        Assert.Contains("PollingIntervalMs", errors[1]);
        Assert.Contains("MaxLogEntries", errors[2]);
    }
}
