using CommunityToolkit.Mvvm.Messaging;
using Serilog.Core;
using Serilog.Events;
using SmartEdgeHMI.Models.Logging;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Utils.Logging;

/// <summary>Serilog 到 UI 日志控制台的转发 Sink</summary>
/// <remarks>
/// <see cref="Emit"/> 在写日志的线程上同步执行, 这里只做消息投递,
/// 切回 UI 线程由 <c>LogConsoleViewModel</c> 负责; 在此处直接操作绑定集合会抛跨线程访问异常。
/// </remarks>
public class WpfSerilogSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var logModel = new SystemLogModel
        {
            Timestamp = logEvent.Timestamp.DateTime,
            Level = logEvent.Level.ToString().ToUpper(),
            Message = logEvent.RenderMessage()
        };
        WeakReferenceMessenger.Default.Send(new LogUpdate(logModel));
    }
}
