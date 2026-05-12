using System.Text.Json.Serialization;

namespace LocalQuestReborn.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestStatus
{
    NotAccepted,
    InProgress,
    ReadyToComplete,
    Completed,
}
