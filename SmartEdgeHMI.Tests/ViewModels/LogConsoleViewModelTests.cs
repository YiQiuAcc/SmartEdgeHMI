using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Tests.ViewModels;

public class LogConsoleViewModelTests
{
    [Theory]
    [InlineData(500, 50)]
    [InlineData(50, 5)]
    [InlineData(9, 1)]
    [InlineData(0, 1)]
    [InlineData(-30, 1)]
    public void TrimCount_ReturnsMaxDividedTenWithFloorOfOne(int max, int expected)
    {
        Assert.Equal(expected, LogConsoleViewModel.TrimCount(max));
    }
}
