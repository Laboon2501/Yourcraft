using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using LocalQuestReborn.Models;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace LocalQuestReborn.Services;

public sealed unsafe class NativeGameObjectDumpService
{
    private unsafe delegate string NativeWriteAction(NativeGameObject* native);

    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IPluginLog log;

    public NativeGameObjectDumpService(IObjectTable objectTable, ITargetManager targetManager, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.log = log;
    }

    public NativeNpcProbeSnapshot? ReferenceSnapshot { get; private set; }

    public NativeNpcProbeSnapshot? ActorSnapshot { get; private set; }

    public string LastComparison { get; private set; } = "尚未执行 native GameObject 对比。";

    public string LastManualTestResult { get; private set; } = "尚未记录人工测试结果。";

    public NativeNpcProbeSnapshot DumpCurrentTarget()
    {
        this.ReferenceSnapshot = this.Capture(this.targetManager.Target, "真实目标 Native GameObject");
        return this.ReferenceSnapshot;
    }

    public NativeNpcProbeSnapshot DumpActor(RuntimeActorInstance actor)
    {
        this.ActorSnapshot = this.Capture(actor.CharacterObject, "Brio Actor Native GameObject");
        this.ActorSnapshot.Fields["RuntimeId"] = new(actor.RuntimeId, "LocalQuestReborn RuntimeActorInstance");
        this.ActorSnapshot.Fields["NpcId"] = new(actor.NpcId, "LocalQuestReborn RuntimeActorInstance");
        this.ActorSnapshot.Fields["Actor.ObjectIndex"] = new(actor.ObjectIndex, "LocalQuestReborn RuntimeActorInstance");
        this.ActorSnapshot.Fields["Actor.Address"] = new(actor.Address, "LocalQuestReborn RuntimeActorInstance");
        return this.ActorSnapshot;
    }

    public string Compare()
    {
        if (this.ReferenceSnapshot == null || this.ActorSnapshot == null)
        {
            this.LastComparison = "需要先保存真实 NPC native dump 和 Brio Actor native dump。";
            return this.LastComparison;
        }

        var lines = new List<string> { "Native GameObject 字段差异：" };
        var keys = this.ReferenceSnapshot.Fields.Keys
            .Union(this.ActorSnapshot.Fields.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var left = this.ReferenceSnapshot.Fields.GetValueOrDefault(key, new("未读取", "未确认"));
            var right = this.ActorSnapshot.Fields.GetValueOrDefault(key, new("未读取", "未确认"));
            if (!string.Equals(left.Value, right.Value, StringComparison.Ordinal))
                lines.Add($"- {key}: 参考={left.Value} [{left.Source}] / Actor={right.Value} [{right.Source}]");
        }

        lines.Add("");
        lines.Add("优先观察：ObjectKind、SubKind、TargetableStatus、GetIsTargetable、BaseId/DataId、HitboxRadius、EventHandler、NameId、NamePlateIconId。");
        lines.Add("写入按钮只改单字段；Targetable 默认写 IsTargetable|ReadyToDraw，参考 NPC 写入以 dump 值为准。");
        this.LastComparison = string.Join(Environment.NewLine, lines);
        return this.LastComparison;
    }

    public NativeNpcProbeSnapshot Capture(object? source, string label)
    {
        var snapshot = new NativeNpcProbeSnapshot { Label = label };
        if (source == null)
        {
            snapshot.Fields["错误"] = new("对象为空", "NativeGameObjectDumpService");
            return snapshot;
        }

        snapshot.Fields["ManagedType"] = new(source.GetType().FullName ?? source.GetType().Name, "反射读取");
        snapshot.Fields["Managed.Name"] = new(ReadManagedMember(source, "Name"), "Dalamud/Brio public or reflected member");
        snapshot.Fields["Managed.ObjectIndex"] = new(ReadManagedMember(source, "ObjectIndex", "ObjectTableIndex", "Index"), "Dalamud/Brio public or reflected member");
        snapshot.Fields["Managed.DataId"] = new(ReadManagedMember(source, "DataId"), "Dalamud public property if present");
        snapshot.Fields["Managed.IsTargetable"] = new(ReadManagedMember(source, "IsTargetable", "Targetable"), "Dalamud public property if present");
        snapshot.Fields["Managed.Position"] = new(ReadManagedMember(source, "Position"), "Dalamud/Brio public or reflected member");

        if (!TryGetAddress(source, out var address))
        {
            snapshot.Fields["NativeAddress"] = new("未找到 Address", "反射读取");
            return snapshot;
        }

        snapshot.Fields["NativeAddress"] = new($"0x{address:X}", "Address -> FFXIVClientStructs GameObject*");
        try
        {
            var native = (NativeGameObject*)address;
            Add(snapshot, "ObjectKind", native->ObjectKind, nameof(NativeGameObject.ObjectKind));
            Add(snapshot, "SubKind", native->SubKind, nameof(NativeGameObject.SubKind));
            Add(snapshot, "TargetableStatus", native->TargetableStatus, nameof(NativeGameObject.TargetableStatus));
            Add(snapshot, "GetIsTargetable()", native->GetIsTargetable(), "method:GetIsTargetable");
            Add(snapshot, "RenderFlags", native->RenderFlags, nameof(NativeGameObject.RenderFlags));
            Add(snapshot, "GameObjectId", native->GetGameObjectId(), "method:GetGameObjectId");
            Add(snapshot, "GameObjectId原始/方法", native->GetGameObjectId(), "method:GetGameObjectId");
            Add(snapshot, "EntityId", native->EntityId, nameof(NativeGameObject.EntityId));
            Add(snapshot, "EntityId原始值", native->EntityId, nameof(NativeGameObject.EntityId));
            Add(snapshot, "BaseId/DataId", native->BaseId, nameof(NativeGameObject.BaseId));
            Add(snapshot, "BaseId原始值", native->BaseId, nameof(NativeGameObject.BaseId));
            Add(snapshot, "ObjectTableIndex", native->ObjectIndex, nameof(NativeGameObject.ObjectIndex));
            Add(snapshot, "HitboxRadius", native->HitboxRadius, nameof(NativeGameObject.HitboxRadius));
            Add(snapshot, "NameId", native->GetNameId(), "method:GetNameId");
            Add(snapshot, "NamePlateIconId", native->NamePlateIconId, nameof(NativeGameObject.NamePlateIconId));
            Add(snapshot, "EventHandler", $"0x{(nint)native->EventHandler:X}", nameof(NativeGameObject.EventHandler));
            Add(snapshot, "DrawObject", $"0x{(nint)native->DrawObject:X}", nameof(NativeGameObject.DrawObject));
            Add(snapshot, "DrawObject非空", native->DrawObject != null, nameof(NativeGameObject.DrawObject));
            Add(snapshot, "Position", native->Position, nameof(NativeGameObject.Position));
            Add(snapshot, "Position有效", IsPositionReasonable(native->Position), nameof(NativeGameObject.Position));
            Add(snapshot, "IsReadyToDraw()", native->IsReadyToDraw(), "method:IsReadyToDraw");
            Add(snapshot, "IsDead()", native->IsDead(), "method:IsDead");
            Add(snapshot, "NamePlateColorType", native->GetNamePlateColorType(), "method:GetNamePlateColorType");
            Add(snapshot, "TargetObjectId", this.TryReadTargetObjectId(address), "Character.GetTargetId if object is Character");
            Add(snapshot, "NameBuffer", TryReadNativeName(source), "Character.NameString / reflected Name fallback");
            this.AddObjectTableFields(snapshot, native, address);
            this.AddTargetManagerFields(snapshot);
        }
        catch (Exception ex)
        {
            snapshot.Fields["NativeReadError"] = new(ex.Message, "FFXIVClientStructs native read");
            this.log.Warning(ex, "[NativeGameObjectDumpService] Failed to capture native GameObject");
        }

        return snapshot;
    }

    public void RecordManualTargetTest(RuntimeActorInstance? actor, string manualResult)
    {
        var actorSummary = actor == null ? "未选中 Actor" : this.BuildActorConditionSummary(actor);
        var currentTarget = this.FormatTargetObject(this.targetManager.Target);
        this.LastManualTestResult = $"人工结果={manualResult}; {actorSummary}; 当前Target={currentTarget}";
        this.log.Information("[NativeGameObjectDumpService] Manual target test: {Result}", this.LastManualTestResult);
    }

    private string BuildActorConditionSummary(RuntimeActorInstance actor)
    {
        var snapshot = this.ActorSnapshot;
        var getIsTargetable = snapshot?.Fields.GetValueOrDefault("GetIsTargetable()")?.Value ?? "未dump";
        var objectKind = snapshot?.Fields.GetValueOrDefault("ObjectKind")?.Value ?? "未dump";
        var subKind = snapshot?.Fields.GetValueOrDefault("SubKind")?.Value ?? "未dump";
        var targetableStatus = snapshot?.Fields.GetValueOrDefault("TargetableStatus")?.Value ?? "未dump";
        var gameObjectId = snapshot?.Fields.GetValueOrDefault("GameObjectId")?.Value ?? "未dump";
        var entityId = snapshot?.Fields.GetValueOrDefault("EntityId")?.Value ?? "未dump";
        var baseId = snapshot?.Fields.GetValueOrDefault("BaseId/DataId")?.Value ?? "未dump";
        return $"runtimeId={actor.RuntimeId}; objectIndex={actor.ObjectIndex}; ObjectKind={objectKind}; SubKind={subKind}; TargetableStatus={targetableStatus}; GetIsTargetable={getIsTargetable}; GameObjectId={gameObjectId}; EntityId={entityId}; BaseId={baseId}";
    }

    private void AddObjectTableFields(NativeNpcProbeSnapshot snapshot, NativeGameObject* native, nint address)
    {
        var index = native->ObjectIndex;
        var foundByIndex = false;
        var addressMatches = false;
        string tableAddress = "未找到";
        try
        {
            foreach (var obj in this.objectTable)
            {
                if (obj == null)
                    continue;

                var objectIndexText = ReadManagedMember(obj, "ObjectIndex", "ObjectTableIndex", "Index");
                if (!ushort.TryParse(objectIndexText, out var objectIndex) || objectIndex != index)
                    continue;

                foundByIndex = true;
                if (TryGetAddress(obj, out var objAddress))
                {
                    tableAddress = $"0x{objAddress:X}";
                    addressMatches = objAddress == address;
                }

                break;
            }
        }
        catch (Exception ex)
        {
            snapshot.Fields["ObjectTableCheckError"] = new(ex.Message, "Dalamud IObjectTable 枚举");
        }

        snapshot.Fields["ObjectTable按Index找到"] = new(foundByIndex ? "true" : "false", "Dalamud IObjectTable 枚举");
        snapshot.Fields["ObjectTable地址"] = new(tableAddress, "Dalamud IObjectTable 枚举");
        snapshot.Fields["ObjectTable地址匹配"] = new(addressMatches ? "true" : "false", "IObjectTable Address == native Address");
    }

    private void AddTargetManagerFields(NativeNpcProbeSnapshot snapshot)
    {
        snapshot.Fields["TargetSystem.Target"] = new(this.FormatTargetObject(ReadRawMember(this.targetManager, "Target")), "Dalamud ITargetManager.Target");
        snapshot.Fields["TargetSystem.MouseOverTarget"] = new(this.FormatTargetObject(ReadRawMember(this.targetManager, "MouseOverTarget")), "Dalamud ITargetManager reflected property");
        snapshot.Fields["TargetSystem.MouseOverNameplateTarget"] = new(this.FormatTargetObject(ReadRawMember(this.targetManager, "MouseOverNameplateTarget")), "Dalamud ITargetManager reflected property");
        snapshot.Fields["TargetSystem.SoftTarget"] = new(this.FormatTargetObject(ReadRawMember(this.targetManager, "SoftTarget")), "Dalamud ITargetManager reflected property");
        snapshot.Fields["TargetSystem.FocusTarget"] = new(this.FormatTargetObject(ReadRawMember(this.targetManager, "FocusTarget")), "Dalamud ITargetManager reflected property");
    }

    private string FormatTargetObject(object? target)
    {
        if (target == null)
            return "null";

        var name = ReadManagedMember(target, "Name");
        var index = ReadManagedMember(target, "ObjectIndex", "ObjectTableIndex", "Index");
        var kind = ReadManagedMember(target, "ObjectKind");
        var dataId = ReadManagedMember(target, "DataId");
        var address = TryGetAddress(target, out var targetAddress) ? $"0x{targetAddress:X}" : "无Address";
        return $"Name={name}, Index={index}, Kind={kind}, DataId={dataId}, Address={address}";
    }

    private static void Add<T>(NativeNpcProbeSnapshot snapshot, string name, T value, string fieldOrMethodName)
    {
        var source = fieldOrMethodName.StartsWith("method:", StringComparison.Ordinal)
            ? $"FFXIVClientStructs {fieldOrMethodName}"
            : $"FFXIVClientStructs GameObject.{fieldOrMethodName} {TryGetOffset(fieldOrMethodName)}";
        snapshot.Fields[name] = new(FormatValue(value), source);
    }

    private static string TryGetOffset(string fieldName)
    {
        try
        {
            var offset = Marshal.OffsetOf<NativeGameObject>(fieldName).ToInt64();
            return $"offset 0x{offset:X}";
        }
        catch
        {
            return "offset 未确认";
        }
    }

    private static string TryReadNativeName(object source)
    {
        var reflected = ReadManagedMember(source, "Name");
        return string.IsNullOrWhiteSpace(reflected) ? "未读取" : reflected;
    }

    private string TryReadTargetObjectId(nint address)
    {
        try
        {
            var character = (NativeCharacter*)address;
            return character->GetTargetId().ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    private static bool TryGetAddress(object source, out nint address)
    {
        address = 0;
        var raw = ReadRawMember(source, "Address");
        if (raw == null)
            return false;

        switch (raw)
        {
            case nint native:
                address = native;
                return address != 0;
            case ulong ulongValue:
                address = unchecked((nint)ulongValue);
                return address != 0;
            case long longValue:
                address = (nint)longValue;
                return address != 0;
            case string text:
                return TryParseAddress(text, out address);
            default:
                return TryParseAddress(raw.ToString(), out address);
        }
    }

    public static bool TryParseAddress(string? raw, out nint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                   && (address = unchecked((nint)hex)) != 0;

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
               && (address = unchecked((nint)number)) != 0;
    }

    private static object? ReadRawMember(object source, params string[] names)
    {
        var type = source.GetType();
        foreach (var name in names)
        {
            try
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                    return property.GetValue(source);

                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field.GetValue(source);
            }
            catch
            {
            }
        }

        return null;
    }

    private static string ReadManagedMember(object source, params string[] names)
        => FormatValue(ReadRawMember(source, names));

    private static string FormatValue(object? value)
    {
        if (value == null)
            return "null";

        return value switch
        {
            Vector3 vector => $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}",
            nint pointer => $"0x{pointer:X}",
            _ => value.ToString() ?? "null",
        };
    }

    public bool TryWriteObjectKind(RuntimeActorInstance actor, out string result)
        => this.TryWrite(actor, "ObjectKind", native =>
        {
            var oldValue = native->ObjectKind;
            native->ObjectKind = NativeObjectKind.EventNpc;
            return $"ObjectKind: {oldValue} -> {native->ObjectKind}";
        }, out result);

    public bool TryWriteSubKind(RuntimeActorInstance actor, out string result)
        => this.TryWrite(actor, "SubKind", native =>
        {
            var oldValue = native->SubKind;
            native->SubKind = 0;
            return $"SubKind: {oldValue} -> {native->SubKind}";
        }, out result);

    public bool TryWriteSubKind(RuntimeActorInstance actor, byte subKind, out string result)
        => this.TryWrite(actor, "SubKind", native =>
        {
            var oldValue = native->SubKind;
            native->SubKind = subKind;
            return $"SubKind: {oldValue} -> {native->SubKind}";
        }, out result);

    public bool TryWriteTargetableStatus(RuntimeActorInstance actor, out string result)
        => this.TryWrite(actor, "TargetableStatus", native =>
        {
            var oldValue = native->TargetableStatus;
            native->TargetableStatus = ObjectTargetableFlags.IsTargetable | ObjectTargetableFlags.ReadyToDraw;
            return $"TargetableStatus: {oldValue} -> {native->TargetableStatus}; GetIsTargetable={native->GetIsTargetable()}";
        }, out result);

    public bool TryWriteTargetableStatus(RuntimeActorInstance actor, byte targetableStatus, out string result)
        => this.TryWrite(actor, "TargetableStatus", native =>
        {
            var oldValue = native->TargetableStatus;
            native->TargetableStatus = (ObjectTargetableFlags)targetableStatus;
            return $"TargetableStatus: {oldValue} -> {native->TargetableStatus}; GetIsTargetable={native->GetIsTargetable()}";
        }, out result);

    public bool TryWriteBaseId(RuntimeActorInstance actor, uint dataId, out string result)
        => this.TryWrite(actor, "BaseId/DataId", native =>
        {
            var oldValue = native->BaseId;
            native->BaseId = dataId;
            return $"BaseId/DataId: {oldValue} -> {native->BaseId}";
        }, out result);

    public bool TryWriteEntityId(RuntimeActorInstance actor, uint entityId, out string result)
        => this.TryWrite(actor, "EntityId", native =>
        {
            var oldValue = native->EntityId;
            native->EntityId = entityId;
            return $"EntityId: {oldValue} -> {native->EntityId}; GameObjectId={native->GetGameObjectId()}";
        }, out result);

    public bool TryWriteGameObjectIdViaBaseId(RuntimeActorInstance actor, uint objectId, out string result)
        => this.TryWrite(actor, "GameObjectId(BaseId route)", native =>
        {
            var oldValue = native->BaseId;
            native->BaseId = objectId;
            return $"GameObjectId 没有独立字段；按源码规则写 BaseId: {oldValue} -> {native->BaseId}; GameObjectId={native->GetGameObjectId()}";
        }, out result);

    public bool TryWriteHitboxRadius(RuntimeActorInstance actor, float radius, out string result)
        => this.TryWrite(actor, "HitboxRadius", native =>
        {
            var oldValue = native->HitboxRadius;
            native->HitboxRadius = radius;
            return $"HitboxRadius: {oldValue:F3} -> {native->HitboxRadius:F3}";
        }, out result);

    public bool TryWriteNamePlateIconId(RuntimeActorInstance actor, uint iconId, out string result)
        => this.TryWrite(actor, "NamePlateIconId", native =>
        {
            var oldValue = native->NamePlateIconId;
            native->NamePlateIconId = (ushort)Math.Clamp(iconId, 0, ushort.MaxValue);
            return $"NamePlateIconId: {oldValue} -> {native->NamePlateIconId}";
        }, out result);

    public bool TryWriteNamePlateColorType(RuntimeActorInstance actor, uint colorType, out string result)
    {
        result = $"NamePlateColorType 是 GetNamePlateColorType() 的 native 方法结果，当前未找到独立可写字段；请求值={colorType}，未写入。";
        actor.HoverOrTargetDebugInfo = result;
        this.log.Information("[NativeGameObjectDumpService] NamePlateColorType write skipped. RuntimeId={RuntimeId}, Requested={Requested}", actor.RuntimeId, colorType);
        return false;
    }

    public bool TryWriteRenderFlags(RuntimeActorInstance actor, ulong flags, out string result)
        => this.TryWrite(actor, "RenderFlags", native =>
        {
            var oldValue = native->RenderFlags;
            native->RenderFlags = (VisibilityFlags)flags;
            return $"RenderFlags: {oldValue} -> {native->RenderFlags}";
        }, out result);

    public bool TryCopyEventHandler(RuntimeActorInstance actor, nint eventHandler, out string result)
        => this.TryWrite(actor, "EventHandler", native =>
        {
            var oldValue = (nint)native->EventHandler;
            native->EventHandler = (FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler*)eventHandler;
            return $"EventHandler: 0x{oldValue:X} -> 0x{(nint)native->EventHandler:X}";
        }, out result);

    private bool TryWrite(RuntimeActorInstance actor, string fieldName, NativeWriteAction write, out string result)
    {
        if (!TryParseAddress(actor.Address, out var address) && actor.CharacterObject != null)
            TryGetAddress(actor.CharacterObject, out address);

        if (address == 0)
        {
            result = $"无法写入 {fieldName}：actor address 不可用。";
            return false;
        }

        try
        {
            var native = (NativeGameObject*)address;
            result = write(native);
            _ = this.DumpActor(actor);
            result = $"{result}; readback GetIsTargetable={this.ActorSnapshot?.Fields.GetValueOrDefault("GetIsTargetable()")?.Value ?? "未读取"}; 当前Target={this.FormatTargetObject(this.targetManager.Target)}";
            actor.HoverOrTargetDebugInfo = result;
            return true;
        }
        catch (Exception ex)
        {
            result = $"写入 {fieldName} 失败：{ex.Message}";
            actor.HoverOrTargetDebugInfo = result;
            this.log.Warning(ex, "[NativeGameObjectDumpService] Failed native single-field write. RuntimeId={RuntimeId}, Field={Field}", actor.RuntimeId, fieldName);
            return false;
        }
    }

    private static bool IsPositionReasonable(FFXIVClientStructs.FFXIV.Common.Math.Vector3 position)
        => float.IsFinite(position.X)
           && float.IsFinite(position.Y)
           && float.IsFinite(position.Z)
           && Math.Abs(position.X) < 1_000_000
           && Math.Abs(position.Y) < 1_000_000
           && Math.Abs(position.Z) < 1_000_000;
}
