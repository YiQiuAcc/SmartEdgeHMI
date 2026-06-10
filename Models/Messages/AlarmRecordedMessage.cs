using SmartEdgeHMI.Models.Entities;

namespace SmartEdgeHMI.Models.Messages;

public class AlarmRecordedMessage
{
    public AlarmRecordEntity Record { get; }

    public AlarmRecordedMessage(AlarmRecordEntity record) => Record = record;
}
