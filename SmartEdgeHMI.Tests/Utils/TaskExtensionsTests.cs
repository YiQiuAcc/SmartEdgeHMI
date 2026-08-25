using SmartEdgeHMI.Utils;

namespace SmartEdgeHMI.Tests.Utils;

public class TaskExtensionsTests
{
    [Fact]
    public async Task ObserveAndLogAsync_FaultedTask_CompletesWithoutRethrowing()
    {
        Task task = Task.FromException(new InvalidOperationException("test"));

        await task.ObserveAndLogAsync("测试后台任务");
    }
}
