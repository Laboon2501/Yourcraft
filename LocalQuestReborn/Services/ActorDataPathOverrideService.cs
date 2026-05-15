using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LocalQuestReborn.Services;

public sealed class ActorDataPathOverrideService
{
    private readonly IPluginLog log;
    private readonly Dictionary<string, DataPathOverrideSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

    public ActorDataPathOverrideService(IPluginLog log)
    {
        this.log = log;
    }

    public bool TryResolveTarget(RuntimeActorInstance actor, ActorAnimationRigPreset preset, out short dataPath, out byte dataHead, out string reason)
    {
        dataPath = 0;
        dataHead = 0;

        if (!TryMapPresetToDataPath(preset, out dataPath))
        {
            reason = $"MappingUnknown: no DataPath mapping for rig preset {preset}.";
            return false;
        }

        if (!TryReadCurrentTribe(actor.CharacterObject, out var currentTribe, out var tribeReason))
        {
            reason = $"MappingUnknown: cannot compute DataHead because current actor Tribe is unavailable. {tribeReason}";
            return false;
        }

        dataHead = ComputeDataHead(dataPath, currentTribe);
        reason = $"targetDataPath={FormatDataPath(dataPath)}; targetDataHead={FormatDataHead(dataHead)}; currentTribe={currentTribe}";
        return true;
    }

    public bool TryReadCurrent(RuntimeActorInstance actor, out ActorDataPathRead read, out string reason)
    {
        read = default;
        if (!TryReadAddress(actor.Address, out var actorPtr) || actorPtr == 0)
        {
            reason = $"actor native address unavailable: {actor.Address}";
            return false;
        }

        try
        {
            unsafe
            {
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actorPtr;
                var drawObjectPtr = character->GameObject.DrawObject == null
                    ? 0
                    : (nint)character->GameObject.DrawObject;
                if (drawObjectPtr == 0)
                {
                    reason = "DrawObject pointer is null.";
                    return false;
                }

                read = ReadDrawObject(drawObjectPtr);
                reason = $"currentDataPath={FormatDataPath(read.DataPath)}; currentDataHead={FormatDataHead(read.DataHead)}; drawObject={FormatPointer(drawObjectPtr)}";
                return true;
            }
        }
        catch (Exception ex)
        {
            reason = $"read failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public bool TryCreateSnapshot(RuntimeActorInstance actor, AnimationDataPathDump dump, out DataPathOverrideSnapshot snapshot, out string reason)
    {
        snapshot = default;
        if (dump.DrawObjectPtr == 0)
        {
            reason = "DrawObject pointer is null.";
            return false;
        }

        if (!dump.CurrentDataPath.HasValue || !dump.CurrentDataHead.HasValue)
        {
            reason = string.IsNullOrWhiteSpace(dump.DataPathReadError)
                ? "Current DataPath/DataHead unavailable."
                : dump.DataPathReadError;
            return false;
        }

        snapshot = new DataPathOverrideSnapshot(
            ActorRuntimeId: actor.RuntimeId,
            ActorAddress: actor.Address,
            SpawnSequence: actor.SpawnSequence,
            DrawObjectPtr: dump.DrawObjectPtr,
            OriginalDataPath: dump.CurrentDataPath.Value,
            OriginalDataHead: dump.CurrentDataHead.Value);
        this.snapshots[actor.RuntimeId] = snapshot;
        reason = $"snapshot originalDataPath={FormatDataPath(snapshot.OriginalDataPath)}; originalDataHead={FormatDataHead(snapshot.OriginalDataHead)}; drawObject={FormatPointer(snapshot.DrawObjectPtr)}";
        return true;
    }

    public bool TryWrite(RuntimeActorInstance actor, short dataPath, byte dataHead, out string reason)
    {
        if (!this.TryReadCurrent(actor, out var read, out reason))
            return false;

        return TryWriteDrawObject(read.DrawObjectPtr, dataPath, dataHead, out reason);
    }

    public bool TryRestore(RuntimeActorInstance actor, out string reason)
    {
        if (!this.snapshots.TryGetValue(actor.RuntimeId, out var snapshot))
        {
            reason = "RestoreSkippedNoSnapshot";
            return false;
        }

        if (!string.Equals(actor.Address, snapshot.ActorAddress, StringComparison.OrdinalIgnoreCase) ||
            actor.SpawnSequence != snapshot.SpawnSequence)
        {
            this.snapshots.Remove(actor.RuntimeId);
            reason = "RestoreSkippedStaleActorGeneration";
            return false;
        }

        if (!this.TryReadCurrent(actor, out var current, out reason))
            return false;

        if (current.DrawObjectPtr != snapshot.DrawObjectPtr)
        {
            this.snapshots.Remove(actor.RuntimeId);
            reason = $"RestoreSkippedStalePointer: snapshotDraw={FormatPointer(snapshot.DrawObjectPtr)}; currentDraw={FormatPointer(current.DrawObjectPtr)}";
            return false;
        }

        if (!TryWriteDrawObject(snapshot.DrawObjectPtr, snapshot.OriginalDataPath, snapshot.OriginalDataHead, out reason))
            return false;

        var restored = ReadDrawObject(snapshot.DrawObjectPtr);
        var ok = restored.DataPath == snapshot.OriginalDataPath && restored.DataHead == snapshot.OriginalDataHead;
        if (ok)
            this.snapshots.Remove(actor.RuntimeId);

        reason = ok
            ? $"Restored original DataPath={FormatDataPath(restored.DataPath)}, DataHead={FormatDataHead(restored.DataHead)}"
            : $"Restore verification failed: afterDataPath={FormatDataPath(restored.DataPath)}, afterDataHead={FormatDataHead(restored.DataHead)}";
        return ok;
    }

    public bool HasSnapshot(string actorRuntimeId) => this.snapshots.ContainsKey(actorRuntimeId);

    private static ActorDataPathRead ReadDrawObject(nint drawObjectPtr)
    {
        var dataPath = Marshal.ReadInt16(drawObjectPtr + 0xAA0);
        var dataHead = Marshal.ReadByte(drawObjectPtr + 0xAA4);
        return new ActorDataPathRead(drawObjectPtr, dataPath, dataHead);
    }

    private static bool TryWriteDrawObject(nint drawObjectPtr, short dataPath, byte dataHead, out string reason)
    {
        if (drawObjectPtr == 0)
        {
            reason = "DrawObject pointer is null.";
            return false;
        }

        try
        {
            Marshal.WriteInt16(drawObjectPtr + 0xAA0, dataPath);
            Marshal.WriteByte(drawObjectPtr + 0xAA4, dataHead);
            var readback = ReadDrawObject(drawObjectPtr);
            var ok = readback.DataPath == dataPath && readback.DataHead == dataHead;
            reason = ok
                ? $"WriteSuccess: DataPath={FormatDataPath(readback.DataPath)}, DataHead={FormatDataHead(readback.DataHead)}"
                : $"WriteReadbackMismatch: readDataPath={FormatDataPath(readback.DataPath)}, readDataHead={FormatDataHead(readback.DataHead)}";
            return ok;
        }
        catch (Exception ex)
        {
            reason = $"write failed at DrawObject={FormatPointer(drawObjectPtr)}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryMapPresetToDataPath(ActorAnimationRigPreset preset, out short dataPath)
    {
        dataPath = preset switch
        {
            ActorAnimationRigPreset.HyurMidlanderMale => 101,
            ActorAnimationRigPreset.HyurMidlanderFemale => 201,
            ActorAnimationRigPreset.HyurHighlanderMale => 301,
            ActorAnimationRigPreset.HyurHighlanderFemale => 401,
            ActorAnimationRigPreset.ElezenMale => 501,
            ActorAnimationRigPreset.ElezenFemale => 601,
            ActorAnimationRigPreset.MiqoteMale => 701,
            ActorAnimationRigPreset.MiqoteFemale => 801,
            ActorAnimationRigPreset.RoegadynMale => 901,
            ActorAnimationRigPreset.RoegadynFemale => 1001,
            ActorAnimationRigPreset.LalafellMale => 1101,
            ActorAnimationRigPreset.LalafellFemale => 1201,
            ActorAnimationRigPreset.AuRaMale => 1301,
            ActorAnimationRigPreset.AuRaFemale => 1401,
            ActorAnimationRigPreset.HrothgarMale => 1501,
            ActorAnimationRigPreset.HrothgarFemale => 1601,
            ActorAnimationRigPreset.VieraMale => 1701,
            ActorAnimationRigPreset.VieraFemale => 1801,
            _ => 0,
        };
        return dataPath != 0;
    }

    private static byte ComputeDataHead(short dataPath, int tribe)
    {
        if (tribe % 2 != 0)
            return dataPath is 301 or 401 ? (byte)0x65 : (byte)0x01;

        if (tribe <= 10)
            return dataPath is 101 or 201 or 104 ? (byte)0x01 : (byte)0x65;

        return dataPath is 301 or 401 ? (byte)0xC9 : (byte)0x65;
    }

    private static bool TryReadCurrentTribe(object? source, out int tribe, out string reason)
    {
        tribe = 0;
        if (source == null)
        {
            reason = "CharacterObject is null.";
            return false;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (TryReadTribeRecursive(source, source.GetType().Name, 0, visited, out tribe, out var path))
        {
            reason = $"read tribe from {path}";
            return true;
        }

        reason = "No readable Tribe member found under CharacterObject.";
        return false;
    }

    private static bool TryReadTribeRecursive(object source, string path, int depth, HashSet<object> visited, out int tribe, out string foundPath)
    {
        tribe = 0;
        foundPath = string.Empty;
        if (depth > 4)
            return false;

        if (!source.GetType().IsValueType && !visited.Add(source))
            return false;

        foreach (var member in source.GetType().GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (member.MemberType is not (MemberTypes.Property or MemberTypes.Field))
                continue;

            object? value;
            Type? valueType;
            try
            {
                switch (member)
                {
                    case PropertyInfo property when property.GetIndexParameters().Length == 0:
                        value = property.GetValue(source);
                        valueType = property.PropertyType;
                        break;
                    case FieldInfo field:
                        value = field.GetValue(source);
                        valueType = field.FieldType;
                        break;
                    default:
                        continue;
                }
            }
            catch
            {
                continue;
            }

            var memberPath = $"{path}.{member.Name}";
            if (string.Equals(member.Name, "Tribe", StringComparison.OrdinalIgnoreCase) &&
                TryConvertToInt(value, out tribe))
            {
                foundPath = memberPath;
                return true;
            }

            if (value == null || value is string || valueType == null || valueType.IsPrimitive || valueType.IsEnum)
                continue;

            if (LooksTraversable(member.Name) &&
                TryReadTribeRecursive(value, memberPath, depth + 1, visited, out tribe, out foundPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksTraversable(string name)
        => name.Contains("Unsafe", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Actor", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Draw", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Customize", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Model", StringComparison.OrdinalIgnoreCase);

    private static bool TryConvertToInt(object? value, out int number)
    {
        number = 0;
        if (value == null)
            return false;

        try
        {
            number = Convert.ToInt32(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadAddress(string? rawAddress, out nint address)
    {
        address = 0;
        var raw = rawAddress?.Trim() ?? string.Empty;
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            address = (nint)hex;
            return true;
        }

        if (ulong.TryParse(raw, out var value))
        {
            address = (nint)value;
            return true;
        }

        return false;
    }

    private static string FormatDataPath(short value) => $"{value} (0x{unchecked((ushort)value):X4})";

    private static string FormatDataHead(byte value) => $"{value} (0x{value:X2})";

    private static string FormatPointer(nint pointer) => pointer == 0 ? "0x0" : $"0x{pointer.ToInt64():X}";
}

public readonly record struct ActorDataPathRead(nint DrawObjectPtr, short DataPath, byte DataHead);

public readonly record struct DataPathOverrideSnapshot(
    string ActorRuntimeId,
    string ActorAddress,
    long SpawnSequence,
    nint DrawObjectPtr,
    short OriginalDataPath,
    byte OriginalDataHead);
