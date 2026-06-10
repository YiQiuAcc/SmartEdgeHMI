using CommunityToolkit.Mvvm.Messaging;
using Serilog.Core;
using Serilog.Events;
using SmartEdgeHMI.Models;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Services;

// 自定义 Serilog 接收器
public class WpfSerilogSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var logModel = new SystemLogModel
        {
            Timestamp = logEvent.Timestamp.DateTime,
            Level = logEvent.Level.ToString().ToUpper(), // 转成大写
            Message = logEvent.RenderMessage() // 渲染出完整的字符串
        };
        // 通过 Messenger 将这条日志广播出去
        WeakReferenceMessenger.Default.Send(new LogUpdateMessage(logModel));
    }
}
