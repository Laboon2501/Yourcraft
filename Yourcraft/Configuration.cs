using Dalamud.Configuration;
using Yourcraft.Models;

namespace Yourcraft;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string UiLanguage { get; set; } = "zh";

    public bool ShowPluginUiInGpose { get; set; } = true;

    public List<ProtectedBgPartSlot> ProtectedBgPartSlots { get; set; } = [];

    public List<ProtectedBgPartResourcePath> ProtectedBgPartResourcePaths { get; set; } = [];

    public List<PreferredModifyBgPartSlot> PreferredModifyBgPartSlots { get; set; } = [];

    public List<PreferredModifyBgPartResourcePath> PreferredModifyBgPartResourcePaths { get; set; } = [];

    public List<SceneEditorNativeModificationRecord> SceneEditorNativeModifications { get; set; } = [];

    public List<SceneEditorLocalBgPartRecord> SceneEditorLocalBgParts { get; set; } = [];

    public List<SceneEditorLocalActorRecord> SceneEditorLocalActors { get; set; } = [];

    public List<LocalLightInstance> LocalLights { get; set; } = [];
}
