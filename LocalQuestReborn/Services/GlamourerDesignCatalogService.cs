using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Text;
using System.Text.Json;

namespace LocalQuestReborn.Services;

public sealed class GlamourerDesignCatalogService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly List<GlamourerDesignEntry> designs = [];

    public GlamourerDesignCatalogService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.ManualConfigDirectory = string.Empty;
        this.Scan();
    }

    public IReadOnlyList<GlamourerDesignEntry> Designs => this.designs;

    public string ManualConfigDirectory { get; set; }

    public string LastScanMessage { get; private set; } = string.Empty;

    public IReadOnlyList<string> LastScannedDirectories { get; private set; } = [];

    public IReadOnlyList<GlamourerDesignEntry> Search(string query, int maxResults = 80)
    {
        var trimmed = query.Trim();
        IEnumerable<GlamourerDesignEntry> source = this.designs;
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            source = source.Where(design =>
                design.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                design.Identifier.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                design.FilePath.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                design.SourceDescription.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        return source
            .OrderBy(design => design.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(design => design.Identifier, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    public void ApplyDesignToNpc(CustomNpc npc, GlamourerDesignEntry design)
    {
        npc.Appearance.SourceType = CustomNpcAppearanceSourceType.GlamourerDesign;
        npc.Appearance.DisplayName = design.Name;
        npc.Appearance.GlamourerDesignId = design.Identifier;
        npc.Appearance.Notes = design.FilePath;
    }

    public void Scan()
    {
        this.designs.Clear();
        var directories = GetCandidateDirectories().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        this.LastScannedDirectories = directories;

        var filesRead = 0;
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in EnumeratePossibleGlamourerFiles(directory))
            {
                filesRead++;
                this.ReadDesignFile(file);
            }
        }

        this.DeduplicateDesigns();
        this.LastScanMessage = $"已扫描 {directories.Count} 个目录、{filesRead} 个 JSON 文件，找到 {this.designs.Count} 个候选 Glamourer 设计。";
        this.log.Information("Scanned Glamourer designs. Directories={DirectoryCount}, Files={FileCount}, Designs={DesignCount}", directories.Count, filesRead, this.designs.Count);
    }

    private IEnumerable<string> GetCandidateDirectories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "XIVLauncher", "pluginConfigs");
            yield return Path.Combine(appData, "XIVLauncherCN", "pluginConfigs");
        }

        var configDirectory = this.pluginInterface.ConfigDirectory.FullName;
        var parent = Directory.GetParent(configDirectory);
        if (parent != null)
            yield return parent.FullName;

        if (!string.IsNullOrWhiteSpace(this.ManualConfigDirectory))
            yield return this.ManualConfigDirectory.Trim();
    }

    private static IEnumerable<string> EnumeratePossibleGlamourerFiles(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var pathHasGlamourerDirectory = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Contains("Glamourer", StringComparison.OrdinalIgnoreCase));

            if (fileName.Contains("Glamourer", StringComparison.OrdinalIgnoreCase) || pathHasGlamourerDirectory)
                yield return file;
        }
    }

    private void ReadDesignFile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            using var document = JsonDocument.Parse(json);
            var before = this.designs.Count;
            this.VisitElement(document.RootElement, filePath, "$", 0);

            if (this.designs.Count == before)
                this.designs.Add(CreateFallbackEntry(filePath, json));
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to parse Glamourer design candidate {Path}", filePath);
        }
    }

    private void VisitElement(JsonElement element, string filePath, string path, int depth)
    {
        if (depth > 12)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                this.TryAddDesignFromObject(element, filePath, path);
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object && Guid.TryParse(property.Name, out _))
                        this.TryAddDesignFromObject(property.Value, filePath, $"{path}.{property.Name}", property.Name);

                    this.VisitElement(property.Value, filePath, $"{path}.{property.Name}", depth + 1);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    this.VisitElement(item, filePath, $"{path}[{index}]", depth + 1);
                    index++;
                }

                break;
        }
    }

    private void TryAddDesignFromObject(JsonElement element, string filePath, string path, string fallbackIdentifier = "")
    {
        var name = ReadFirstString(element, "Name", "name", "DesignName", "Label", "DisplayName");
        var identifier = ReadFirstString(element, "Identifier", "identifier", "Guid", "GUID", "Id", "ID", "DesignId");

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(identifier))
            return;

        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(filePath);

        if (string.IsNullOrWhiteSpace(identifier))
            identifier = string.IsNullOrWhiteSpace(fallbackIdentifier) ? name : fallbackIdentifier;

        this.designs.Add(new GlamourerDesignEntry
        {
            Name = name,
            Identifier = identifier,
            FilePath = filePath,
            RawJsonPreview = CreatePreview(element),
            SourceDescription = $"从 JSON 节点 {path} 读取",
        });
    }

    private static GlamourerDesignEntry CreateFallbackEntry(string filePath, string json)
        => new()
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            Identifier = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            RawJsonPreview = json.Length > 600 ? json[..600] : json,
            SourceDescription = "无法识别固定结构，使用文件名作为候选项",
        };

    private void DeduplicateDesigns()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<GlamourerDesignEntry>();
        foreach (var design in this.designs)
        {
            var key = $"{design.Identifier}|{design.FilePath}";
            if (!seen.Add(key))
                continue;

            unique.Add(design);
        }

        this.designs.Clear();
        this.designs.AddRange(unique);
    }

    private static string ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value))
                continue;

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return string.Empty;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string CreatePreview(JsonElement element)
    {
        var preview = element.GetRawText();
        return preview.Length > 600 ? preview[..600] : preview;
    }
}
