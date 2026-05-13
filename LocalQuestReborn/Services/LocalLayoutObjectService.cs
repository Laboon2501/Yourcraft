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
    private readonly BgObjectModelOverrideService modelOverrideService = new();
    private readonly BgPartRecreateExperimentService recreateExperimentService = new();
    private readonly BgPartCollisionExperimentService collisionExperimentService = new();
    private readonly List<LocalLayoutObjectInstance> instances = [];
    private readonly Dictionary<ulong, LocalLayoutObjectInstance> occupiedSlots = [];

    public IReadOnlyList<LocalLayoutObjectInstance> Instances => this.instances;

    public int ActiveOccupiedSlotCount => this.GetActiveInstances()
        .Select(item => item.SlotAddress)
        .Distinct()
        .Count();

    public int DuplicateSlotCount => this.GetActiveInstances()
        .GroupBy(item => item.SlotAddress)
        .Sum(group => Math.Max(0, group.Count() - 1));

    public string LastStatus { get; private set; } = "尚未创建本地场景物体。";

    public string LastModelOverrideStatus => this.modelOverrideService.LastResult;

    public string LastRecreateExperimentStatus => this.recreateExperimentService.LastResult;

    public string LastCollisionExperimentStatus => this.collisionExperimentService.LastResult;

    public bool IsSlotOccupied(string slotAddress)
    {
        this.RebuildOccupiedSlotRegistry();
        return TryNormalizeSlotAddress(slotAddress, out var normalizedAddress)
            && this.occupiedSlots.ContainsKey(normalizedAddress);
    }

    public LocalLayoutObjectInstance? CreateFromCandidate(LayoutProbeInstance? candidate, Vector3 playerPosition, LocalLayoutTransformMode mode)
        => this.CreateFromCandidate(candidate, playerPosition, mode, template: null, applyTemplateModel: false);

    public LocalLayoutObjectInstance? CreateFromTemplate(LayoutProbeInstance? template, LayoutProbeInstance? targetSlot, Vector3 position, LocalLayoutTransformMode mode, bool applyTemplateModel = false)
        => this.CreateFromCandidate(targetSlot, position, mode, template, applyTemplateModel);

    private LocalLayoutObjectInstance? CreateFromCandidate(LayoutProbeInstance? candidate, Vector3 playerPosition, LocalLayoutTransformMode mode, LayoutProbeInstance? template, bool applyTemplateModel)
    {
        this.RebuildOccupiedSlotRegistry();
        if (candidate == null)
        {
            this.LastStatus = "请先查找并选择一个候选 BgPart。";
            return null;
        }

        if (!string.Equals(candidate.Type, "BgPart", StringComparison.Ordinal))
        {
            this.LastStatus = $"当前候选不是 BgPart：{candidate.Type}";
            return null;
        }

        if (TryNormalizeSlotAddress(candidate.Address, out var candidateSlotAddress)
            && this.occupiedSlots.TryGetValue(candidateSlotAddress, out var owner))
        {
            this.LastStatus = $"该 BgPart slot 已被实例 {owner.Id} 占用。";
            return owner;
        }

        if (!TryGetPointer(candidate.Address, out var pointer))
        {
            this.LastStatus = $"候选地址解析失败：{candidate.Address}";
            return null;
        }

        var originalLayout = ReadLayoutTransform(pointer);
        if (originalLayout == null)
        {
            this.LastStatus = "读取候选原始 layout transform 失败，未创建。";
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
                this.LastStatus = "读取 BgPart GraphicsObject 失败，无法创建 VisualOnly 本地物件。";
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
            TemplateSourceSlotAddress = template?.Address ?? candidate.Address,
            SourceResourcePath = candidate.ResourcePath,
            OriginalResourcePath = candidate.ResourcePath,
            CurrentResourcePath = candidate.ResourcePath,
            CustomModelPath = string.Empty,
            OriginalModelResourcePath = candidate.ResourcePath,
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
            OriginalVisualTransform = mode == LocalLayoutTransformMode.VisualOnly ? FormatSceneSnapshot(originalVisual) : "FullLayout 模式不使用视觉字段",
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
                ? "VisualOnly：只写 Graphics.Scene.Object transform，不移动 layout/collision。"
                : "危险：FullLayoutWithCollision 会写 layout transform 并移动碰撞体。",
        };

        this.instances.Add(instance);
        if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var occupiedSlotAddress))
            this.occupiedSlots[occupiedSlotAddress] = instance;
        this.WriteInstanceTransform(instance, playerPosition, instance.CurrentRotationEuler, instance.CurrentScale, "从候选 BgPart 创建本地物件实例");
        if (applyTemplateModel)
            this.LastStatus = "SetModel 当前为高风险实验，创建实例时已强制跳过自动模型替换。";
        return instance;
    }

    public IReadOnlyList<LocalLayoutObjectInstance> CreateManyFromTemplate(
        LayoutProbeInstance? template,
        IEnumerable<LayoutProbeInstance> candidateSlots,
        int count,
        Vector3 basePosition,
        LocalLayoutTransformMode mode,
        Vector3 spacing,
        bool allowDifferentResourcePathSlots = false)
    {
        var created = new List<LocalLayoutObjectInstance>();
        if (template == null)
        {
            this.LastStatus = "请先设置复制模板。";
            return created;
        }

        var availableSlots = candidateSlots
            .Where(slot => string.Equals(slot.Type, "BgPart", StringComparison.Ordinal))
            .Where(slot => !this.IsSlotOccupied(slot.Address));

        if (!allowDifferentResourcePathSlots)
            availableSlots = availableSlots.Where(slot => string.Equals(slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase));

        var slots = availableSlots
            .Take(Math.Max(0, count))
            .ToList();

        if (slots.Count < count)
        {
            this.LastStatus = allowDifferentResourcePathSlots
                ? $"当前地图可用 BgPart slot 不足，无法继续复制。需要 {count} 个，可用 {slots.Count} 个。"
                : $"同 resourcePath 可用 slot 不足，当前只能创建 {slots.Count} 个。不同外观 slot 不能伪装为模板，除非 SetModel per-instance 跑通。";
            return created;
        }

        for (var index = 0; index < slots.Count; index++)
        {
            var position = basePosition + spacing * index;
            var instance = this.CreateFromTemplate(template, slots[index], position, mode, applyTemplateModel: false);
            if (instance != null)
                created.Add(instance);
        }

        this.LastStatus = allowDifferentResourcePathSlots
            ? $"已从 {created.Count} 个不同/相同 resourcePath slot 创建实例。注意：这不是复制模板，只是移动不同物体。"
            : $"已从模板创建 {created.Count} 个同 resourcePath 本地实例。";
        return created;
    }

    public void UpdateExistingSlotToPlayer(LayoutProbeInstance? candidate, Vector3 playerPosition)
    {
        if (candidate == null)
        {
            this.LastStatus = "请先选择候选 BgPart。";
            return;
        }

        this.RebuildOccupiedSlotRegistry();
        if (!TryNormalizeSlotAddress(candidate.Address, out var candidateSlotAddress)
            || !this.occupiedSlots.TryGetValue(candidateSlotAddress, out var owner))
        {
            this.LastStatus = "该候选 slot 当前未被本地实例占用。";
            return;
        }

        this.MoveToPlayer(owner.Id, playerPosition);
    }

    public LocalLayoutObjectInstance? RestoreAndReoccupy(LayoutProbeInstance? candidate, Vector3 playerPosition, LocalLayoutTransformMode mode)
    {
        if (candidate == null)
        {
            this.LastStatus = "请先选择候选 BgPart。";
            return null;
        }

        this.RebuildOccupiedSlotRegistry();
        if (TryNormalizeSlotAddress(candidate.Address, out var candidateSlotAddress)
            && this.occupiedSlots.TryGetValue(candidateSlotAddress, out var owner))
            this.Delete(owner.Id);

        return this.CreateFromCandidate(candidate, playerPosition, mode);
    }

    public void MoveToPlayer(string id, Vector3 playerPosition)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, playerPosition, instance.CurrentRotationEuler, instance.CurrentScale, "移动实例到玩家当前位置");
    }

    public void MoveX(string id, float delta) => this.MoveBy(id, new Vector3(delta, 0f, 0f), $"X {(delta >= 0 ? "+" : string.Empty)}{delta:F1}");

    public void MoveY(string id, float delta) => this.MoveBy(id, new Vector3(0f, delta, 0f), $"Y {(delta >= 0 ? "+" : string.Empty)}{delta:F1}");

    public void MoveZ(string id, float delta) => this.MoveBy(id, new Vector3(0f, 0f, delta), $"Z {(delta >= 0 ? "+" : string.Empty)}{delta:F1}");

    public void ApplyVisualTransform(string id, Vector3 position, Vector3 rotationEuler, Vector3 scale)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, position, rotationEuler, scale, instance.TransformMode == LocalLayoutTransformMode.VisualOnly ? "应用 VisualOnly transform" : "应用 FullLayout transform");
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

        this.WriteInstanceTransform(instance, instance.CurrentPosition, Vector3.Zero, instance.CurrentScale, "重置旋转");
    }

    public void ResetScale(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        var targetScale = instance.TransformMode == LocalLayoutTransformMode.VisualOnly
            ? instance.OriginalVisualScale
            : instance.OccupiedSlotOriginalScale;
        this.WriteInstanceTransform(instance, instance.CurrentPosition, instance.CurrentRotationEuler, targetScale, "重置缩放");
    }

    public void AdjustScale(string id, float multiplier)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        var scale = Vector3.Max(instance.CurrentScale * multiplier, new Vector3(0.01f));
        this.WriteInstanceTransform(instance, instance.CurrentPosition, instance.CurrentRotationEuler, scale, $"缩放 x{multiplier:F2}");
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
                instance.LastError = "GraphicsObject 地址解析失败。";
                this.LastStatus = instance.LastError;
                return;
            }

            var current = ReadSceneObjectTransform(graphicsAddress);
            if (current == null)
            {
                instance.LastError = "读取当前 Scene.Object transform 失败。";
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
            this.LastStatus = $"已保存当前 VisualOnly transform：{instance.Id}。";
            return;
        }

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
        {
            instance.LastError = "slot 地址解析失败。";
            this.LastStatus = instance.LastError;
            return;
        }

        var layout = ReadLayoutTransform(pointer);
        if (layout == null)
        {
            instance.LastError = "读取当前 layout transform 失败。";
            this.LastStatus = instance.LastError;
            return;
        }

        instance.CurrentPosition = layout.Value.Position;
        instance.CurrentRotation = layout.Value.Rotation;
        instance.CurrentScale = layout.Value.Scale;
        instance.LastReadback = FormatSnapshot(layout.Value);
        instance.LastError = string.Empty;
        this.LastStatus = $"已保存当前 transform：{instance.Id}";
    }

    public bool ApplyModelOverride(string id, string modelPath)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.modelOverrideService.ApplyModel(instance, modelPath);
        this.LastStatus = this.modelOverrideService.LastResult;
        return success;
    }

    public string GetApplyModelBlockReason(string id, string modelPath, bool unsafeEnabled, bool confirmed)
    {
        var instance = this.GetById(id);
        return instance == null
            ? "未选中有效实例。"
            : this.modelOverrideService.GetApplyModelBlockReason(instance, modelPath, unsafeEnabled, confirmed);
    }

    public bool RestoreModel(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        instance.LastModelOverrideError = "SetModel 直接调用已暂停：ResourceCategory / 调用签名未确认，会崩溃。";
        instance.LastModelOverrideResult = string.Empty;
        this.LastStatus = instance.LastModelOverrideError;
        return false;
    }

    public bool RefreshModel(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.modelOverrideService.Refresh(instance);
        this.LastStatus = this.modelOverrideService.LastResult;
        return success;
    }

    public bool ExecuteModelRefreshStep(string id, string stepName)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.modelOverrideService.ExecuteRefreshStep(instance, stepName);
        this.LastStatus = this.modelOverrideService.LastResult;
        return success;
    }

    public bool ApplyModelOverrideWithRefreshChain(string id, string modelPath, string chainName)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.modelOverrideService.ApplyModelWithRefreshChain(instance, modelPath, chainName);
        this.LastStatus = this.modelOverrideService.LastResult;
        return success;
    }

    public bool ReapplyCurrentModelPath(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var currentPath = FirstNonEmpty(instance.AfterModelPath, instance.CurrentResourcePath, instance.SourceResourcePath);
        var success = this.modelOverrideService.ApplyModel(instance, currentPath);
        this.LastStatus = this.modelOverrideService.LastResult;
        return success;
    }

    public void RecordLiveReloadCandidate(string id, string candidateName)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        instance.LastModelOverrideResult = $"Live reload 候选入口已记录，未调用 native：{candidateName}";
        instance.LastModelOverrideError = "v7.8 只做取证按钮，不执行单对象 reload/reinitialize。";
        this.LastStatus = instance.LastModelOverrideResult;
    }

    public bool SaveRecreateSnapshot(string id, string targetPath)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.recreateExperimentService.SaveSnapshot(instance, targetPath);
        this.LastStatus = this.recreateExperimentService.LastResult;
        return success;
    }

    public bool ExecuteRecreateExperiment(string id, string targetPath, bool unsafeEnabled, bool experimentEnabled, bool confirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.recreateExperimentService.ExecuteDestroyCreate(instance, targetPath, unsafeEnabled, experimentEnabled, confirmed);
        this.LastStatus = this.recreateExperimentService.LastResult;
        return success;
    }

    public string GetRecreateExperimentBlockReason(string id, string targetPath, bool unsafeEnabled, bool experimentEnabled, bool confirmed)
    {
        var instance = this.GetById(id);
        return this.recreateExperimentService.GetExecuteBlockReason(instance, targetPath, unsafeEnabled, experimentEnabled, confirmed);
    }

    public bool CaptureCollisionSource(string id, LayoutProbeInstance? source)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.collisionExperimentService.CaptureSource(instance, source);
        this.LastStatus = this.collisionExperimentService.LastResult;
        return success;
    }

    public bool SaveCollisionSnapshot(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.collisionExperimentService.SaveSnapshot(instance);
        this.LastStatus = this.collisionExperimentService.LastResult;
        return success;
    }

    public bool ApplyCollisionSource(string id, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.collisionExperimentService.ApplySourceCollision(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
        this.LastStatus = this.collisionExperimentService.LastResult;
        return success;
    }

    public bool RestoreCollisionSource(string id, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var success = this.collisionExperimentService.RestoreCollision(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
        this.LastStatus = this.collisionExperimentService.LastResult;
        return success;
    }

    public string GetApplyCollisionSourceBlockReason(string id, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        var instance = this.GetById(id);
        return this.collisionExperimentService.GetApplyBlockReason(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
    }

    public string GetRestoreCollisionSourceBlockReason(string id, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        var instance = this.GetById(id);
        return this.collisionExperimentService.GetRestoreBlockReason(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
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
        var preCleanupCount = this.CleanupDuplicateInstances(auto: true);
        this.RebuildOccupiedSlotRegistry();
        var restoreCount = this.occupiedSlots.Count;
        foreach (var instance in this.occupiedSlots.Values.ToList())
            this.RestoreOriginal(instance, removeAfterRestore);

        if (removeAfterRestore)
            this.instances.RemoveAll(item => item.IsRestored || item.IsDuplicate);

        var postRestoreStaleCount = this.RemoveStaleRecords();
        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = removeAfterRestore
            ? $"已自动清理 {preCleanupCount} 条重复/残留记录，恢复/移除 {restoreCount} 个 occupied slot，恢复后清理 {postRestoreStaleCount} 条列表记录。"
            : $"已自动清理 {preCleanupCount} 条重复/残留记录，恢复 {restoreCount} 个 occupied slot，恢复后清理 {postRestoreStaleCount} 条列表记录。";
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

            this.WriteInstanceTransform(instance, playerPosition, instance.CurrentRotationEuler, instance.CurrentScale, "全部移回玩家脚下");
            moved++;
        }

        this.LastStatus = $"已将 {moved} 个 active VisualOnly slot 的视觉模型移回玩家脚下。";
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
            if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var occupiedSlotAddress))
                this.occupiedSlots.Remove(occupiedSlotAddress);
            restored++;
        }

        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = $"已恢复 {restored} 个 active VisualOnly slot 的原 visual transform。";
    }

    public int CleanupDuplicateInstances(bool auto = false)
    {
        this.RebuildOccupiedSlotRegistry();
        var duplicateGroups = this.GetActiveInstances()
            .GroupBy(entry => entry.SlotAddress)
            .Where(group => group.Count() > 1)
            .ToList();

        var affectedSlotCount = duplicateGroups.Count;
        var duplicateIds = duplicateGroups
            .SelectMany(group => group.OrderBy(entry => entry.Index).Skip(1))
            .Select(entry => entry.Instance.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var staleDuplicate in this.instances.Where(item => item.IsDuplicate && !string.IsNullOrWhiteSpace(item.OccupiedSlotAddress)))
            duplicateIds.Add(staleDuplicate.Id);

        var staleIds = this.instances
            .Where(item => !duplicateIds.Contains(item.Id) && IsStaleRecord(item))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var instance in this.instances.Where(item => duplicateIds.Contains(item.Id)))
        {
            instance.IsDuplicate = true;
            instance.IsInvalid = true;
            instance.IsOccupied = false;
            instance.IsRestored = true;
            instance.LastError = "重复 slot 实例已清理，未执行 restore。";
            instance.Notes = auto
                ? "自动恢复全部前清理的重复 slot 实例，未执行 restore。"
                : "手动清理的重复 slot 实例，未执行 restore。";
        }

        foreach (var instance in this.instances.Where(item => staleIds.Contains(item.Id)))
        {
            instance.IsInvalid = true;
            instance.IsOccupied = false;
            instance.IsRestored = true;
            instance.LastError = "残留列表记录已清理。";
            instance.Notes = auto
                ? "自动恢复全部前清理的残留列表记录。"
                : "手动清理的残留列表记录。";
        }

        var removed = this.instances.RemoveAll(item => duplicateIds.Contains(item.Id));
        var staleRemoved = this.instances.RemoveAll(item => staleIds.Contains(item.Id));
        var totalRemoved = removed + staleRemoved;
        this.RebuildOccupiedSlotRegistry();
        if (!auto)
        {
            this.LastStatus = totalRemoved == 0
                ? "没有任何需要清理的重复实例或残留列表记录。"
                : removed == 0
                    ? $"没有场景重复实例，但已清理 {staleRemoved} 条残留列表记录。"
                    : $"已清理重复实例 {removed} 个，影响 {affectedSlotCount} 个 slot；同时清理 {staleRemoved} 条残留列表记录。";
        }

        return totalRemoved;
    }

    public LocalLayoutObjectInstance? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var instance = this.instances.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (instance == null)
            this.LastStatus = $"找不到本地场景物体实例：{id}";

        return instance;
    }

    private void MoveBy(string id, Vector3 delta, string action)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, instance.CurrentPosition + delta, instance.CurrentRotationEuler, instance.CurrentScale, action);
    }

    private int RemoveStaleRecords()
    {
        var staleIds = this.instances
            .Where(IsStaleRecord)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        return this.instances.RemoveAll(item => staleIds.Contains(item.Id));
    }

    private void RebuildOccupiedSlotRegistry()
    {
        this.occupiedSlots.Clear();
        foreach (var instance in this.instances)
        {
            if (!IsActiveInstance(instance, out var slotAddress))
                continue;

            if (this.occupiedSlots.ContainsKey(slotAddress))
            {
                instance.IsDuplicate = true;
                instance.Notes = "重复 slot 实例，已标记 invalid，不参与 RestoreAll。";
                continue;
            }

            instance.IsDuplicate = false;
            this.occupiedSlots[slotAddress] = instance;
        }
    }

    private void RestoreOriginal(LocalLayoutObjectInstance instance, bool removeAfterRestore)
    {
        if (instance.IsRenderInvalid)
        {
            instance.IsOccupied = false;
            instance.IsRestored = true;
            instance.IsInvalid = true;
            instance.LastError = "实例 render 已失效，无法安全恢复，请切图/重载地图恢复。";
            instance.TransformWriteDisabledReason = instance.LastError;
            if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var invalidSlotAddress))
                this.occupiedSlots.Remove(invalidSlotAddress);
            if (removeAfterRestore)
                this.instances.Remove(instance);
            this.LastStatus = instance.LastError;
            return;
        }

        if (instance.IsDuplicate)
        {
            instance.LastError = "重复 slot 实例不参与恢复，避免覆盖原始 transform。";
            this.LastStatus = instance.LastError;
            if (removeAfterRestore)
                this.instances.Remove(instance);
            return;
        }

        if (!instance.CanRestore)
        {
            instance.LastError = "没有可恢复的原始 transform。";
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
        if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var occupiedSlotAddress))
            this.occupiedSlots.Remove(occupiedSlotAddress);
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
            instance.LastError = "重复 slot 实例禁止写入。";
            this.LastStatus = instance.LastError;
            return;
        }

        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
        {
            instance.LastError = $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}";
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

            this.LastStatus = $"{action} 完成：VisualOnly 写 Graphics.Scene.Object Position/Rotation/Scale，不写 layout/collision。";
        }
        catch (Exception ex)
        {
            instance.LastError = $"{action} 失败：{ex.Message}";
            this.LastStatus = instance.LastError;
        }
    }

    private void RestoreOriginalVisualTransform(LocalLayoutObjectInstance instance, string action)
    {
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
        {
            instance.LastError = $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}";
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
            this.LastStatus = $"{action} 完成：已恢复 Scene.Object Position/Rotation/Scale。";
        }
        catch (Exception ex)
        {
            instance.LastError = $"{action} 失败：{ex.Message}";
            this.LastStatus = instance.LastError;
        }
    }

    private void WriteLayoutTransform(LocalLayoutObjectInstance instance, Vector3 position, Quaternion rotation, Vector3 scale, string action)
    {
        if (instance.IsDuplicate)
        {
            instance.LastError = "重复 slot 实例禁止写入。";
            this.LastStatus = instance.LastError;
            return;
        }

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
        {
            instance.LastError = $"slot 地址解析失败：{instance.OccupiedSlotAddress}";
            this.LastStatus = instance.LastError;
            return;
        }

        var target = new LayoutTransformSnapshot(position, rotation, scale);
        if (!WriteLayoutTransform(pointer, target))
        {
            instance.LastError = $"{action} 失败：SetTransform 未成功。";
            this.LastStatus = instance.LastError;
            return;
        }

        var after = ReadLayoutTransform(pointer);
        if (after == null)
        {
            instance.LastError = $"{action} 后 readback 失败。";
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
        this.LastStatus = $"{action} 完成：FullLayoutWithCollision 会移动碰撞体。readback={FormatVector(after.Value.Position)}";
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

    private IEnumerable<(LocalLayoutObjectInstance Instance, int Index, ulong SlotAddress)> GetActiveInstances()
    {
        for (var index = 0; index < this.instances.Count; index++)
        {
            var instance = this.instances[index];
            if (IsActiveInstance(instance, out var slotAddress))
                yield return (instance, index, slotAddress);
        }
    }

    private static bool IsActiveInstance(LocalLayoutObjectInstance instance, out ulong slotAddress)
    {
        slotAddress = 0;
        return instance.IsOccupied
            && !instance.IsRestored
            && !instance.IsInvalid
            && TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out slotAddress)
            && slotAddress != 0;
    }

    private static bool IsStaleRecord(LocalLayoutObjectInstance instance)
        => instance.IsRestored
            || instance.IsInvalid
            || !instance.IsOccupied
            || !TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var slotAddress)
            || slotAddress == 0;

    private static bool TryNormalizeSlotAddress(string? raw, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return ulong.TryParse(raw, out address)
            || ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    private static string FormatSnapshot(LayoutTransformSnapshot snapshot)
        => $"position=({FormatVector(snapshot.Position)}), rotation={snapshot.Rotation}, scale=({FormatVector(snapshot.Scale)})";

    private static string FormatMatrix(Matrix4x4 matrix)
        => $"translation=({FormatVector(GetMatrixTranslation(matrix))}); M11={matrix.M11:F3}, M22={matrix.M22:F3}, M33={matrix.M33:F3}, M44={matrix.M44:F3}";

    private static string FormatSceneSnapshot(SceneTransformSnapshot snapshot)
        => $"position=({FormatVector(snapshot.Position)}), rotation={snapshot.Rotation}, scale=({FormatVector(snapshot.Scale)}), IsTransformChanged={snapshot.IsTransformChanged}";

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private readonly record struct SceneTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool IsTransformChanged);
}



