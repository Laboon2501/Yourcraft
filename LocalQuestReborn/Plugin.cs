using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using LocalQuestReborn.Services;
using LocalQuestReborn.UI;

namespace LocalQuestReborn;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/lqr";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WindowSystem windowSystem = new("Yourcraft");

    private readonly Configuration configuration;
    private readonly QuestDatabase database;
    private readonly QuestRuntimeService runtime;
    private readonly ExperimentalNpcService experimentalNpc;
    private readonly RealNpcNameService realNpcName;
    private readonly RuntimeActorRegistry runtimeActorRegistry;
    private readonly BrioIpcProbeService brioIpcProbe;
    private readonly BrioNpcBridgeService brioNpcBridge;
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly BrioCapabilityBridgeService brioCapabilityBridge;
    private readonly GlamourerIpcProbeService glamourerIpcProbe;
    private readonly GlamourerIpcBridgeService glamourerIpcBridge;
    private readonly PenumbraIpcService penumbraIpc;
    private readonly GlamourerStateApplyService glamourerStateApply;
    private readonly BrioHumanoidAppearanceApplyService brioHumanoidAppearanceApply;
    private readonly AppearanceApplyService appearanceApply;
    private readonly AppearanceApplyQueue appearanceApplyQueue;
    private readonly ActorAnimationService actorAnimation;
    private readonly ActorAnimationCatalogService actorAnimationCatalog;
    private readonly ActorAnimationPickerService actorAnimationPicker;
    private readonly ActorLipSyncPresetService actorLipSyncPresets;
    private readonly ActorNativeBubbleService actorBubble;
    private readonly ActorActionSequenceService actorActionSequence;
    private readonly ActorLookAtService actorLookAt;
    private readonly PlayerLookAtActorService playerLookAtActor;
    private readonly ActorValidityMonitorService actorValidityMonitor;
    private readonly ActorNameplateService actorNameplate;
    private readonly ActorTargetabilityService actorTargetability;
    private readonly TargetProbeService targetProbe;
    private readonly NativeNpcProbeService nativeNpcProbe;
    private readonly NativeGameObjectDumpService nativeGameObjectDump;
    private readonly ExperimentalEventNpcService experimentalEventNpcService;
    private readonly NativeTalkProbeService nativeTalkProbe;
    private readonly EventNpcHostService eventNpcHost;
    private readonly RealNpcSpawnService realNpcSpawn;
    private readonly BrioPropBridgeService brioPropBridge;
    private readonly PropModelService propModel;
    private readonly PropRuntimeService propRuntime;
    private readonly LayoutProbeService layoutProbe;
    private readonly LayoutInstanceTransformService layoutTransform;
    private readonly LayoutInstanceCloneService layoutClone;
    private readonly LayerDumpService layerDump;
    private readonly ProtectedBgPartRegistry protectedBgParts;
    private readonly PreferredModifyBgPartRegistry preferredModifyBgParts;
    private readonly LocalLayoutObjectService localLayoutObjects;
    private readonly LocalLightNativeService localLights;
    private readonly SceneEditorSelectionService sceneEditorSelection;
    private readonly SceneEditorService sceneEditor;
    private readonly BgPartVisualTransformProbeService bgPartVisualProbe;
    private readonly RotationMatrixExperimentService rotationMatrixExperiment;
    private readonly BgPartVisualRescueService bgPartVisualRescue;
    private readonly VisualOnlyRotationDeepProbeService visualOnlyRotationDeepProbe;
    private readonly DrawObjectUpdateDirtyProbeService drawObjectUpdateDirtyProbe;
    private readonly GraphicsSceneObjectTransformService graphicsSceneObjectTransform;
    private readonly BgPartCollisionSourceProbeService bgPartCollisionSourceProbe;
    private readonly AnimatedBgPartControllerProbeService animatedBgPartControllerProbe;
    private readonly StandaloneBgObjectProbeService standaloneBgObjectProbe;
    private readonly StandaloneRenderListProbeService standaloneRenderListProbe;
    private readonly MeddleStyleSceneProbeService meddleSceneProbe;
    private readonly GameNpcCatalogService gameNpcCatalog;
    private readonly GameNpcAppearanceResolver gameNpcAppearanceResolver;
    private readonly GameNpcAppearanceApplyService gameNpcAppearanceApply;
    private readonly ActorAppearanceLocalizerService actorAppearanceLocalizer;
    private readonly GlamourerDesignCatalogService glamourerDesignCatalog;
    private readonly ActionTimelinePickerWindow actionTimelinePickerWindow;
    private readonly SceneEditorOverlayWindow sceneEditorOverlayWindow;
    private readonly MainWindow mainWindow;
    private uint lastLayoutObjectTerritoryType;
    private bool lastPluginUiGposeState;
    private bool lastLocalLightGposeState;
    private bool lastLocalLightPlayerAvailable;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDataManager dataManager,
        IGameGui gameGui,
        IFramework framework,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;

        this.configuration = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Localization.CurrentLanguage = Localization.Normalize(this.configuration.UiLanguage);
        this.database = new QuestDatabase(pluginInterface, log);
        this.runtime = new QuestRuntimeService(clientState, objectTable, this.database);
        this.experimentalNpc = new ExperimentalNpcService(log);
        this.realNpcName = new RealNpcNameService(log);
        this.runtimeActorRegistry = new RuntimeActorRegistry();
        this.brioIpcProbe = new BrioIpcProbeService(pluginInterface, log);
        this.brioNpcBridge = new BrioNpcBridgeService(pluginInterface, this.brioIpcProbe, log);
        this.brioAssemblyBridge = new BrioAssemblyBridgeService(objectTable, log, this.realNpcName);
        this.brioCapabilityBridge = new BrioCapabilityBridgeService(log);
        this.glamourerIpcProbe = new GlamourerIpcProbeService(pluginInterface, log);
        this.glamourerIpcBridge = new GlamourerIpcBridgeService(pluginInterface, log);
        this.penumbraIpc = new PenumbraIpcService(pluginInterface, log);
        this.glamourerStateApply = new GlamourerStateApplyService(pluginInterface, log);
        this.brioHumanoidAppearanceApply = new BrioHumanoidAppearanceApplyService(dataManager, log);
        this.gameNpcCatalog = new GameNpcCatalogService(targetManager, clientState, dataManager, log);
        this.gameNpcAppearanceResolver = new GameNpcAppearanceResolver(dataManager, log);
        this.gameNpcAppearanceApply = new GameNpcAppearanceApplyService(this.brioHumanoidAppearanceApply, this.glamourerStateApply, log);
        this.actorAppearanceLocalizer = new ActorAppearanceLocalizerService(dataManager, this.gameNpcAppearanceResolver, log);
        this.appearanceApply = new AppearanceApplyService(pluginInterface, this.glamourerIpcProbe, this.glamourerIpcBridge, this.penumbraIpc, this.gameNpcAppearanceResolver, this.gameNpcAppearanceApply, log);
        this.appearanceApplyQueue = new AppearanceApplyQueue(this.database, this.runtimeActorRegistry, this.appearanceApply, log);
        this.actorAnimation = new ActorAnimationService(this.brioAssemblyBridge, log);
        this.actorAnimationCatalog = new ActorAnimationCatalogService(dataManager, log);
        this.actorAnimationPicker = new ActorAnimationPickerService(this.database, this.runtimeActorRegistry, this.actorAnimationCatalog, log);
        this.actorLipSyncPresets = new ActorLipSyncPresetService(this.actorAnimationCatalog);
        this.actorBubble = new ActorNativeBubbleService(this.brioAssemblyBridge, log);
        this.actorActionSequence = new ActorActionSequenceService(this.actorAnimation, this.actorBubble, this.brioCapabilityBridge, this.actorLipSyncPresets);
        this.actorLookAt = new ActorLookAtService(objectTable, this.brioAssemblyBridge, log);
        this.playerLookAtActor = new PlayerLookAtActorService(objectTable, this.brioAssemblyBridge, log);
        this.actorValidityMonitor = new ActorValidityMonitorService(clientState, objectTable);
        this.actorNameplate = new ActorNameplateService(this.brioAssemblyBridge, log);
        this.actorTargetability = new ActorTargetabilityService(targetManager, this.brioAssemblyBridge, log);
        this.targetProbe = new TargetProbeService(targetManager);
        this.nativeNpcProbe = new NativeNpcProbeService(targetManager);
        this.nativeGameObjectDump = new NativeGameObjectDumpService(objectTable, targetManager, log);
        this.experimentalEventNpcService = new ExperimentalEventNpcService(this.brioAssemblyBridge, this.nativeGameObjectDump, log);
        this.nativeTalkProbe = new NativeTalkProbeService(targetManager);
        this.eventNpcHost = new EventNpcHostService(targetManager, clientState, this.database);
        this.realNpcSpawn = new RealNpcSpawnService(clientState, targetManager, this.database, this.runtimeActorRegistry, this.brioNpcBridge, this.brioAssemblyBridge, this.brioCapabilityBridge, this.actorAppearanceLocalizer, this.appearanceApply, this.appearanceApplyQueue, this.actorAnimation, this.actorActionSequence, this.actorLipSyncPresets, this.actorLookAt, this.playerLookAtActor, this.actorValidityMonitor, this.actorNameplate, this.actorTargetability, this.targetProbe, this.nativeNpcProbe, this.nativeGameObjectDump, this.experimentalEventNpcService, this.nativeTalkProbe, this.glamourerIpcProbe, this.glamourerIpcBridge, this.penumbraIpc, log);
        this.realNpcSpawn.SetEventNpcHostService(this.eventNpcHost);
        this.realNpcSpawn.CleanupActorsForMissingNpcs();
        this.brioPropBridge = new BrioPropBridgeService(this.brioAssemblyBridge, log);
        this.propModel = new PropModelService(this.brioAssemblyBridge, log);
        this.propRuntime = new PropRuntimeService(objectTable, this.database, this.brioAssemblyBridge, this.brioPropBridge, this.propModel, log);
        this.layoutProbe = new LayoutProbeService(objectTable, log);
        this.layoutTransform = new LayoutInstanceTransformService();
        this.layoutClone = new LayoutInstanceCloneService();
        this.layerDump = new LayerDumpService();
        this.protectedBgParts = new ProtectedBgPartRegistry(this.configuration, () => this.runtime.TerritoryType, () => this.pluginInterface.SavePluginConfig(this.configuration));
        this.preferredModifyBgParts = new PreferredModifyBgPartRegistry(this.configuration, () => this.runtime.TerritoryType, () => this.pluginInterface.SavePluginConfig(this.configuration));
        this.localLayoutObjects = new LocalLayoutObjectService(this.protectedBgParts, this.preferredModifyBgParts);
        this.localLights = new LocalLightNativeService(this.configuration, log, () => this.pluginInterface.SavePluginConfig(this.configuration), () => this.runtime.TerritoryType);
        this.sceneEditorSelection = new SceneEditorSelectionService(log);
        this.sceneEditor = new SceneEditorService(
            this.realNpcSpawn,
            this.localLayoutObjects,
            this.localLights,
            this.sceneEditorSelection,
            objectTable,
            this.layoutProbe,
            () => this.runtime.PlayerPosition,
            this.configuration,
            () => this.runtime.TerritoryType,
            () => this.pluginInterface.SavePluginConfig(this.configuration),
            log);
        this.bgPartVisualProbe = new BgPartVisualTransformProbeService();
        this.rotationMatrixExperiment = new RotationMatrixExperimentService(this.bgPartVisualProbe);
        this.bgPartVisualRescue = new BgPartVisualRescueService(this.bgPartVisualProbe);
        this.visualOnlyRotationDeepProbe = new VisualOnlyRotationDeepProbeService();
        this.drawObjectUpdateDirtyProbe = new DrawObjectUpdateDirtyProbeService();
        this.graphicsSceneObjectTransform = new GraphicsSceneObjectTransformService(this.bgPartVisualProbe);
        this.bgPartCollisionSourceProbe = new BgPartCollisionSourceProbeService();
        this.animatedBgPartControllerProbe = new AnimatedBgPartControllerProbeService();
        this.standaloneBgObjectProbe = new StandaloneBgObjectProbeService();
        this.standaloneRenderListProbe = new StandaloneRenderListProbeService();
        this.meddleSceneProbe = new MeddleStyleSceneProbeService(objectTable, log);
        this.glamourerDesignCatalog = new GlamourerDesignCatalogService(pluginInterface, log);
        this.lastLayoutObjectTerritoryType = clientState.TerritoryType;
        this.lastPluginUiGposeState = clientState.IsGPosing;
        this.lastLocalLightGposeState = clientState.IsGPosing;
        this.lastLocalLightPlayerAvailable = this.runtime.PlayerPosition.HasValue;
        this.ApplyGposeUiVisibilityPolicy();

        this.actionTimelinePickerWindow = new ActionTimelinePickerWindow(this.actorAnimationPicker);
        this.sceneEditorOverlayWindow = new SceneEditorOverlayWindow(gameGui, this.sceneEditor, this.sceneEditorSelection);
        this.mainWindow = new MainWindow(this.configuration, this.database, this.runtime, this.experimentalNpc, this.realNpcSpawn, this.propRuntime, this.layoutProbe, this.layoutTransform, this.layoutClone, this.layerDump, this.localLayoutObjects, this.localLights, this.sceneEditor, this.sceneEditorSelection, this.bgPartVisualProbe, this.rotationMatrixExperiment, this.bgPartVisualRescue, this.visualOnlyRotationDeepProbe, this.drawObjectUpdateDirtyProbe, this.graphicsSceneObjectTransform, this.bgPartCollisionSourceProbe, this.animatedBgPartControllerProbe, this.standaloneBgObjectProbe, this.standaloneRenderListProbe, this.meddleSceneProbe, this.gameNpcCatalog, this.gameNpcAppearanceResolver, this.glamourerDesignCatalog, this.actorAnimationPicker, this.actorLipSyncPresets, this.actionTimelinePickerWindow, this.penumbraIpc, this.Reload, () => this.pluginInterface.SavePluginConfig(this.configuration), () => this.clientState.IsGPosing);

        this.windowSystem.AddWindow(this.mainWindow);
        this.windowSystem.AddWindow(this.actionTimelinePickerWindow);
        this.windowSystem.AddWindow(this.sceneEditorOverlayWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open Yourcraft. Subcommand: reload refreshes the saved configuration.",
        });

        this.InitializeDefaultRuntimeSettings();

        this.framework.Update += this.OnFrameworkUpdate;
        this.pluginInterface.UiBuilder.Draw += this.DrawUi;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;

        this.log.Information("Yourcraft initialized");
    }

    private void InitializeDefaultRuntimeSettings()
    {
        try
        {
            this.realNpcSpawn.EnableUnsafeNativeWrites = true;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to enable unsafe native writes by default.");
        }

        try
        {
            this.realNpcSpawn.ProbeBrioIpc();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Startup Brio IPC probe failed.");
        }

        try
        {
            this.realNpcSpawn.ProbeGlamourerIpc();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Startup Glamourer/Penumbra IPC probe failed.");
        }

        this.sceneEditor.RequestRestore("PluginStart");
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        this.commandManager.RemoveHandler(CommandName);
        this.sceneEditor.FlushPersistence();
        this.localLights.DestroyAllNative("插件卸载", keepInstances: true);
        this.standaloneBgObjectProbe.MarkAllInvalid("插件卸载：Standalone 对象没有安全销毁入口，已停止写入并标记失效。");
        this.localLayoutObjects.NotifySceneChanging("PluginDispose");
        this.layoutProbe.ClearRuntimeCache("PluginDispose");
        this.realNpcSpawn.DespawnAll();
        this.penumbraIpc.Dispose();
        this.propRuntime.DespawnAll();
        this.brioAssemblyBridge.DespawnAll(out _);
        this.windowSystem.RemoveAllWindows();
    }

    private void OnCommand(string command, string arguments)
    {
        var arg = arguments.Trim();
        if (arg.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            this.Reload();
            return;
        }

        this.mainWindow.Toggle();
    }

    private void Reload()
    {
        this.database.Reload();
        this.realNpcSpawn.CleanupActorsForMissingNpcs();
        this.propRuntime.CleanupPropsForMissingConfigs();
        this.log.Information("Reloaded Yourcraft configuration");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.ApplyGposeUiVisibilityPolicy();
        this.runtime.Update();
        this.penumbraIpc.Update();
        if (this.penumbraIpc.ConsumeReapplyRequested())
            this.realNpcSpawn.ApplyAllNpcAppearances();
        this.realNpcSpawn.Update();
        this.localLayoutObjects.Update();
        this.localLights.Update();
        this.standaloneBgObjectProbe.Update();
        this.animatedBgPartControllerProbe.Update();
        if (this.clientState.IsGPosing != this.lastLocalLightGposeState)
        {
            this.sceneEditor.NotifySceneChanging(this.clientState.IsGPosing ? "GPoseEnter" : "GPoseExit");
            this.localLayoutObjects.NotifySceneChanging(this.clientState.IsGPosing ? "GPoseEnter" : "GPoseExit");
            this.layoutProbe.ClearRuntimeCache(this.clientState.IsGPosing ? "GPoseEnter" : "GPoseExit");
            this.localLights.DestroyAllNative("GPose enter/exit，销毁 native light 并等待重建", keepInstances: true);
            this.lastLocalLightGposeState = this.clientState.IsGPosing;
        }

        if (this.clientState.IsGPosing != this.lastPluginUiGposeState)
        {
            this.log.Information("[UI] GPose {State}: keep plugin UI visible={Visible}", this.clientState.IsGPosing ? "entered" : "exited", this.configuration.ShowPluginUiInGpose);
            if (this.lastPluginUiGposeState && !this.clientState.IsGPosing)
                this.sceneEditor.RequestRestore("GPoseExit");
            this.lastPluginUiGposeState = this.clientState.IsGPosing;
        }

        var playerAvailable = this.runtime.PlayerPosition.HasValue;
        if (!playerAvailable && this.lastLocalLightPlayerAvailable)
            this.localLights.DestroyAllNative("LocalPlayer 不可用/可能登出，销毁 native light", keepInstances: true);
        this.lastLocalLightPlayerAvailable = playerAvailable;

        if (this.runtime.TerritoryType != this.lastLayoutObjectTerritoryType)
        {
            this.sceneEditor.NotifySceneChanging($"TerritoryChanging:{this.lastLayoutObjectTerritoryType}->{this.runtime.TerritoryType}");
            this.localLayoutObjects.NotifySceneChanging($"TerritoryChanging:{this.lastLayoutObjectTerritoryType}->{this.runtime.TerritoryType}");
            this.layoutProbe.ClearRuntimeCache($"TerritoryChanging:{this.lastLayoutObjectTerritoryType}->{this.runtime.TerritoryType}");
            this.localLights.DestroyAllNative("区域切换，销毁 native light 并等待重建", keepInstances: true);
            this.standaloneBgObjectProbe.MarkAllInvalid("区域切换：Standalone 对象指针可能已由游戏清理，已停止写入并标记失效。");
            this.localLayoutObjects.RequestRestoreAllAndClear("TerritoryChanged");
            this.lastLayoutObjectTerritoryType = this.runtime.TerritoryType;
            this.sceneEditor.RequestRestore("TerritoryChanged");
        }

        this.localLayoutObjects.UpdateRestoreAllAndClearQueue(playerAvailable && this.runtime.TerritoryType != 0 && !this.clientState.IsGPosing, this.runtime.TerritoryType);
        this.sceneEditor.UpdateRestoreQueue(playerAvailable && this.runtime.TerritoryType != 0 && !this.clientState.IsGPosing);
    }

    private void DrawUi()
    {
        this.ApplyGposeUiVisibilityPolicy();
        this.windowSystem.Draw();
    }

    private void OpenMainUi()
        => this.mainWindow.IsOpen = true;

    private void OpenConfigUi()
        => this.mainWindow.IsOpen = true;

    private void ApplyGposeUiVisibilityPolicy()
        => this.pluginInterface.UiBuilder.DisableGposeUiHide = this.configuration.ShowPluginUiInGpose;

}
