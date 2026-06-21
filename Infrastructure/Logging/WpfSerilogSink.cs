using CommunityToolkit.Mvvm.Messaging;
using Serilog.Core;
using Serilog.Events;
using SmartEdgeHMI.Models.Messages;

namespace SmartEdgeHMI.Infrastructure.Logging;

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
