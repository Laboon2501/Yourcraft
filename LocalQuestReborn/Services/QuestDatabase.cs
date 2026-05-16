using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalQuestReborn.Services;

public sealed class QuestDatabase
{
    private const string DevelopmentQuestFilePath =
        @"C:\Users\kiomo\Documents\New project\LocalQuestReborn\Data\quests.json";

    private readonly IPluginLog log;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public QuestDatabase(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.UseDevelopmentQuestPath = true;

        var fallbackDataDirectory = Path.Combine(pluginInterface.ConfigDirectory.FullName, "Data");
        var fallbackQuestFilePath = Path.Combine(fallbackDataDirectory, "quests.json");

        if (this.UseDevelopmentQuestPath && File.Exists(DevelopmentQuestFilePath))
        {
            this.DataDirectory = Path.GetDirectoryName(DevelopmentQuestFilePath) ?? fallbackDataDirectory;
            this.QuestFilePath = DevelopmentQuestFilePath;
            this.IsUsingDevelopmentQuestPath = true;
        }
        else
        {
            this.DataDirectory = fallbackDataDirectory;
            this.QuestFilePath = fallbackQuestFilePath;
            this.IsUsingDevelopmentQuestPath = false;

            if (this.UseDevelopmentQuestPath)
            {
                this.log.Information(
                    "Development quest file not found at {DevelopmentPath}; falling back to {FallbackPath}",
                    DevelopmentQuestFilePath,
                    fallbackQuestFilePath);
            }
        }

        this.Reload();
    }

    public bool UseDevelopmentQuestPath { get; }

    public bool IsUsingDevelopmentQuestPath { get; private set; }

    public string DataDirectory { get; }

    public string QuestFilePath { get; }

    public string PacksDirectory => Path.Combine(this.DataDirectory, "Packs");

    public List<CustomNpc> Npcs { get; private set; } = [];

    public List<PersistentActorConfig> ActorConfigs { get; private set; } = [];

    public List<CustomQuest> Quests { get; private set; } = [];

    public List<CustomProp> Props { get; private set; } = [];

    public void Reload()
    {
        Directory.CreateDirectory(this.DataDirectory);
        Directory.CreateDirectory(this.PacksDirectory);

        if (!File.Exists(this.QuestFilePath))
        {
            File.WriteAllText(this.QuestFilePath, CreateSampleJson(), Encoding.UTF8);
            this.log.Information("Created sample quest database at {Path}", this.QuestFilePath);
        }

        try
        {
            var json = File.ReadAllText(this.QuestFilePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<QuestDatabaseFile>(json, this.jsonOptions) ?? new QuestDatabaseFile();
            this.Npcs = data.Npcs ?? [];
            this.ActorConfigs = data.ActorConfigs ?? [];
            this.Quests = data.Quests ?? [];
            this.Props = data.Props ?? [];
            this.log.Information(
                "Loaded {QuestCount} quests, {NpcCount} NPCs, {ActorConfigCount} actor configs and {PropCount} props from {Path}. UsingDevelopmentPath={UsingDevelopmentPath}",
                this.Quests.Count,
                this.Npcs.Count,
                this.ActorConfigs.Count,
                this.Props.Count,
                this.QuestFilePath,
                this.IsUsingDevelopmentQuestPath);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to load quest database from {Path}", this.QuestFilePath);
            this.Npcs = [];
            this.ActorConfigs = [];
            this.Quests = [];
            this.Props = [];
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(this.DataDirectory);
        Directory.CreateDirectory(this.PacksDirectory);
        var data = new QuestDatabaseFile
        {
            Npcs = this.Npcs,
            ActorConfigs = this.ActorConfigs,
            Quests = this.Quests,
            Props = this.Props,
        };
        var json = JsonSerializer.Serialize(data, this.jsonOptions);
        File.WriteAllText(this.QuestFilePath, json, Encoding.UTF8);
        this.log.Information("Saved quest database to {Path}", this.QuestFilePath);
    }

    public QuestPack ExportCurrentQuestPack(string? packName = null, string? author = null, string? description = null)
    {
        Directory.CreateDirectory(this.PacksDirectory);
        var packId = CreateSafePackId(packName);
        var pack = new QuestPack
        {
            PackId = packId,
            PackName = string.IsNullOrWhiteSpace(packName) ? "Yourcraft 当前任务包" : packName.Trim(),
            Author = string.IsNullOrWhiteSpace(author) ? "Yourcraft" : author.Trim(),
            Version = "1.0.0",
            Description = description ?? "从当前 quests.json 导出的任务包。",
            Npcs = CloneViaJson(this.Npcs),
            ActorConfigs = CloneViaJson(this.ActorConfigs),
            Quests = CloneViaJson(this.Quests),
        };

        var path = Path.Combine(this.PacksDirectory, $"{pack.PackId}.lqrpack.json");
        File.WriteAllText(path, JsonSerializer.Serialize(pack, this.jsonOptions), Encoding.UTF8);
        this.log.Information("Exported quest pack {PackId} to {Path}", pack.PackId, path);
        return pack;
    }

    public QuestPack ExportCurrentQuestPackToPath(string path, string? packName = null, string? author = null, string? description = null)
    {
        var normalizedPath = NormalizePackPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedPath) ?? this.PacksDirectory);

        var packId = Path.GetFileNameWithoutExtension(normalizedPath)
            .Replace(".lqrpack", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(packId))
            packId = CreateSafePackId(packName);

        var pack = new QuestPack
        {
            PackId = packId,
            PackName = string.IsNullOrWhiteSpace(packName) ? "Yourcraft 当前任务包" : packName.Trim(),
            Author = string.IsNullOrWhiteSpace(author) ? "Yourcraft" : author.Trim(),
            Version = "1.0.0",
            Description = description ?? "从当前 quests.json 导出的任务包。",
            Npcs = CloneViaJson(this.Npcs),
            ActorConfigs = CloneViaJson(this.ActorConfigs),
            Quests = CloneViaJson(this.Quests),
        };

        File.WriteAllText(normalizedPath, JsonSerializer.Serialize(pack, this.jsonOptions), Encoding.UTF8);
        this.log.Information("Exported quest pack {PackId} to {Path}", pack.PackId, normalizedPath);
        return pack;
    }

    public List<QuestPackFile> GetAvailableQuestPacks()
    {
        Directory.CreateDirectory(this.PacksDirectory);
        var packs = new List<QuestPackFile>();
        foreach (var path in Directory.EnumerateFiles(this.PacksDirectory, "*.lqrpack.json"))
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var pack = JsonSerializer.Deserialize<QuestPack>(json, this.jsonOptions);
                if (pack != null)
                    packs.Add(new QuestPackFile(path, pack));
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, "Failed to read quest pack at {Path}", path);
            }
        }

        return packs.OrderBy(pack => pack.Pack.PackName).ToList();
    }

    public void ImportQuestPack(QuestPack pack, bool overwriteExistingIds)
    {
        var npcIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var importedNpc in CloneViaJson(pack.Npcs))
        {
            var originalId = importedNpc.Id;
            if (this.Npcs.Any(npc => string.Equals(npc.Id, importedNpc.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (overwriteExistingIds)
                {
                    var index = this.Npcs.FindIndex(npc => string.Equals(npc.Id, importedNpc.Id, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                        this.Npcs[index] = importedNpc;
                }
                else
                {
                    importedNpc.Id = CreateUniqueId(importedNpc.Id, id => this.Npcs.Any(npc => string.Equals(npc.Id, id, StringComparison.OrdinalIgnoreCase)));
                    this.Npcs.Add(importedNpc);
                }
            }
            else
            {
                this.Npcs.Add(importedNpc);
            }

            npcIdMap[originalId] = importedNpc.Id;
        }

        foreach (var importedQuest in CloneViaJson(pack.Quests))
        {
            if (npcIdMap.TryGetValue(importedQuest.GiverNpcId, out var mappedGiverId))
                importedQuest.GiverNpcId = mappedGiverId;

            foreach (var objective in importedQuest.Objectives)
            {
                if (objective.TargetNpcId != null && npcIdMap.TryGetValue(objective.TargetNpcId, out var mappedTargetId))
                    objective.TargetNpcId = mappedTargetId;
            }

            if (this.Quests.Any(quest => string.Equals(quest.Id, importedQuest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (overwriteExistingIds)
                {
                    var index = this.Quests.FindIndex(quest => string.Equals(quest.Id, importedQuest.Id, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                        this.Quests[index] = importedQuest;
                }
                else
                {
                    importedQuest.Id = CreateUniqueId(importedQuest.Id, id => this.Quests.Any(quest => string.Equals(quest.Id, id, StringComparison.OrdinalIgnoreCase)));
                    this.Quests.Add(importedQuest);
                }
            }
            else
            {
                this.Quests.Add(importedQuest);
            }
        }

        foreach (var importedActor in CloneViaJson(pack.ActorConfigs))
        {
            if (npcIdMap.TryGetValue(importedActor.SourceNpcPresetId, out var mappedNpcId))
                importedActor.SourceNpcPresetId = mappedNpcId;

            if (this.ActorConfigs.Any(actor => string.Equals(actor.ConfigId, importedActor.ConfigId, StringComparison.OrdinalIgnoreCase)))
            {
                if (overwriteExistingIds)
                {
                    var index = this.ActorConfigs.FindIndex(actor => string.Equals(actor.ConfigId, importedActor.ConfigId, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                        this.ActorConfigs[index] = importedActor;
                }
                else
                {
                    importedActor.ConfigId = Guid.NewGuid().ToString("N");
                    importedActor.RuntimeId = Guid.NewGuid().ToString("N");
                    this.ActorConfigs.Add(importedActor);
                }
            }
            else
            {
                this.ActorConfigs.Add(importedActor);
            }
        }

        this.Save();
        this.Reload();
    }

    public QuestPack ImportQuestPackFromPath(string path, bool overwriteExistingIds)
    {
        var normalizedPath = NormalizePackPath(path);
        var json = File.ReadAllText(normalizedPath, Encoding.UTF8);
        var pack = JsonSerializer.Deserialize<QuestPack>(json, this.jsonOptions)
                   ?? throw new InvalidOperationException($"无法读取任务包：{normalizedPath}");

        this.ImportQuestPack(pack, overwriteExistingIds);
        this.log.Information("Imported quest pack {PackId} from {Path}", pack.PackId, normalizedPath);
        return pack;
    }

    public CustomNpc CreateTestNpc(Vector3 playerPosition, uint territoryType)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var npc = new CustomNpc
        {
            Id = $"local-npc-{timestamp}",
            Name = "Yourcraft Actor",
            TerritoryType = ToTerritoryType(territoryType),
            Position = ToVector3Data(playerPosition),
            InteractRadius = 5f,
        };

        this.Npcs.Add(npc);
        this.Save();
        return npc;
    }

    public CustomQuest CreateTestQuestForNpc(CustomNpc npc)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var quest = new CustomQuest
        {
            Id = $"local-quest-{timestamp}",
            Title = $"{npc.Name} 的测试任务",
            GiverNpcId = npc.Id,
            Summary = "本地虚拟 NPC 测试任务。",
            StartDialogue =
            [
                "你能看见我吗？",
                "如果能，那说明 Yourcraft Actor 系统已经工作了。",
            ],
            AcceptText = "接受测试任务",
            Objectives =
            [
                new QuestObjective
                {
                    Id = "manual-confirm",
                    Type = QuestObjectiveType.Manual,
                    Description = "在任务编辑器中手动完成这个测试目标。",
                },
            ],
            ProgressDialogue =
            [
                "测试任务正在进行。",
            ],
            CompleteDialogue =
            [
                "很好，Yourcraft Actor 与任务对话都已经连通。",
            ],
            RewardsText = "奖励：Yourcraft Actor 系统测试通过。",
        };

        this.Quests.Add(quest);
        this.Save();
        return quest;
    }

    public CustomProp CreateTestProp(Vector3 playerPosition, uint territoryType)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var prop = new CustomProp
        {
            Id = $"local-prop-{timestamp}",
            Name = "本地场景物体",
            TerritoryType = ToTerritoryType(territoryType),
            Position = ToVector3Data(playerPosition),
            Rotation = 0f,
            Scale = 1f,
            ModelPath = "bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl",
            Visible = true,
        };

        this.Props.Add(prop);
        this.Save();
        return prop;
    }

    public void MoveNpcToPlayer(CustomNpc npc, Vector3 playerPosition, uint territoryType)
    {
        npc.TerritoryType = ToTerritoryType(territoryType);
        npc.Position = ToVector3Data(playerPosition);
        this.Save();
    }

    public void DeleteNpc(CustomNpc npc)
    {
        this.Npcs.Remove(npc);
        this.Save();
    }

    public void MovePropToPlayer(CustomProp prop, Vector3 playerPosition, uint territoryType)
    {
        prop.TerritoryType = ToTerritoryType(territoryType);
        prop.Position = ToVector3Data(playerPosition);
        this.Save();
    }

    public void DeleteProp(CustomProp prop)
    {
        this.Props.Remove(prop);
        this.Save();
    }

    public CustomQuest? GetQuestById(string questId)
        => this.Quests.FirstOrDefault(quest => string.Equals(quest.Id, questId, StringComparison.OrdinalIgnoreCase));

    public CustomNpc? GetNpcById(string npcId)
        => this.Npcs.FirstOrDefault(npc => string.Equals(npc.Id, npcId, StringComparison.OrdinalIgnoreCase));

    public CustomProp? GetPropById(string propId)
        => this.Props.FirstOrDefault(prop => string.Equals(prop.Id, propId, StringComparison.OrdinalIgnoreCase));

    private string CreateSampleJson()
    {
        var sample = new QuestDatabaseFile
        {
            Npcs =
            [
                new CustomNpc
                {
                    Id = "lqr-guide",
                    Name = "本地任务向导",
                    TerritoryType = 129,
                    Position = new Vector3Data { X = 0f, Y = 0f, Z = 0f },
                    InteractRadius = 8f,
                },
                new CustomNpc
                {
                    Id = "lqr-witness",
                    Name = "好奇的见证人",
                    TerritoryType = 129,
                    Position = new Vector3Data { X = 12f, Y = 0f, Z = 12f },
                    InteractRadius = 8f,
                },
            ],
            Quests =
            [
                new CustomQuest
                {
                    Id = "first-local-step",
                    Title = "本地任务第一步",
                    GiverNpcId = "lqr-guide",
                    Summary = "在不接入原生任务系统的情况下，测试本地任务闭环。",
                    StartDialogue =
                    [
                        "你找到我了。这说明本地任务运行时已经能读取你的位置。",
                        "接受这个试炼，然后移动到目标坐标，再和我的见证人交谈。",
                    ],
                    AcceptText = "开始本地试炼",
                    Objectives =
                    [
                        new QuestObjective
                        {
                            Id = "reach-test-point",
                            Type = QuestObjectiveType.ReachPosition,
                            Description = "到达附近的测试地点。",
                            TerritoryType = 129,
                            Position = new Vector3Data { X = 8f, Y = 0f, Z = 8f },
                            Radius = 8f,
                        },
                        new QuestObjective
                        {
                            Id = "talk-to-witness",
                            Type = QuestObjectiveType.TalkToNpc,
                            Description = "与好奇的见证人交谈。",
                            TargetNpcId = "lqr-witness",
                            TerritoryType = 129,
                            Radius = 8f,
                        },
                        new QuestObjective
                        {
                            Id = "manual-confirm",
                            Type = QuestObjectiveType.Manual,
                            Description = "在任务编辑器窗口中手动确认完成。",
                        },
                    ],
                    ProgressDialogue =
                    [
                        "试炼仍在进行。请查看任务追踪器中的当前步骤。",
                    ],
                    CompleteDialogue =
                    [
                        "完成了。一个完全在本地运行的任务闭环已经打通。",
                    ],
                    RewardsText = "奖励：信心、日志，以及一个能跑起来的原型。",
                },
            ],
            Props =
            [
                new CustomProp
                {
                    Id = "lqr-sample-prop",
                    Name = "本地场景物体样例",
                    TerritoryType = 129,
                    Position = new Vector3Data { X = 3f, Y = 0f, Z = 3f },
                    Rotation = 0f,
                    Scale = 1f,
                    ModelPath = "bg/ffxiv/sea_s1/fld/common/bgparts/s1f0_a0_oba03.mdl",
                    Visible = true,
                    Notes = "用于测试 Brio Prop flag 和模型路径 dump。",
                },
            ],
        };

        return JsonSerializer.Serialize(sample, this.jsonOptions);
    }

    private static Vector3Data ToVector3Data(Vector3 vector)
        => new() { X = vector.X, Y = vector.Y, Z = vector.Z };

    private static ushort ToTerritoryType(uint territoryType)
        => territoryType > ushort.MaxValue ? ushort.MaxValue : (ushort)territoryType;

    private static string CreateSafePackId(string? packName)
    {
        var baseName = string.IsNullOrWhiteSpace(packName) ? "local-quest-pack" : packName.Trim().ToLowerInvariant();
        var safe = new string(baseName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        safe = string.Join('-', safe.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return $"{safe}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
    }

    private static string NormalizePackPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("任务包路径不能为空。", nameof(path));

        if (Directory.Exists(trimmed))
            return Path.Combine(trimmed, $"local-quest-pack-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.lqrpack.json");

        if (!trimmed.EndsWith(".lqrpack.json", StringComparison.OrdinalIgnoreCase))
            trimmed += ".lqrpack.json";

        return Path.GetFullPath(trimmed);
    }

    private static string CreateUniqueId(string id, Func<string, bool> exists)
    {
        var baseId = string.IsNullOrWhiteSpace(id) ? "imported" : id;
        var index = 1;
        var candidate = $"{baseId}-imported";
        while (exists(candidate))
        {
            index++;
            candidate = $"{baseId}-imported-{index}";
        }

        return candidate;
    }

    private List<T> CloneViaJson<T>(List<T> value)
        => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(value, this.jsonOptions), this.jsonOptions) ?? [];

    private sealed class QuestDatabaseFile
    {
        public List<CustomNpc>? Npcs { get; set; }

        public List<PersistentActorConfig>? ActorConfigs { get; set; }

        public List<CustomQuest>? Quests { get; set; }

        public List<CustomProp>? Props { get; set; }
    }
}

public sealed record QuestPackFile(string Path, QuestPack Pack);
