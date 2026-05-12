using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Terrain;
using LocalQuestReborn.Models;
using System.Numerics;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace LocalQuestReborn.Services;

public sealed unsafe class LocalLayoutObjectService
{
    private const int VisualMatrixOffset = 0x20;

    private readonly LayoutObjectTransformService transformService = new();
    private readonly List<LocalLayoutObjectInstance> instances = [];
    private readonly Dictionary<string, LocalLayoutObjectInstance> occupiedSlots = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LocalLayoutObjectInstance> Instances => this.instances;

    public int ActiveOccupiedSlotCount => this.occupiedSlots.Count;

    public int DuplicateSlotCount => this.instances
        .Where(item => item.IsOccupied && !item.IsRestored)
        .GroupBy(item => item.OccupiedSlotAddress, StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Count() > 1);

    public string LastStatus { get; private set; } = "灏氭湭鍒涘缓鏈湴鍦烘櫙鐗╀綋銆?";

    public bool IsSlotOccupied(string slotAddress)
    {
        this.RebuildOccupiedSlotRegistry();
        return this.occupiedSlots.ContainsKey(slotAddress);
    }

    public LocalLayoutObjectInstance? CreateFromCandidate(LayoutProbeInstance? candidate, Vector3 playerPosition, LocalLayoutTransformMode mode)
    {
        this.RebuildOccupiedSlotRegistry();
        if (candidate == null)
        {
            this.LastStatus = "璇峰厛鏌ユ壘骞堕€夋嫨涓€涓€欓€?BgPart銆?";
            return null;
        }

        if (!string.Equals(candidate.Type, "BgPart", StringComparison.Ordinal))
        {
            this.LastStatus = $"褰撳墠鍊欓€変笉鏄?BgPart锛{candidate.Type}";
            return null;
        }

        if (this.occupiedSlots.TryGetValue(candidate.Address, out var owner))
        {
            this.LastStatus = $"璇?BgPart slot 宸茶瀹炰緥 {owner.Id} 鍗犵敤銆?";
            return owner;
        }

        if (!TryGetPointer(candidate.Address, out var pointer))
        {
            this.LastStatus = $"鍊欓€夊湴鍧€瑙ｆ瀽澶辫触锛{candidate.Address}";
            return null;
        }

        var originalLayout = ReadLayoutTransform(pointer);
        if (originalLayout == null)
        {
            this.LastStatus = "璇诲彇鍊欓€夊師濮?layout transform 澶辫触锛屾湭鍒涘缓銆?";
            return null;
        }

        var graphicsObjectAddress = string.Empty;
        var originalVisualMatrix = Matrix4x4.Identity;
        var originalVisualTranslation = originalLayout.Value.Position;
        var originalVisual = new SceneTransformSnapshot(originalLayout.Value.Position, originalLayout.Value.Rotation, originalLayout.Value.Scale, false);
        if (mode == LocalLayoutTransformMode.VisualOnly)
        {
            if (!TryGetGraphicsObjectAddress(pointer, out var graphicsAddress))
            {
                this.LastStatus = "璇诲彇 BgPart GraphicsObject 澶辫触锛屾棤娉曞垱寤?VisualOnly 鏈湴鐗╀欢銆?";
                return null;
            }

            graphicsObjectAddress = $"0x{graphicsAddress:X}";
            originalVisualMatrix = ReadVisualMatrix(graphicsAddress, VisualMatrixOffset);
            originalVisualTranslation = GetMatrixTranslation(originalVisualMatrix);
            originalVisual = ReadSceneObjectTransform(graphicsAddress) ?? originalVisual;
        }

        var instance = new LocalLayoutObjectInstance
        {
            Id = $"layout-object-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}",
            SourceResourcePath = candidate.ResourcePath,
            OccupiedSlotAddress = candidate.Address,
            TransformMode = mode,
            GraphicsObjectAddress = graphicsObjectAddress,
            VisualTransformOffset = VisualMatrixOffset,
            OccupiedSlotOriginalPosition = originalLayout.Value.Position,
            OccupiedSlotOriginalRotation = originalLayout.Value.Rotation,
            OccupiedSlotOriginalScale = originalLayout.Value.Scale,
            OriginalLayoutPosition = originalLayout.Value.Position,
            OriginalLayoutRotation = originalLayout.Value.Rotation,
            OriginalLayoutScale = originalLayout.Value.Scale,
            OriginalLayoutTransform = FormatSnapshot(originalLayout.Value),
            OriginalVisualTransform = mode == LocalLayoutTransformMode.VisualOnly ? FormatSceneSnapshot(originalVisual) : "FullLayout 妯″紡涓嶄娇鐢ㄨ瑙夊瓧娈?",
            OriginalVisualTranslation = originalVisualTranslation,
            OriginalVisualPosition = originalVisual.Position,
            OriginalVisualRotation = originalVisual.Rotation,
            OriginalVisualScale = originalVisual.Scale,
            OriginalVisualMatrix = originalVisualMatrix,
            CurrentVisualTranslation = mode == LocalLayoutTransformMode.VisualOnly ? playerPosition : Vector3.Zero,
            CurrentVisualMatrix = mode == LocalLayoutTransformMode.VisualOnly ? originalVisualMatrix : Matrix4x4.Identity,
            CurrentPosition = playerPosition,
            CurrentRotation = mode == LocalLayoutTransformMode.VisualOnly ? originalVisual.Rotation : originalLayout.Value.Rotation,
            CurrentRotationEuler = Vector3.Zero,
            CurrentScale = mode == LocalLayoutTransformMode.VisualOnly ? originalVisual.Scale : originalLayout.Value.Scale,
            Visible = candidate.Visible,
            IsOccupied = true,
            CanRestore = true,
            VisualOnlyVerified = mode == LocalLayoutTransformMode.VisualOnly,
            HasCollisionMoved = mode == LocalLayoutTransformMode.FullLayoutWithCollision,
            Notes = mode == LocalLayoutTransformMode.VisualOnly
                ? "VisualOnly锛氬彧鍐?GraphicsObject + 0x20 visual matrix锛屼笉绉诲姩 layout/collision銆?"
                : "鍗遍櫓锛欶ullLayoutWithCollision 浼氬啓 layout transform 骞剁Щ鍔ㄧ鎾炰綋銆?",
        };

        this.instances.Add(instance);
        this.occupiedSlots[instance.OccupiedSlotAddress] = instance;
        this.WriteInstanceTransform(instance, playerPosition, instance.CurrentRotationEuler, instance.CurrentScale, "浠庡€欓€?BgPart 鍒涘缓鏈湴鐗╀欢瀹炰緥");
        return instance;
    }

    public void UpdateExistingSlotToPlayer(LayoutProbeInstance? candidate, Vector3 playerPosition)
    {
        if (candidate == null)
        {
            this.LastStatus = "璇峰厛閫夋嫨鍊欓€?BgPart銆?";
            return;
        }

        this.RebuildOccupiedSlotRegistry();
        if (!this.occupiedSlots.TryGetValue(candidate.Address, out var owner))
        {
            this.LastStatus = "璇ュ€欓€?slot 褰撳墠鏈鏈湴瀹炰緥鍗犵敤銆?";
            return;
        }

        this.MoveToPlayer(owner.Id, playerPosition);
    }

    public LocalLayoutObjectInstance? RestoreAndReoccupy(LayoutProbeInstance? candidate, Vector3 playerPosition, LocalLayoutTransformMode mode)
    {
        if (candidate == null)
        {
            this.LastStatus = "璇峰厛閫夋嫨鍊欓€?BgPart銆?";
            return null;
        }

        this.RebuildOccupiedSlotRegistry();
        if (this.occupiedSlots.TryGetValue(candidate.Address, out var owner))
            this.Delete(owner.Id);

        return this.CreateFromCandidate(candidate, playerPosition, mode);
    }

    public void MoveToPlayer(string id, Vector3 playerPosition)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, playerPosition, instance.CurrentRotationEuler, instance.CurrentScale, "绉诲姩瀹炰緥鍒扮帺瀹跺綋鍓嶄綅缃?");
    }

    public void MoveX(string id, float delta) => this.MoveBy(id, new Vector3(delta, 0f, 0f), $"X {(delta >= 0 ? "+" : string.Empty)}{delta:F1}");

    public void MoveY(string id, float delta) => this.MoveBy(id, new Vector3(0f, delta, 0f), $"Y {(delta >= 0 ? "+" : string.Empty)}{delta:F1}");

    public void MoveZ(string id, float delta) => this.MoveBy(id, new Vector3(0f, 0f, delta), $"Z {(delta >= 0 ? "+" : string.Empty)}{delta:F1}");

    public void ApplyVisualTransform(string id, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, position, rotationEuler, scale, instance.TransformMode == LocalLayoutTransformMode.VisualOnly ? "搴旂敤 VisualOnly transform" : "搴旂敤 FullLayout transform");
    }

    public void ResetPosition(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        var targetPosition = instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? instance.OriginalVisualPosition
            : instance.OccupiedSlotOriginalPosition;
        this.WriteInstanceTransform(instance, targetPosition, instance.CurrentRotationEuler, instance.CurrentScale, "閲嶇疆浣嶇疆");
    }

    public void ResetRotation(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, instance.CurrentPosition, Vector3.Zero, instance.CurrentScale, "閲嶇疆鏃嬭浆");
    }

    public void ResetScale(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        var targetScale = instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? instance.OriginalVisualScale
            : instance.OccupiedSlotOriginalScale;
        this.WriteInstanceTransform(instance, instance.CurrentPosition, instance.CurrentRotationEuler, targetScale, "閲嶇疆缂╂斁");
    }

    public void AdjustScale(string id, float multiplier)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        var scale = Vector3.Max(instance.CurrentScale * multiplier, new Vector3(0.01f));
        this.WriteInstanceTransform(instance, instance.CurrentPosition, instance.CurrentRotationEuler, scale, $"缂╂斁 x{multiplier:F2}");
    }

    public void SaveCurrentTransform(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
        {
            if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            {
                instance.LastError = "GraphicsObject 鍦板潃瑙ｆ瀽澶辫触銆?";
                this.LastStatus = instance.LastError;
                return;
            }

            var current = ReadSceneObjectTransform(graphicsAddress);
            if (current == null)
            {
                instance.LastError = "璇诲彇褰撳墠 Scene.Object transform 澶辫触銆?";
                this.LastStatus = instance.LastError;
                return;
            }

            instance.CurrentPosition = current.Value.Position;
            instance.CurrentRotation = current.Value.Rotation;
            instance.CurrentScale = current.Value.Scale;
            instance.CurrentVisualTranslation = current.Value.Position;
            instance.CurrentVisualMatrix = ReadVisualMatrix((nint)graphicsAddress, instance.VisualTransformOffset);
            instance.LastReadback = FormatSceneSnapshot(current.Value);
            instance.LastError = string.Empty;
            this.LastStatus = $"宸蹭繚瀛樺綋鍓?VisualOnly transform锛{instance.Id}銆?";
            return;
        }

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
        {
            instance.LastError = "slot 鍦板潃瑙ｆ瀽澶辫触銆?";
            this.LastStatus = instance.LastError;
            return;
        }

        var layout = ReadLayoutTransform(pointer);
        if (layout == null)
        {
            instance.LastError = "璇诲彇褰撳墠 layout transform 澶辫触銆?";
            this.LastStatus = instance.LastError;
            return;
        }

        instance.CurrentPosition = layout.Value.Position;
        instance.CurrentRotation = layout.Value.Rotation;
        instance.CurrentScale = layout.Value.Scale;
        instance.LastReadback = FormatSnapshot(layout.Value);
        instance.LastError = string.Empty;
        this.LastStatus = $"宸蹭繚瀛樺綋鍓?transform锛{instance.Id}";
    }

    public void RestoreOriginal(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.RestoreOriginal(instance, removeAfterRestore: false);
    }

    public void Delete(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.RestoreOriginal(instance, removeAfterRestore: true);
    }

    public void RestoreAll(bool removeAfterRestore = false)
    {
        var duplicateCleanupCount = this.CleanupDuplicateInstances(auto: true);
        this.RebuildOccupiedSlotRegistry();
        var restoreCount = this.occupiedSlots.Count;
        foreach (var instance in this.occupiedSlots.Values.ToList())
            this.RestoreOriginal(instance, removeAfterRestore);

        if (removeAfterRestore)
            this.instances.RemoveAll(item => item.IsRestored || item.IsDuplicate);

        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = removeAfterRestore
            ? "宸叉寜 slot 鍘婚噸鎭㈠骞剁Щ闄ゅ叏閮ㄦ湰鍦板満鏅墿浣撳疄渚嬨€?"
            : "宸叉寜 slot 鍘婚噸鎭㈠鍏ㄩ儴鏈湴鍦烘櫙鐗╀綋瀹炰緥銆?";
        this.LastStatus = removeAfterRestore
            ? $"已自动清理重复实例 {duplicateCleanupCount} 个，并恢复/移除 {restoreCount} 个 occupied slot。"
            : $"已自动清理重复实例 {duplicateCleanupCount} 个，并恢复 {restoreCount} 个 occupied slot。";
    }

    public void RestoreAllAndClear() => this.RestoreAll(removeAfterRestore: true);

    public void MoveAllActiveVisualOnlyToPlayer(Vector3 playerPosition)
    {
        this.RebuildOccupiedSlotRegistry();
        var moved = 0;
        foreach (var instance in this.occupiedSlots.Values.ToList())
        {
            if (instance.TransformMode != LocalLayoutTransformMode.VisualOnly)
                continue;

            this.WriteInstanceTransform(instance, playerPosition, instance.CurrentRotationEuler, instance.CurrentScale, "鍏ㄩ儴绉诲洖鐜╁鑴氫笅");
            moved++;
        }

        this.LastStatus = $"宸插皢 {moved} 涓?active VisualOnly slot 鐨勮瑙夋ā鍨嬬Щ鍥炵帺瀹惰剼涓嬨€?";
    }

    public void RestoreAllActiveVisualOnlyTranslations()
    {
        this.RebuildOccupiedSlotRegistry();
        var restored = 0;
        foreach (var instance in this.occupiedSlots.Values.ToList())
        {
            if (instance.TransformMode != LocalLayoutTransformMode.VisualOnly)
                continue;

            this.transformService.RestoreTransform(instance);
            instance.IsOccupied = false;
            instance.IsRestored = true;
            instance.HasCollisionMoved = false;
            this.occupiedSlots.Remove(instance.OccupiedSlotAddress);
            restored++;
        }

        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = $"宸叉仮澶?{restored} 涓?active VisualOnly slot 鐨勫師 visual transform銆?";
    }

    public int CleanupDuplicateInstances(bool auto = false)
    {
        var duplicateIds = this.instances
            .Select((item, index) => new { Item = item, Index = index })
            .Where(entry => entry.Item.IsOccupied && !entry.Item.IsRestored && !string.IsNullOrWhiteSpace(entry.Item.OccupiedSlotAddress))
            .GroupBy(entry => entry.Item.OccupiedSlotAddress, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderBy(entry => entry.Index).Skip(1))
            .Select(entry => entry.Item.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var instance in this.instances.Where(item => duplicateIds.Contains(item.Id)))
        {
            instance.IsDuplicate = true;
            instance.IsOccupied = false;
            instance.IsRestored = true;
            instance.LastError = "重复 slot 实例已清理，未执行 restore。";
            instance.Notes = auto
                ? "自动恢复全部前清理的重复 slot 实例，未执行 restore。"
                : "手动清理的重复 slot 实例，未执行 restore。";
        }

        var removed = this.instances.RemoveAll(item => duplicateIds.Contains(item.Id));
        this.RebuildOccupiedSlotRegistry();
        if (!auto)
            this.LastStatus = $"已清理重复实例 {removed} 个。";

        return removed;
    }

    public LocalLayoutObjectInstance? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var instance = this.instances.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (instance == null)
            this.LastStatus = $"鎵句笉鍒版湰鍦板満鏅墿浣撳疄渚嬶細{id}";

        return instance;
    }

    private void MoveBy(string id, Vector3 delta, string action)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, instance.CurrentPosition + delta, instance.CurrentRotationEuler, instance.CurrentScale, action);
    }

    private void RebuildOccupiedSlotRegistry()
    {
        this.occupiedSlots.Clear();
        foreach (var instance in this.instances)
        {
            if (!instance.IsOccupied || instance.IsRestored)
                continue;

            if (this.occupiedSlots.ContainsKey(instance.OccupiedSlotAddress))
            {
                instance.IsDuplicate = true;
                instance.Notes = "閲嶅 slot 瀹炰緥锛屽凡鏍囪 invalid锛屼笉鍙備笌 RestoreAll銆?";
                continue;
            }

            instance.IsDuplicate = false;
            this.occupiedSlots[instance.OccupiedSlotAddress] = instance;
        }
    }

    private void RestoreOriginal(LocalLayoutObjectInstance instance, bool removeAfterRestore)
    {
        if (instance.IsDuplicate)
        {
            instance.LastError = "閲嶅 slot 瀹炰緥涓嶅弬涓庢仮澶嶏紝閬垮厤瑕嗙洊鍘熷 transform銆?";
            this.LastStatus = instance.LastError;
            if (removeAfterRestore)
                this.instances.Remove(instance);
            return;
        }

        if (!instance.CanRestore)
        {
            instance.LastError = "娌℃湁鍙仮澶嶇殑鍘熷 transform銆?";
            this.LastStatus = instance.LastError;
            return;
        }

        if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
            this.transformService.RestoreTransform(instance);
        else
            this.transformService.RestoreTransform(instance);

        instance.IsOccupied = false;
        instance.IsRestored = true;
        instance.HasCollisionMoved = false;
        this.occupiedSlots.Remove(instance.OccupiedSlotAddress);
        if (removeAfterRestore)
            this.instances.Remove(instance);

        this.LastStatus = this.transformService.LastResult;
    }

    private void WriteInstanceTransform(LocalLayoutObjectInstance instance, Vector3 position, Vector3 rotationEuler, Vector3 scale, string action)
    {
        instance.CurrentPosition = position;
        instance.CurrentRotationEuler = rotationEuler;
        instance.CurrentScale = scale;
        this.transformService.ApplyTransform(instance);
        this.LastStatus = $"{action}锛{this.transformService.LastResult}";
    }

    private void WriteVisualTransform(LocalLayoutObjectInstance instance, Vector3 position, Quaternion rotation, Vector3 scale, string action)
    {
        if (instance.IsDuplicate)
        {
            instance.LastError = "閲嶅 slot 瀹炰緥绂佹鍐欏叆銆?";
            this.LastStatus = instance.LastError;
            return;
        }

        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
        {
            instance.LastError = $"GraphicsObject 鍦板潃瑙ｆ瀽澶辫触锛{instance.GraphicsObjectAddress}";
            this.LastStatus = instance.LastError;
            return;
        }

        try
        {
            var target = new SceneTransformSnapshot(position, rotation, scale, false);
            WriteSceneObjectTransform((nint)graphicsAddress, target);
            var readback = ReadVisualMatrix((nint)graphicsAddress, instance.VisualTransformOffset);
            var sceneReadback = ReadSceneObjectTransform((nint)graphicsAddress) ?? target;
            instance.CurrentVisualMatrix = readback;
            instance.CurrentVisualTranslation = sceneReadback.Position;
            instance.CurrentPosition = sceneReadback.Position;
            instance.CurrentRotation = sceneReadback.Rotation;
            instance.CurrentScale = sceneReadback.Scale;
            instance.LastReadback = FormatSceneSnapshot(sceneReadback);
            instance.LastError = string.Empty;
            instance.IsOccupied = true;
            instance.IsRestored = false;
            instance.HasCollisionMoved = false;
            instance.VisualOnlyVerified = true;

            this.LastStatus = $"{action} 瀹屾垚锛歏isualOnly 鍐?Graphics.Scene.Object Position/Rotation/Scale锛屼笉鍐?layout/collision銆?";
        }
        catch (Exception ex)
        {
            instance.LastError = $"{action} 澶辫触锛{ex.Message}";
            this.LastStatus = instance.LastError;
        }
    }

    private void RestoreOriginalVisualTransform(LocalLayoutObjectInstance instance, string action)
    {
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
        {
            instance.LastError = $"GraphicsObject 鍦板潃瑙ｆ瀽澶辫触锛{instance.GraphicsObjectAddress}";
            this.LastStatus = instance.LastError;
            return;
        }

        try
        {
            var original = new SceneTransformSnapshot(instance.OriginalVisualPosition, instance.OriginalVisualRotation, instance.OriginalVisualScale, false);
            WriteSceneObjectTransform((nint)graphicsAddress, original);
            var readback = ReadVisualMatrix((nint)graphicsAddress, instance.VisualTransformOffset);
            var sceneReadback = ReadSceneObjectTransform((nint)graphicsAddress) ?? original;
            instance.CurrentVisualMatrix = readback;
            instance.CurrentVisualTranslation = sceneReadback.Position;
            instance.CurrentPosition = sceneReadback.Position;
            instance.CurrentRotation = sceneReadback.Rotation;
            instance.CurrentRotationEuler = Vector3.Zero;
            instance.CurrentScale = sceneReadback.Scale;
            instance.LastReadback = FormatSceneSnapshot(sceneReadback);
            instance.LastError = string.Empty;
            instance.HasCollisionMoved = false;
            this.LastStatus = $"{action} 瀹屾垚锛氬凡鎭㈠ Scene.Object Position/Rotation/Scale銆?";
        }
        catch (Exception ex)
        {
            instance.LastError = $"{action} 澶辫触锛{ex.Message}";
            this.LastStatus = instance.LastError;
        }
    }

    private void WriteLayoutTransform(LocalLayoutObjectInstance instance, Vector3 position, Quaternion rotation, Vector3 scale, string action)
    {
        if (instance.IsDuplicate)
        {
            instance.LastError = "閲嶅 slot 瀹炰緥绂佹鍐欏叆銆?";
            this.LastStatus = instance.LastError;
            return;
        }

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
        {
            instance.LastError = $"slot 鍦板潃瑙ｆ瀽澶辫触锛{instance.OccupiedSlotAddress}";
            this.LastStatus = instance.LastError;
            return;
        }

        var target = new LayoutTransformSnapshot(position, rotation, scale);
        if (!WriteLayoutTransform(pointer, target))
        {
            instance.LastError = $"{action} 澶辫触锛歋etTransform 鏈垚鍔熴€?";
            this.LastStatus = instance.LastError;
            return;
        }

        var after = ReadLayoutTransform(pointer);
        if (after == null)
        {
            instance.LastError = $"{action} 鍚?readback 澶辫触銆?";
            this.LastStatus = instance.LastError;
            return;
        }

        instance.CurrentPosition = after.Value.Position;
        instance.CurrentRotation = after.Value.Rotation;
        instance.CurrentScale = after.Value.Scale;
        instance.LastReadback = FormatSnapshot(after.Value);
        instance.LastError = string.Empty;
        instance.IsOccupied = true;
        instance.IsRestored = false;
        instance.HasCollisionMoved = true;
        this.LastStatus = $"{action} 瀹屾垚锛欶ullLayoutWithCollision 浼氱Щ鍔ㄧ鎾炰綋銆俽eadback={FormatVector(after.Value.Position)}";
    }

    private static bool TryGetPointer(string? raw, out ILayoutInstance* pointer)
    {
        pointer = null;
        if (!TryParseAddress(raw, out var address) || address == 0)
            return false;

        pointer = (ILayoutInstance*)address;
        return true;
    }

    private static bool TryGetGraphicsObjectAddress(ILayoutInstance* pointer, out nint graphicsObjectAddress)
    {
        graphicsObjectAddress = 0;
        try
        {
            if (pointer == null || pointer->Id.Type != InstanceType.BgPart)
                return false;

            var bgPart = (BgPartsLayoutInstance*)pointer;
            if (bgPart->GraphicsObject == null)
                return false;

            graphicsObjectAddress = (nint)bgPart->GraphicsObject;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LayoutTransformSnapshot? ReadLayoutTransform(ILayoutInstance* pointer)
    {
        if (pointer == null)
            return null;

        try
        {
            var transform = pointer->GetTransformImpl();
            return transform == null ? null : new LayoutTransformSnapshot(transform->Translation, transform->Rotation, transform->Scale);
        }
        catch
        {
            return null;
        }
    }

    private static bool WriteLayoutTransform(ILayoutInstance* pointer, LayoutTransformSnapshot snapshot)
    {
        if (pointer == null)
            return false;

        try
        {
            var transform = new Transform
            {
                Translation = snapshot.Position,
                Rotation = snapshot.Rotation,
                Scale = snapshot.Scale,
            };
            pointer->SetTransform(&transform);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SceneTransformSnapshot? ReadSceneObjectTransform(nint graphicsObjectAddress)
    {
        if (graphicsObjectAddress == 0)
            return null;

        try
        {
            var obj = (SceneObject*)graphicsObjectAddress;
            var bg = (SceneBgObject*)graphicsObjectAddress;
            return new SceneTransformSnapshot(obj->Position, obj->Rotation, obj->Scale, bg->IsTransformChanged);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSceneObjectTransform(nint graphicsObjectAddress, SceneTransformSnapshot snapshot)
    {
        var obj = (SceneObject*)graphicsObjectAddress;
        var bg = (SceneBgObject*)graphicsObjectAddress;
        obj->Position = snapshot.Position;
        obj->Rotation = snapshot.Rotation;
        obj->Scale = snapshot.Scale;
        bg->IsTransformChanged = true;
        bg->NotifyTransformChanged();
        bg->UpdateTransforms(true);
        bg->UpdateRender();
    }

    private static Matrix4x4 ReadVisualMatrix(nint graphicsObjectAddress, int matrixOffset)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset);

    private static void WriteVisualMatrix(nint graphicsObjectAddress, int matrixOffset, Matrix4x4 matrix)
        => *(Matrix4x4*)((byte*)graphicsObjectAddress + matrixOffset) = matrix;

    private static void WriteVisualTranslation(nint graphicsObjectAddress, int matrixOffset, Vector3 translation)
    {
        var basePtr = (byte*)graphicsObjectAddress + matrixOffset;
        *(float*)(basePtr + 0x30) = translation.X;
        *(float*)(basePtr + 0x34) = translation.Y;
        *(float*)(basePtr + 0x38) = translation.Z;
    }

    private static Vector3 GetMatrixTranslation(Matrix4x4 matrix)
        => new(matrix.M41, matrix.M42, matrix.M43);

    private static bool TryParseAddress(string? raw, out nint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
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

    private static string FormatSnapshot(LayoutTransformSnapshot snapshot)
        => $"position=({FormatVector(snapshot.Position)}), rotation={snapshot.Rotation}, scale=({FormatVector(snapshot.Scale)})";

    private static string FormatMatrix(Matrix4x4 matrix)
        => $"translation=({FormatVector(GetMatrixTranslation(matrix))}); M11={matrix.M11:F3}, M22={matrix.M22:F3}, M33={matrix.M33:F3}, M44={matrix.M44:F3}";

    private static string FormatSceneSnapshot(SceneTransformSnapshot snapshot)
        => $"position=({FormatVector(snapshot.Position)}), rotation={snapshot.Rotation}, scale=({FormatVector(snapshot.Scale)}), IsTransformChanged={snapshot.IsTransformChanged}";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private readonly record struct SceneTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool IsTransformChanged);
}



