using Serilog;

namespace SmartEdgeHMI.Utils;

public static class TaskExtensions
{
    /// <summary>观察未等待的后台任务</summary>
    public static async Task ObserveAndLogAsync(this Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 任务主动取消
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Operation} 异常退出", operation);
        }
    }
}
