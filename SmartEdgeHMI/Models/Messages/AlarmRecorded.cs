using SmartEdgeHMI.Database.Entities;

namespace SmartEdgeHMI.Models.Messages;

public class AlarmRecorded(AlarmRecord record)
{
    public AlarmRecord Record { get; } = record;
}
