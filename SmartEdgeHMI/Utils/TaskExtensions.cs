using Serilog;

namespace SmartEdgeHMI.Utils;

/// <summary>后台任务异常兜底, 将 fire-and-forget 任务接入统一日志出口</summary>
/// <remarks>
/// 未被 await 的任务一旦抛异常, 只在终结器回收时以 UnobservedTaskException 的形式出现。
/// 因此所有 <c>_ = Task.Run(...)</c> 形式的调用点都必须通过此方法统一处理异常。
/// 本方法自身不会抛出, 取消视为正常结束。
/// </remarks>
public static class TaskExtensions
{
    /// <summary>等待任务完成并记录未被处理的异常</summary>
    /// <param name="task">被观察的任务</param>
    /// <param name="operation">日志中的操作名, 用于定位是哪个后台循环退出</param>
    public static async Task ObserveAndLogAsync(this Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是后台循环的预期退出路径
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Operation} 异常退出", operation);
        }
    }
}
