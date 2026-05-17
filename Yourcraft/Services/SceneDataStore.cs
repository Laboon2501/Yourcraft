using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yourcraft.Services;

public sealed class SceneDataStore
{
    private const string DataFileName = "scene-data.json";
    private const string LegacyDataFileName = "quests.json";
    private const string DevelopmentDataDirectory =
        @"C:\Users\kiomo\Documents\New project\Yourcraft\Data";

    private readonly IPluginLog log;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        IncludeFields = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public SceneDataStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        var developmentDataFilePath = Path.Combine(DevelopmentDataDirectory, DataFileName);
        var developmentLegacyDataFilePath = Path.Combine(DevelopmentDataDirectory, LegacyDataFileName);
        var fallbackDataDirectory = Path.Combine(pluginInterface.ConfigDirectory.FullName, "Data");
        var fallbackDataFilePath = Path.Combine(fallbackDataDirectory, DataFileName);
        var fallbackLegacyDataFilePath = Path.Combine(fallbackDataDirectory, LegacyDataFileName);

        if (File.Exists(developmentDataFilePath) || File.Exists(developmentLegacyDataFilePath))
        {
            this.DataDirectory = DevelopmentDataDirectory;
            this.DataFilePath = developmentDataFilePath;
            this.LegacyDataFilePath = developmentLegacyDataFilePath;
            this.IsUsingDevelopmentDataPath = true;
        }
        else
        {
            this.DataDirectory = fallbackDataDirectory;
            this.DataFilePath = fallbackDataFilePath;
            this.LegacyDataFilePath = fallbackLegacyDataFilePath;
            this.IsUsingDevelopmentDataPath = false;
        }

        this.Reload();
    }

    public bool IsUsingDevelopmentDataPath { get; }

    public string DataDirectory { get; }

    public string DataFilePath { get; }

    public string LegacyDataFilePath { get; }

    public List<CustomNpc> Npcs { get; private set; } = [];

    public List<PersistentActorConfig> ActorConfigs { get; private set; } = [];

    public void Reload()
    {
        Directory.CreateDirectory(this.DataDirectory);

        var loadPath = File.Exists(this.DataFilePath)
            ? this.DataFilePath
            : File.Exists(this.LegacyDataFilePath)
                ? this.LegacyDataFilePath
                : string.Empty;

        if (string.IsNullOrWhiteSpace(loadPath))
        {
            File.WriteAllText(this.DataFilePath, CreateEmptyJson(), Encoding.UTF8);
            loadPath = this.DataFilePath;
            this.log.Information("Created Yourcraft data file at {Path}", this.DataFilePath);
        }

        try
        {
            var json = File.ReadAllText(loadPath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<YourcraftDataFile>(json, this.jsonOptions) ?? new YourcraftDataFile();
            this.Npcs = data.Npcs ?? [];
            this.ActorConfigs = data.ActorConfigs ?? [];
            this.log.Information(
                "Loaded {NpcCount} NPC templates and {ActorConfigCount} actor configs from {Path}. UsingDevelopmentPath={UsingDevelopmentPath}",
                this.Npcs.Count,
                this.ActorConfigs.Count,
                loadPath,
                this.IsUsingDevelopmentDataPath);

            if (!string.Equals(loadPath, this.DataFilePath, StringComparison.OrdinalIgnoreCase))
            {
                this.Save();
                this.log.Information("Migrated legacy Yourcraft data from {LegacyPath} to {Path}", loadPath, this.DataFilePath);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to load Yourcraft data from {Path}", loadPath);
            this.Npcs = [];
            this.ActorConfigs = [];
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(this.DataDirectory);
        var data = new YourcraftDataFile
        {
            Npcs = this.Npcs,
            ActorConfigs = this.ActorConfigs,
        };
        var json = JsonSerializer.Serialize(data, this.jsonOptions);
        File.WriteAllText(this.DataFilePath, json, Encoding.UTF8);
        this.log.Information("Saved Yourcraft data to {Path}", this.DataFilePath);
    }

    public CustomNpc? GetNpcById(string npcId)
        => this.Npcs.FirstOrDefault(npc => string.Equals(npc.Id, npcId, StringComparison.OrdinalIgnoreCase));

    private string CreateEmptyJson()
    {
        var sample = new YourcraftDataFile
        {
            Npcs = [],
            ActorConfigs = [],
        };

        return JsonSerializer.Serialize(sample, this.jsonOptions);
    }

    private sealed class YourcraftDataFile
    {
        public List<CustomNpc>? Npcs { get; set; }

        public List<PersistentActorConfig>? ActorConfigs { get; set; }
    }
}
