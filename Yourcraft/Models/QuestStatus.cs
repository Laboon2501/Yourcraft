using System.Text.Json.Serialization;

namespace Yourcraft.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestStatus
{
    NotAccepted,
    InProgress,
    ReadyToComplete,
    Completed,
}
