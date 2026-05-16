using Dalamud.Plugin.Services;
using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed class ExperimentalEventNpcService
{
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly NativeGameObjectDumpService nativeGameObjectDump;
    private readonly IPluginLog log;

    public ExperimentalEventNpcService(
        BrioAssemblyBridgeService brioAssemblyBridge,
        NativeGameObjectDumpService nativeGameObjectDump,
        IPluginLog log)
    {
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.nativeGameObjectDump = nativeGameObjectDump;
        this.log = log;
    }

    public string LastResult { get; private set; } = "尚未执行 EventNpc 单字段 native 实验。";

    public bool TryWriteObjectKind(RuntimeActorInstance actor)
        => this.TryNativeWrite(actor, "ObjectKind=EventNpc", target => this.nativeGameObjectDump.TryWriteObjectKind(target, out _));

    public bool TryWriteSubKind(RuntimeActorInstance actor)
        => this.TryNativeWrite(actor, "SubKind=0", target => this.nativeGameObjectDump.TryWriteSubKind(target, out _));

    public bool TryWriteSubKind(RuntimeActorInstance actor, byte subKind)
        => this.TryNativeWrite(actor, $"SubKind={subKind}", target => this.nativeGameObjectDump.TryWriteSubKind(target, subKind, out _));

    public bool TryWriteIsTargetable(RuntimeActorInstance actor)
        => this.TryNativeWrite(actor, "TargetableStatus=IsTargetable|ReadyToDraw", target => this.nativeGameObjectDump.TryWriteTargetableStatus(target, out _));

    public bool TryWriteTargetableStatus(RuntimeActorInstance actor, byte targetableStatus)
        => this.TryNativeWrite(actor, $"TargetableStatus={targetableStatus}", target => this.nativeGameObjectDump.TryWriteTargetableStatus(target, targetableStatus, out _));

    public bool TryWriteDataId(RuntimeActorInstance actor, uint dataId)
        => this.TryNativeWrite(actor, $"BaseId/DataId={dataId}", target => this.nativeGameObjectDump.TryWriteBaseId(target, dataId, out _));

    public bool TryWriteGameObjectId(RuntimeActorInstance actor, uint objectId)
        => this.TryNativeWrite(actor, $"GameObjectId via BaseId={objectId}", target => this.nativeGameObjectDump.TryWriteGameObjectIdViaBaseId(target, objectId, out _));

    public bool TryWriteEntityId(RuntimeActorInstance actor, uint entityId)
        => this.TryNativeWrite(actor, $"EntityId={entityId}", target => this.nativeGameObjectDump.TryWriteEntityId(target, entityId, out _));

    public bool TryWriteRenderFlags(RuntimeActorInstance actor, ulong flags)
        => this.TryNativeWrite(actor, $"RenderFlags={flags}", target => this.nativeGameObjectDump.TryWriteRenderFlags(target, flags, out _));

    public bool TryWriteNamePlateIconId(RuntimeActorInstance actor, uint iconId)
        => this.TryNativeWrite(actor, $"NamePlateIconId={iconId}", target => this.nativeGameObjectDump.TryWriteNamePlateIconId(target, iconId, out _));

    public bool TryWriteNamePlateColorType(RuntimeActorInstance actor, uint colorType)
        => this.TryNativeWrite(actor, $"NamePlateColorType={colorType}", target => this.nativeGameObjectDump.TryWriteNamePlateColorType(target, colorType, out _));

    public bool TryCopyEventHandler(RuntimeActorInstance actor, nint eventHandler)
        => this.TryNativeWrite(actor, $"EventHandler=0x{eventHandler:X}", target => this.nativeGameObjectDump.TryCopyEventHandler(target, eventHandler, out _));

    public bool TryWriteHitbox(RuntimeActorInstance actor, float radius)
        => this.TryNativeWrite(actor, $"HitboxRadius={radius:F3}", target => this.nativeGameObjectDump.TryWriteHitboxRadius(target, radius, out _));

    public bool RefreshNameplate(RuntimeActorInstance actor)
    {
        this.LastResult = "NamePlateGui 注册/刷新接口尚未确认；本按钮当前只提示，不写 native 字段。";
        actor.HoverOrTargetDebugInfo = this.LastResult;
        return false;
    }

    private bool TryNativeWrite(RuntimeActorInstance actor, string operation, Func<RuntimeActorInstance, bool> write)
    {
        if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
        {
            this.LastResult = "UnsafeMode=false，EventNpc-like native 字段写入已禁用。";
            actor.HoverOrTargetDebugInfo = this.LastResult;
            return false;
        }

        try
        {
            var success = write(actor);
            this.LastResult = string.IsNullOrWhiteSpace(actor.HoverOrTargetDebugInfo)
                ? $"{operation} 已执行，但没有返回 readback。"
                : actor.HoverOrTargetDebugInfo;
            this.log.Information("[ExperimentalEventNpcService] {Operation}: {Result}", operation, this.LastResult);
            return success;
        }
        catch (Exception ex)
        {
            this.LastResult = $"{operation} 失败：{ex.Message}";
            actor.HoverOrTargetDebugInfo = this.LastResult;
            this.log.Warning(ex, "EventNpc native experiment failed. RuntimeId={RuntimeId}, Operation={Operation}", actor.RuntimeId, operation);
            return false;
        }
    }
}
