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
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WindowSystem windowSystem = new("本地 NPC 实验室");

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
    private readonly GlamourerStateApplyService glamourerStateApply;
    private readonly BrioHumanoidAppearanceApplyService brioHumanoidAppearanceApply;
    private readonly AppearanceApplyService appearanceApply;
    private readonly AppearanceApplyQueue appearanceApplyQueue;
    private readonly ActorAnimationService actorAnimation;
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
    private readonly LocalLayoutObjectService localLayoutObjects;
    private readonly BgPartVisualTransformProbeService bgPartVisualProbe;
    private readonly RotationMatrixExperimentService rotationMatrixExperiment;
    private readonly BgPartVisualRescueService bgPartVisualRescue;
    private readonly VisualOnlyRotationDeepProbeService visualOnlyRotationDeepProbe;
    private readonly DrawObjectUpdateDirtyProbeService drawObjectUpdateDirtyProbe;
    private readonly GraphicsSceneObjectTransformService graphicsSceneObjectTransform;
    private readonly BgPartCollisionSourceProbeService bgPartCollisionSourceProbe;
    private readonly AnimatedBgPartControllerProbeService animatedBgPartControllerProbe;
    private readonly MeddleStyleSceneProbeService meddleSceneProbe;
    private readonly GameNpcCatalogService gameNpcCatalog;
    private readonly GameNpcAppearanceResolver gameNpcAppearanceResolver;
    private readonly GameNpcAppearanceApplyService gameNpcAppearanceApply;
    private readonly GlamourerDesignCatalogService glamourerDesignCatalog;
    private readonly MainWindow mainWindow;
    private uint lastLayoutObjectTerritoryType;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDataManager dataManager,
        IFramework framework,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.log = log;

        this.configuration = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
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
        this.glamourerStateApply = new GlamourerStateApplyService(pluginInterface, log);
        this.brioHumanoidAppearanceApply = new BrioHumanoidAppearanceApplyService(dataManager, log);
        this.gameNpcCatalog = new GameNpcCatalogService(targetManager, clientState, dataManager, log);
        this.gameNpcAppearanceResolver = new GameNpcAppearanceResolver(dataManager, log);
        this.gameNpcAppearanceApply = new GameNpcAppearanceApplyService(this.brioHumanoidAppearanceApply, this.glamourerStateApply, log);
        this.appearanceApply = new AppearanceApplyService(pluginInterface, this.glamourerIpcProbe, this.glamourerIpcBridge, this.gameNpcAppearanceResolver, this.gameNpcAppearanceApply, log);
        this.appearanceApplyQueue = new AppearanceApplyQueue(this.database, this.runtimeActorRegistry, this.appearanceApply, log);
        this.actorAnimation = new ActorAnimationService(this.brioAssemblyBridge, log);
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
        this.realNpcSpawn = new RealNpcSpawnService(clientState, targetManager, this.database, this.runtimeActorRegistry, this.brioNpcBridge, this.brioAssemblyBridge, this.brioCapabilityBridge, this.appearanceApply, this.appearanceApplyQueue, this.actorAnimation, this.actorLookAt, this.playerLookAtActor, this.actorValidityMonitor, this.actorNameplate, this.actorTargetability, this.targetProbe, this.nativeNpcProbe, this.nativeGameObjectDump, this.experimentalEventNpcService, this.nativeTalkProbe, this.glamourerIpcProbe, this.glamourerIpcBridge, log);
        this.realNpcSpawn.SetEventNpcHostService(this.eventNpcHost);
        this.brioPropBridge = new BrioPropBridgeService(this.brioAssemblyBridge, log);
        this.propModel = new PropModelService(this.brioAssemblyBridge, log);
        this.propRuntime = new PropRuntimeService(objectTable, this.database, this.brioAssemblyBridge, this.brioPropBridge, this.propModel, log);
        this.layoutProbe = new LayoutProbeService(objectTable, log);
        this.layoutTransform = new LayoutInstanceTransformService();
        this.layoutClone = new LayoutInstanceCloneService();
        this.layerDump = new LayerDumpService();
        this.localLayoutObjects = new LocalLayoutObjectService();
        this.bgPartVisualProbe = new BgPartVisualTransformProbeService();
        this.rotationMatrixExperiment = new RotationMatrixExperimentService(this.bgPartVisualProbe);
        this.bgPartVisualRescue = new BgPartVisualRescueService(this.bgPartVisualProbe);
        this.visualOnlyRotationDeepProbe = new VisualOnlyRotationDeepProbeService();
        this.drawObjectUpdateDirtyProbe = new DrawObjectUpdateDirtyProbeService();
        this.graphicsSceneObjectTransform = new GraphicsSceneObjectTransformService(this.bgPartVisualProbe);
        this.bgPartCollisionSourceProbe = new BgPartCollisionSourceProbeService();
        this.animatedBgPartControllerProbe = new AnimatedBgPartControllerProbeService();
        this.meddleSceneProbe = new MeddleStyleSceneProbeService(objectTable, log);
        this.glamourerDesignCatalog = new GlamourerDesignCatalogService(pluginInterface, log);
        this.lastLayoutObjectTerritoryType = clientState.TerritoryType;

        this.mainWindow = new MainWindow(this.configuration, this.database, this.runtime, this.experimentalNpc, this.realNpcSpawn, this.propRuntime, this.layoutProbe, this.layoutTransform, this.layoutClone, this.layerDump, this.localLayoutObjects, this.bgPartVisualProbe, this.rotationMatrixExperiment, this.bgPartVisualRescue, this.visualOnlyRotationDeepProbe, this.drawObjectUpdateDirtyProbe, this.graphicsSceneObjectTransform, this.bgPartCollisionSourceProbe, this.animatedBgPartControllerProbe, this.meddleSceneProbe, this.gameNpcCatalog, this.gameNpcAppearanceResolver, this.glamourerDesignCatalog, this.Reload);

        this.windowSystem.AddWindow(this.mainWindow);

        this.commandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开本地 NPC 实验室。子命令：reload 重新读取 NPC 配置。",
        });

        this.InitializeDefaultRuntimeSettings();

        this.framework.Update += this.OnFrameworkUpdate;
        this.pluginInterface.UiBuilder.Draw += this.DrawUi;
        this.pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;

        this.log.Information("LocalQuestReborn initialized");
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
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.pluginInterface.UiBuilder.Draw -= this.DrawUi;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        this.commandManager.RemoveHandler(CommandName);
        this.localLayoutObjects.RestoreAllAndClear();
        this.realNpcSpawn.DespawnAll();
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
        this.log.Information("Reloaded LocalQuestReborn NPC database");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.runtime.Update();
        this.realNpcSpawn.Update();
        this.localLayoutObjects.Update();
        this.animatedBgPartControllerProbe.Update();
        if (this.runtime.TerritoryType != this.lastLayoutObjectTerritoryType)
        {
            this.localLayoutObjects.RestoreAllAndClear();
            this.lastLayoutObjectTerritoryType = this.runtime.TerritoryType;
        }
    }

    private void DrawUi()
        => this.windowSystem.Draw();

    private void OpenMainUi()
        => this.mainWindow.IsOpen = true;

    private void OpenConfigUi()
        => this.mainWindow.IsOpen = true;

}
