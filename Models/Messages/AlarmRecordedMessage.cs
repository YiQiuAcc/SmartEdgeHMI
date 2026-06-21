using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Models.Messages;

public class AlarmRecordedMessage(AlarmRecordEntity record)
{
    public AlarmRecordEntity Record { get; } = record;
}
