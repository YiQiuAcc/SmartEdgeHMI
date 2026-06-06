using CommunityToolkit.Mvvm.Messaging;
using Serilog.Core;
using Serilog.Events;

namespace SmartEdgeHMI.Services;

// 日志的数据模型 (UI 绑定实体)
public class SystemLogModel
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

// 用于广播的消息记录
public class LogUpdateMessage(SystemLogModel data)
{
    public SystemLogModel LogData { get; } = data;
}

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
