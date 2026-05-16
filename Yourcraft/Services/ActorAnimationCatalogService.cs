using System.Reflection;
using Dalamud.Plugin.Services;
using Yourcraft.Models;
using Lumina.Excel.Sheets;

namespace Yourcraft.Services;

public sealed class ActorAnimationCatalogService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly List<ActorAnimationCatalogEntry> entries = [];
    private bool loaded;

    public ActorAnimationCatalogService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public IReadOnlyList<ActorAnimationCatalogEntry> Entries
    {
        get
        {
            this.EnsureLoaded();
            return this.entries;
        }
    }

    public IEnumerable<ActorAnimationCatalogEntry> Search(ActorAnimationPickerMode mode, string searchText, int limit = 300)
    {
        this.EnsureLoaded();
        var query = searchText.Trim();
        var source = mode switch
        {
            ActorAnimationPickerMode.EmoteActionsOnly => this.entries.Where(entry => entry.SourceType == "Emote"),
            ActorAnimationPickerMode.ExpressionCandidates => this.entries.Where(IsExpressionCandidate),
            _ => this.entries,
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(entry =>
                entry.ActionTimelineId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.SourceRowId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Command.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return source
            .OrderBy(entry => entry.SourceType == "Emote" ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ActionTimelineId)
            .Take(limit)
            .ToList();
    }

    public void Refresh()
    {
        this.loaded = false;
        this.entries.Clear();
        this.EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (this.loaded)
            return;

        this.loaded = true;
        this.entries.Clear();
        this.ReadActionTimelines();
        this.ReadEmotes();
    }

    private void ReadActionTimelines()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<ActionTimeline>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var rowId = ReadUInt(row, "RowId");
                if (rowId == 0 || rowId > ushort.MaxValue)
                    continue;

                var key = ReadFirstString(row, "Key", "Name");
                this.entries.Add(new ActorAnimationCatalogEntry(
                    (ushort)rowId,
                    rowId,
                    string.IsNullOrWhiteSpace(key) ? $"ActionTimeline {rowId}" : key,
                    string.Empty,
                    "ActionTimeline",
                    "Raw",
                    key,
                    ReadFirstString(row, "Slot"),
                    false));
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read ActionTimeline sheet.");
        }
    }

    private void ReadEmotes()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Emote>();
            if (sheet == null)
                return;

            foreach (var row in sheet)
            {
                var emoteRowId = ReadUInt(row, "RowId");
                var name = ReadFirstString(row, "Name", "Text");
                if (string.IsNullOrWhiteSpace(name))
                    name = $"Emote {emoteRowId}";

                var command = ReadFirstString(row, "TextCommand", "Command");
                for (var i = 0; i < 7; i++)
                {
                    if (!TryReadIndexedRowId(row, "ActionTimeline", i, out var timelineId) ||
                        timelineId == 0 ||
                        timelineId > ushort.MaxValue)
                    {
                        continue;
                    }

                    this.entries.Add(new ActorAnimationCatalogEntry(
                        (ushort)timelineId,
                        emoteRowId,
                        name,
                        command,
                        "Emote",
                        EmoteTimelinePurpose(i),
                        string.Empty,
                        i == 4 ? "UpperBody" : string.Empty,
                        i == 0));
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read Emote sheet.");
        }
    }

    private static bool IsExpressionCandidate(ActorAnimationCatalogEntry entry)
        => entry.SourceType == "Emote" &&
           (entry.Purpose.Contains("Upper", StringComparison.OrdinalIgnoreCase) ||
            entry.Slot.Contains("Facial", StringComparison.OrdinalIgnoreCase) ||
            entry.Slot.Contains("Upper", StringComparison.OrdinalIgnoreCase) ||
            entry.Name.Contains("smile", StringComparison.OrdinalIgnoreCase) ||
            entry.Name.Contains("laugh", StringComparison.OrdinalIgnoreCase));

    private static string EmoteTimelinePurpose(int index)
        => index switch
        {
            0 => "Loop",
            1 => "Intro",
            2 => "Ground",
            3 => "Chair",
            4 => "UpperBodyBlend",
            _ => $"Variant {index}",
        };

    private static bool TryReadIndexedRowId(object source, string memberName, int index, out uint rowId)
    {
        rowId = 0;
        var value = ReadMember(source, memberName);
        if (value == null)
            return false;

        try
        {
            var indexer = value.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(prop => prop.GetIndexParameters().Length == 1);
            if (indexer == null)
                return false;

            var parameterType = indexer.GetIndexParameters()[0].ParameterType;
            var indexValue = parameterType == typeof(uint)
                ? (object)(uint)index
                : parameterType == typeof(byte)
                    ? (byte)index
                    : index;
            var item = indexer.GetValue(value, [indexValue]);
            rowId = ReadUInt(item, "RowId");
            return rowId != 0;
        }
        catch
        {
            return false;
        }
    }

    private static object? ReadMember(object? source, string name)
    {
        if (source == null)
            return null;

        var type = source.GetType();
        try
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
                return property.GetValue(source);

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static uint ReadUInt(object? source, params string[] memberNames)
    {
        foreach (var name in memberNames)
        {
            var raw = ReadMember(source, name);
            switch (raw)
            {
                case uint u:
                    return u;
                case ushort us:
                    return us;
                case byte b:
                    return b;
                case int i when i >= 0:
                    return (uint)i;
                case ulong ul when ul <= uint.MaxValue:
                    return (uint)ul;
                case string text when uint.TryParse(text, out var parsed):
                    return parsed;
            }
        }

        return 0;
    }

    private static string ReadFirstString(object source, params string[] memberNames)
    {
        foreach (var name in memberNames)
        {
            var raw = ReadMember(source, name);
            var text = raw?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }
}
