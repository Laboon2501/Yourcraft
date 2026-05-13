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
    private readonly BgPartCollisionSourceResolver collisionSourceResolver = new();
    private readonly AnimatedPlaybackSystem animatedPlaybackSystem = new();
    private readonly List<LocalLayoutObjectInstance> instances = [];
    private readonly Dictionary<ulong, LocalLayoutObjectInstance> occupiedSlots = [];
    private bool animatedCreateBusy;

    public bool AutoPinDynamicTransforms { get; set; }

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

    public string LastAnimatedPlaybackStatus => this.animatedPlaybackSystem.LastStatus;

    public int AnimatedPlaybackCount => this.animatedPlaybackSystem.PlaybackCount;

    public int AnimatedGroupCount => this.animatedPlaybackSystem.GroupCount;

    public IReadOnlyList<LocalAnimatedGroupInstance> AnimatedGroups => this.animatedPlaybackSystem.Groups;

    public void StopAllPlayback(string reason = "用户手动停止全部动画回放。")
        => this.animatedPlaybackSystem.StopAllAndDetach(this.instances, reason);

    public void Update()
    {
        foreach (var instance in this.instances.Where(item => item.PendingVisualTransform).ToList())
        {
            if (this.recreateExperimentService.ProcessPendingVisualTransform(instance))
                this.ScheduleTransformMonitor(instance, instance.CurrentPosition, instance.CurrentRotationEuler, instance.CurrentScale, "延迟 VisualOnly transform");
        }

        foreach (var instance in this.instances.Where(item => item.TransformMonitorActive).ToList())
            this.UpdateTransformMonitor(instance);

        this.animatedPlaybackSystem.Update(this.instances);
    }

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
            Id = $"layout-object-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..45],
            TemplateSourceSlotAddress = template?.Address ?? candidate.Address,
            TemplateResourcePath = template?.ResourcePath ?? candidate.ResourcePath,
            SourceResourcePath = candidate.ResourcePath,
            SourceKind = candidate.SourceKind,
            SourceSharedGroupPath = candidate.SharedGroupPath,
            SourceParentAddress = candidate.ParentAddress,
            SourceParentKey = candidate.ParentKey,
            SourceChildIndex = candidate.ChildIndex,
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
            OriginalVisible = candidate.Visible,
            CarrierRejectReason = string.Empty,
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
        bool allowDifferentResourcePathSlots = false,
        string defaultCustomMdlPath = "",
        IEnumerable<LayoutProbeInstance>? bgParts = null,
        bool unsafeEnabled = false,
        bool fullLayoutConfirmed = false)
    {
        var created = new List<LocalLayoutObjectInstance>();
        if (template == null)
        {
            this.LastStatus = "请先设置复制模板。";
            return created;
        }

        count = Math.Max(0, count);
        var customMdlPath = (defaultCustomMdlPath ?? string.Empty).Trim();
        var hasCustomMdlPath = !string.IsNullOrWhiteSpace(customMdlPath);
        if (hasCustomMdlPath && (!customMdlPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || !IsSupportedMdlPath(customMdlPath)))
        {
            this.LastStatus = "批量 custom mdl path 必须以 .mdl 结尾，并且只支持 bg/...mdl 或 bgcommon/...mdl。";
            return created;
        }

        if (hasCustomMdlPath && !unsafeEnabled)
        {
            this.LastStatus = "批量应用 custom mdl path 需要 UnsafeMode=true。";
            return created;
        }

        if (hasCustomMdlPath && mode == LocalLayoutTransformMode.FullLayoutWithCollision && !fullLayoutConfirmed)
        {
            this.LastStatus = "FullLayoutWithCollision 批量应用 custom mdl path 需要二次确认。";
            return created;
        }

        var indexedSlots = candidateSlots
            .Select((slot, index) => new { Slot = slot, Index = index });

        var availableSlots = indexedSlots
            .Where(slot => string.Equals(slot.Slot.Type, "BgPart", StringComparison.Ordinal))
            .Where(slot => !string.Equals(slot.Slot.Address, template.Address, StringComparison.OrdinalIgnoreCase))
            .Where(slot => !this.IsSlotOccupied(slot.Slot.Address));

        if (hasCustomMdlPath || allowDifferentResourcePathSlots)
        {
            availableSlots = availableSlots
                .Where(slot => IsSupportedMdlPath(slot.Slot.ResourcePath))
                .OrderBy(slot => string.Equals(slot.Slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(slot => slot.Index);
        }
        else
        {
            availableSlots = availableSlots
                .Where(slot => string.Equals(slot.Slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(slot => slot.Index);
        }

        var slots = availableSlots
            .Select(slot => slot.Slot)
            .Take(Math.Max(0, count))
            .ToList();

        if (slots.Count < count)
        {
            this.LastStatus = hasCustomMdlPath || allowDifferentResourcePathSlots
                ? $"可用 slot 不足：请求 {count}，可用 {slots.Count}。模板本体 slot 已排除。"
                : $"同 resourcePath 可用 slot 不足：请求 {count}，可用 {slots.Count}。模板本体 slot 已排除；未填写 custom mdl path 时不会使用不同外观 slot。";
            return created;
        }

        var mdlAppliedCount = 0;
        var failures = new List<string>();
        for (var index = 0; index < slots.Count; index++)
        {
            var position = basePosition + spacing * index;
            var instance = this.CreateFromTemplate(template, slots[index], position, mode, applyTemplateModel: false);
            if (instance == null)
                continue;

            created.Add(instance);
            if (!hasCustomMdlPath)
                continue;

            instance.CustomModelPath = customMdlPath;
            var applied = this.ApplyMdlPath(
                instance.Id,
                customMdlPath,
                bgParts ?? candidateSlots,
                unsafeEnabled,
                fullLayoutConfirmed || mode == LocalLayoutTransformMode.VisualOnly);
            if (!applied)
            {
                instance.ApplyMdlStatus = "Failed";
                instance.ApplyMdlError = FirstNonEmpty(instance.ApplyMdlError, instance.LastModelOverrideError, this.LastStatus);
                failures.Add($"{instance.Id}: {instance.ApplyMdlError}");
                continue;
            }

            mdlAppliedCount++;
        }

        this.LastStatus = hasCustomMdlPath
            ? $"批量完成：createdCount={created.Count}; mdlAppliedCount={mdlAppliedCount}; mdlFailedCount={failures.Count}; failed={string.Join(" | ", failures)}"
            : allowDifferentResourcePathSlots
                ? $"已从 {created.Count} 个 bg/bgcommon 可用 slot 创建实例。未应用 custom mdl path 时，不建议把它当作模板外观复制。模板 slot 已排除。"
                : $"已从模板创建 {created.Count} 个同 resourcePath 本地实例。模板 slot 已排除。";
        return created;
    }

    public IReadOnlyList<LocalLayoutObjectInstance> CreateAnimatedFromSource(
        LayoutProbeInstance? source,
        IEnumerable<LayoutProbeInstance> allBgParts,
        Vector3 basePosition,
        LocalLayoutTransformMode mode,
        bool unsafeEnabled,
        bool fullLayoutConfirmed)
    {
        var created = new List<LocalLayoutObjectInstance>();
        if (this.animatedCreateBusy)
        {
            this.LastStatus = "动态本地实例正在创建中，请等待当前操作完成。";
            return created;
        }

        this.animatedCreateBusy = true;
        try
        {
        if (source == null)
        {
            this.LastStatus = "请先选择一个动态 source BgPart。";
            return created;
        }

        if (!string.Equals(source.Type, "BgPart", StringComparison.Ordinal))
        {
            this.LastStatus = $"动态 source 必须是 BgPart：{source.Type}";
            return created;
        }

        if (!unsafeEnabled)
        {
            this.LastStatus = "创建动态本地实例需要 UnsafeMode=true。";
            return created;
        }

        if (mode == LocalLayoutTransformMode.FullLayoutWithCollision && !fullLayoutConfirmed)
        {
            this.LastStatus = "FullLayoutWithCollision 创建动态本地实例需要二次确认。";
            return created;
        }

        var all = allBgParts.ToList();
        var isSharedGroupSource = string.Equals(source.SourceKind, "SharedGroup", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(source.ParentAddress);
        if (isSharedGroupSource)
        {
            var children = all
                .Where(item => string.Equals(item.SourceKind, "SharedGroup", StringComparison.Ordinal))
                .Where(item => string.Equals(item.ParentAddress, source.ParentAddress, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(item.Type, "BgPart", StringComparison.Ordinal))
                .OrderBy(item => item.ChildIndex)
                .ToList();

            if (children.Count > 1)
                return this.CreateVisibilityCyclingGroup(source, children, all, basePosition, mode, unsafeEnabled, fullLayoutConfirmed);
        }

        var carrier = this.FindStableCarrierSlots(all, excludedAddresses: [source.Address])
            .FirstOrDefault();
        if (carrier == null)
        {
            this.LastStatus = "没有可用的静态 carrier slot。需要非 SharedGroup child、未占用、bg/bgcommon 的 BgPart。";
            return created;
        }

        var instance = this.CreateFromTemplate(source, carrier, basePosition, mode, applyTemplateModel: false);
        if (instance == null)
            return created;

        created.Add(instance);
        instance.AnimationPlaybackMode = AnimationPlaybackMode.TransformDelta;
        instance.AnimationPlaybackEnabled = true;
        if (!string.Equals(instance.CurrentResourcePath, source.ResourcePath, StringComparison.OrdinalIgnoreCase))
            this.ApplyMdlPath(instance.Id, source.ResourcePath, all, unsafeEnabled, fullLayoutConfirmed || mode == LocalLayoutTransformMode.VisualOnly);
        this.animatedPlaybackSystem.ConfigureTransformDelta(instance, source);
        this.LastStatus = $"已创建 TransformDelta 动态本地实例：source={source.Address}; carrier={carrier.Address}; instance={instance.Id}";
        return created;
        }
        catch (Exception ex)
        {
            foreach (var instance in created.ToList())
            {
                this.DisablePlayback(instance, "动态创建异常，rollback 停止 playback。");
                this.RestoreOriginal(instance, removeAfterRestore: true);
            }

            this.LastStatus = $"创建动态本地实例失败，已 rollback：{ex.Message}";
            return [];
        }
        finally
        {
            this.animatedCreateBusy = false;
        }
    }

    private IReadOnlyList<LocalLayoutObjectInstance> CreateVisibilityCyclingGroup(
        LayoutProbeInstance source,
        IReadOnlyList<LayoutProbeInstance> children,
        IReadOnlyList<LayoutProbeInstance> allBgParts,
        Vector3 basePosition,
        LocalLayoutTransformMode mode,
        bool unsafeEnabled,
        bool fullLayoutConfirmed)
    {
        var excluded = children.Select(child => child.Address).Append(source.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var carriers = this.FindStableCarrierSlots(allBgParts, excluded)
            .Take(children.Count)
            .ToList();
        var created = new List<LocalLayoutObjectInstance>();
        if (carriers.Count < children.Count)
        {
            this.LastStatus = $"可用 carrier 不足：VisibilityCycling 需要 {children.Count} 个，可用 {carriers.Count} 个。不会创建半成品 group。";
            return created;
        }

        var groupId = $"animated-group-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..46];
        var failures = new List<string>();
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var carrier = carriers[index];
            var instance = this.CreateFromTemplate(child, carrier, basePosition, mode, applyTemplateModel: false);
            if (instance == null)
            {
                failures.Add($"child {child.ChildIndex}: create carrier failed");
                break;
            }

            created.Add(instance);
            if (!string.Equals(instance.CurrentResourcePath, child.ResourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var applied = this.ApplyMdlPath(instance.Id, child.ResourcePath, allBgParts, unsafeEnabled, fullLayoutConfirmed || mode == LocalLayoutTransformMode.VisualOnly);
                if (!applied)
                {
                    failures.Add($"child {child.ChildIndex}: mdl failed={FirstNonEmpty(instance.ApplyMdlError, instance.LastModelOverrideError, this.LastStatus)}");
                    break;
                }
            }
        }

        if (failures.Count > 0 || created.Count != children.Count)
        {
            foreach (var instance in created.ToList())
            {
                this.DisablePlayback(instance, "VisibilityCycling 创建失败，rollback 停止 playback。");
                this.RestoreOriginal(instance, removeAfterRestore: true);
            }

            this.LastStatus = $"VisibilityCycling 创建失败，已 rollback：{string.Join(" | ", failures)}";
            return [];
        }

        for (var index = 0; index < children.Count; index++)
            this.animatedPlaybackSystem.ConfigureVisibilityCycling(created[index], children[index], groupId, basePosition);

        this.animatedPlaybackSystem.RegisterGroup(new LocalAnimatedGroupInstance
        {
            GroupId = groupId,
            SourceSharedGroup = FirstNonEmpty(source.SharedGroupPath, source.ParentAddress, source.ResourcePath),
            ChildInstanceIds = created.Select(item => item.Id).ToList(),
            CarrierSlotAddresses = created.Select(item => item.OccupiedSlotAddress).ToList(),
            PlaybackEnabled = true,
            RestoreStatus = "播放中",
        });

        this.LastStatus = $"已创建 VisibilityCycling 动态本地组：children={children.Count}; created={created.Count}; group={groupId}; failures={string.Join(" | ", failures)}";
        return created;
    }

    private IEnumerable<LayoutProbeInstance> FindStableCarrierSlots(IEnumerable<LayoutProbeInstance> allBgParts, IEnumerable<string> excludedAddresses)
    {
        var excluded = excludedAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allBgParts
            .Where(slot => string.IsNullOrWhiteSpace(this.GetCarrierRejectReason(slot, excluded)))
            .OrderByDescending(slot => !slot.Visible)
            .ThenBy(slot => IsSmallDecorativeCarrier(slot) ? 0 : 1)
            .ThenByDescending(slot => slot.DistanceToPlayer);
    }

    public string GetCarrierRejectReason(LayoutProbeInstance? slot)
        => this.GetCarrierRejectReason(slot, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string GetCarrierRejectReason(LayoutProbeInstance? slot, ISet<string> excludedAddresses)
    {
        if (slot == null)
            return "空 slot";
        string reason;
        if (!string.Equals(slot.Type, "BgPart", StringComparison.Ordinal))
            reason = "不是 BgPart";
        else if (string.Equals(slot.SourceKind, "SharedGroup", StringComparison.Ordinal))
            reason = "SharedGroupChild";
        else if (!IsSupportedMdlPath(slot.ResourcePath))
            reason = "非 bg/bgcommon mdl";
        else if (excludedAddresses.Contains(slot.Address))
            reason = "source/template slot 已排除";
        else if (this.IsSlotOccupied(slot.Address))
            reason = "AlreadyOccupied";
        else if (IsTerrainLikeCarrier(slot.ResourcePath))
            reason = "TerrainLike";
        else if (IsTooLargeCarrier(slot))
            reason = "TooLarge";
        else
            reason = string.Empty;

        slot.CarrierRejectReason = reason;
        return reason;
    }

    private static bool IsTerrainLikeCarrier(string resourcePath)
    {
        var path = resourcePath.ToLowerInvariant();
        string[] blocked =
        [
            "floor",
            "wall",
            "base",
            "terrain",
            "land",
            "map",
            "bgbase",
            "sky",
            "sea",
            "collision",
            "col_",
        ];
        return blocked.Any(path.Contains);
    }

    private static bool IsTooLargeCarrier(LayoutProbeInstance slot)
        => Math.Abs(slot.Scale.X) > 20f
            || Math.Abs(slot.Scale.Y) > 20f
            || Math.Abs(slot.Scale.Z) > 20f;

    private static bool IsSmallDecorativeCarrier(LayoutProbeInstance slot)
        => Math.Abs(slot.Scale.X) <= 5f
            && Math.Abs(slot.Scale.Y) <= 5f
            && Math.Abs(slot.Scale.Z) <= 5f;

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

    public void SetPinTransform(string id, bool enabled)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        if (enabled)
            instance.PinFailed = false;

        instance.PinTransformEnabled = enabled;
        instance.PinTransformReason = enabled
            ? "用户手动启用 PinTransform。"
            : "用户手动关闭 PinTransform。";
        instance.LastPinWriteResult = enabled
            ? "PinTransform 已启用，下一帧开始写回目标 transform。"
            : "PinTransform 已关闭。";
        if (enabled)
        {
            instance.PinWriteFailedCount = 0;
            instance.PinTargetPosition = instance.CurrentPosition;
            instance.PinTargetRotationEuler = instance.CurrentRotationEuler;
            instance.PinTargetScale = instance.CurrentScale;
        }

        this.LastStatus = instance.LastPinWriteResult;
    }

    public void DisablePlayback(string id, string reason)
    {
        var instance = this.GetById(id);
        if (instance != null)
            this.DisablePlayback(instance, reason);
    }

    private void DisablePlayback(LocalLayoutObjectInstance instance, string reason)
    {
        this.animatedPlaybackSystem.DisablePlayback(instance, reason);
        instance.PinTransformEnabled = false;
        instance.TransformMonitorActive = false;
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

    public bool ApplyMdlPath(
        string id,
        string modelPath,
        IEnumerable<LayoutProbeInstance> bgParts,
        bool unsafeEnabled,
        bool fullLayoutConfirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        var blockReason = this.GetApplyMdlPathBlockReason(instance, modelPath, unsafeEnabled, fullLayoutConfirmed);
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            instance.ApplyMdlStatus = "Failed";
            instance.ApplyMdlError = blockReason;
            instance.LastModelOverrideError = blockReason;
            this.LastStatus = blockReason;
            return false;
        }

        modelPath = modelPath.Trim();
        instance.ApplyMdlStatus = "Applying";
        instance.ModelApplyStatus = "Applying";
        instance.ApplyMdlError = string.Empty;
        instance.TransformWriteDisabledReason = string.Empty;
        instance.CustomModelPath = modelPath;
        instance.TargetModelPath = modelPath;

        if (!this.recreateExperimentService.ExecuteDestroyCreate(instance, modelPath, unsafeEnabled, experimentEnabled: true, confirmed: true))
        {
            instance.ApplyMdlStatus = "Failed";
            instance.ApplyMdlError = this.recreateExperimentService.LastResult;
            instance.LastModelOverrideError = this.recreateExperimentService.LastResult;
            this.LastStatus = this.recreateExperimentService.LastResult;
            return false;
        }

        instance.BeforeModelPath = FirstNonEmpty(instance.BeforeModelPath, instance.RecreateSnapshotOriginalPath, instance.CurrentResourcePath);
        instance.AfterModelPath = FirstNonEmpty(instance.AfterModelPath, modelPath);
        instance.CurrentResourcePath = FirstNonEmpty(instance.AfterModelPath, modelPath, instance.CurrentResourcePath);
        instance.ModelOverrideApplied = !string.Equals(instance.CurrentResourcePath, instance.OriginalModelResourcePath, StringComparison.OrdinalIgnoreCase);
        instance.CollisionApplied = false;
        instance.CollisionError = string.Empty;

        if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
        {
            instance.HasCollisionMoved = false;
            instance.CollisionSourceResolveResult = "VisualOnly：不解析、不复制、不创建 collision；替换 mdl 后延迟应用 Graphics.Scene.Object transform。";
            instance.LastModelOverrideResult = instance.ModelApplyStatus switch
            {
                "AnimatedStaticOnly" => "自带动画/动态材质模型可能只显示静态外观；动画需要原 layout controller/shared group/event update 支持，暂未支持。",
                "UnsafeComplexModel" => "复杂动态模型已 recreate，但自动 transform 写入被安全保护拦截。",
                "UnsafeAfterRecreate" => "recreate 后 GraphicsObject 状态不安全，已停止 transform 写入。",
                _ when instance.PendingVisualTransform => "VisualOnly 已 recreate，等待数帧后安全写入 transform。",
                _ => $"VisualOnly 已应用 mdl path：{modelPath}；collision moved=false。",
            };
            instance.ApplyMdlStatus = string.IsNullOrWhiteSpace(instance.ModelApplyStatus)
                ? instance.PendingVisualTransform ? "PendingVisualTransform" : "Applied"
                : instance.ModelApplyStatus;
            instance.ApplyMdlError = instance.IsRenderInvalid ? instance.TransformWriteDisabledReason : string.Empty;
            if (!instance.IsRenderInvalid
                && string.Equals(instance.ModelApplyStatus, "UnsafeComplexModel", StringComparison.Ordinal)
                && this.AutoPinDynamicTransforms
                && instance.PinTransformAutoEnabled
                && !instance.PinFailed)
            {
                instance.PinTargetPosition = instance.CurrentPosition;
                instance.PinTargetRotationEuler = instance.CurrentRotationEuler;
                instance.PinTargetScale = instance.CurrentScale;
                instance.PinTransformEnabled = true;
                instance.ControlledByRuntime = true;
                instance.PinTransformReason = "复杂/动态模型 recreate 后不做同帧写入，改由 PinTransform 每帧固定到用户目标位置。";
                instance.LastPinWriteResult = "等待下一次 Framework.Update 写回 pin target。";
            }

            this.LastStatus = instance.LastModelOverrideResult;
            return !instance.IsRenderInvalid;
        }

        var resolve = this.collisionSourceResolver.Resolve(modelPath, bgParts);
        instance.CollisionSourceResolveResult = resolve.Message;
        if (resolve.Found && resolve.SourceInstance != null)
        {
            var captured = this.collisionExperimentService.CaptureSource(instance, resolve.SourceInstance);
            if (captured)
                this.collisionExperimentService.ApplySourceCollision(instance, unsafeEnabled, fullLayoutConfirmed: true, confirmed: true);
            if (!captured)
                instance.CollisionExperimentLastError = this.collisionExperimentService.LastResult;
            instance.CollisionApplied = string.IsNullOrWhiteSpace(instance.CollisionExperimentLastError);
            instance.CollisionError = instance.CollisionApplied ? string.Empty : instance.CollisionExperimentLastError;
        }
        else
        {
            instance.CollisionApplied = false;
            instance.CollisionError = "未找到目标 mdl 对应的 collision source，模型已替换，但 collision 未替换/保持原状态。";
        }

        this.transformService.ApplyTransform(instance);
        instance.LastReadback = this.transformService.LastResult;
        instance.LastModelOverrideResult = instance.CollisionApplied
            ? $"FullLayoutWithCollision 已应用 mdl path 和 target collision：{modelPath}；source={instance.CollisionSourceBgPartAddress}；type={instance.CollisionSourceColliderType}。"
            : $"FullLayoutWithCollision 已应用 mdl path：{modelPath}；{instance.CollisionError}";
        instance.ApplyMdlStatus = instance.CollisionApplied ? "Applied" : "AppliedWithoutCollisionSource";
        instance.ApplyMdlError = instance.CollisionApplied ? string.Empty : instance.CollisionError;
        this.LastStatus = instance.LastModelOverrideResult;
        return true;
    }

    public bool RestoreModelAndTransform(
        string id,
        IEnumerable<LayoutProbeInstance> bgParts,
        bool unsafeEnabled,
        bool fullLayoutConfirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return false;

        this.DisablePlayback(instance, "恢复原 mdl / transform 前停止动画回放。");
        var originalPath = FirstNonEmpty(instance.OriginalModelResourcePath, instance.OriginalResourcePath, instance.SourceResourcePath);
        var ok = true;
        instance.RestoreStatus = "Restoring";
        if (!string.IsNullOrWhiteSpace(originalPath) && unsafeEnabled)
            ok = this.ApplyMdlPath(id, originalPath, bgParts, unsafeEnabled, fullLayoutConfirmed || instance.TransformMode == LocalLayoutTransformMode.VisualOnly);

        if (instance.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision && !string.IsNullOrWhiteSpace(instance.CollisionSnapshotColliderType))
            this.collisionExperimentService.RestoreCollision(instance, unsafeEnabled, fullLayoutConfirmed: true, confirmed: true);

        if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
        {
            instance.PendingVisualTransform = false;
            instance.PendingVisualTransformFrameWait = 0;
            instance.PendingVisualTransformResult = "恢复流程取消待写 VisualOnly transform，slot 应回到原始视觉位置。";
        }

        this.RestoreOriginal(instance, removeAfterRestore: false);
        instance.ModelOverrideApplied = false;
        instance.CurrentResourcePath = originalPath;
        instance.CurrentModelPath = originalPath;
        instance.CustomModelPath = string.Empty;
        instance.ApplyMdlStatus = "Restored";
        instance.ApplyMdlError = string.Empty;
        instance.RestoreStatus = ok ? "Restored" : $"RestoreModelFailed：{instance.LastModelOverrideError}";
        this.LastStatus = $"已恢复原 mdl / transform：{instance.Id}。modelRestore={ok}; transform={this.transformService.LastResult}";
        return ok;
    }

    public string GetApplyMdlPathBlockReason(string id, string modelPath, bool unsafeEnabled, bool fullLayoutConfirmed)
    {
        var instance = this.GetById(id);
        return instance == null
            ? "未选中有效实例。"
            : this.GetApplyMdlPathBlockReason(instance, modelPath, unsafeEnabled, fullLayoutConfirmed);
    }

    private string GetApplyMdlPathBlockReason(LocalLayoutObjectInstance instance, string modelPath, bool unsafeEnabled, bool fullLayoutConfirmed)
    {
        if (!unsafeEnabled)
            return "UnsafeMode=false。";
        if (instance.IsInvalid || instance.IsRestored || instance.IsDuplicate)
            return "实例已失效、已恢复或是重复记录。";
        if (instance.IsRenderInvalid)
            return "实例 render 已失效，请切图/重载地图恢复后再应用 mdl path。";
        if (string.IsNullOrWhiteSpace(modelPath))
            return "custom mdl path 为空。";
        if (!modelPath.Trim().EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return "custom mdl path 必须以 .mdl 结尾。";
        if (!IsSupportedMdlPath(modelPath.Trim()))
            return "custom mdl path 只支持 bg/...mdl 或 bgcommon/...mdl。";
        if (instance.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision && !fullLayoutConfirmed)
            return "FullLayoutWithCollision 需要二次确认。";
        return string.Empty;
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

    public void RefreshAnimationCapabilityDump(string id, LayoutProbeInstance? reference)
    {
        var instance = this.GetById(id);
        if (instance == null)
            return;

        instance.AnimationCapabilityDump = this.recreateExperimentService.BuildAnimationCapabilityDump(instance, reference);
        this.LastStatus = "已刷新动画/复杂模型能力取证 dump。";
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

        if (!string.IsNullOrWhiteSpace(instance.AnimationGroupId))
        {
            this.RestoreAnimatedGroup(instance.AnimationGroupId, removeAfterRestore: true);
            return;
        }

        this.RestoreOriginal(instance, removeAfterRestore: true);
    }

    private void RestoreAnimatedGroup(string groupId, bool removeAfterRestore)
    {
        var children = this.instances
            .Where(item => string.Equals(item.AnimationGroupId, groupId, StringComparison.Ordinal))
            .ToList();
        if (children.Count == 0)
            return;

        foreach (var child in children)
            this.DisablePlayback(child, "恢复动画组前停止 VisibilityCycling。");

        var failures = new List<string>();
        foreach (var child in children)
        {
            try
            {
                this.RestoreOriginal(child, removeAfterRestore);
            }
            catch (Exception ex)
            {
                child.RestoreStatus = $"GroupRestoreFailed：{ex.Message}";
                child.LastError = child.RestoreStatus;
                failures.Add($"{child.Id}: {ex.Message}");
            }
        }

        this.LastStatus = $"已按 group 原子恢复 VisibilityCycling：group={groupId}; children={children.Count}; failures={failures.Count}; {string.Join(" | ", failures)}";
    }

    public void RestoreAll(
        bool removeAfterRestore = false,
        IEnumerable<LayoutProbeInstance>? bgParts = null,
        bool unsafeEnabled = true,
        bool fullLayoutConfirmed = true)
    {
        this.animatedPlaybackSystem.StopAllAndDetach(this.instances, "RestoreAll 前全局停止动画回放。");
        var preCleanupCount = this.CleanupDuplicateInstances(auto: true);
        this.RebuildOccupiedSlotRegistry();
        var restoreCount = this.occupiedSlots.Count;
        var modelRestoreFailures = new List<string>();

        foreach (var instance in this.occupiedSlots.Values.ToList())
        {
            try
            {
                var originalPath = FirstNonEmpty(instance.OriginalModelResourcePath, instance.OriginalResourcePath, instance.SourceResourcePath);
                var needsModelRestore = !string.IsNullOrWhiteSpace(originalPath)
                    && !string.Equals(FirstNonEmpty(instance.CurrentResourcePath, instance.AfterModelPath), originalPath, StringComparison.OrdinalIgnoreCase);

                if (needsModelRestore)
                {
                    var restoredModel = this.RestoreModelAndTransform(
                        instance.Id,
                        bgParts ?? Enumerable.Empty<LayoutProbeInstance>(),
                        unsafeEnabled,
                        fullLayoutConfirmed || instance.TransformMode == LocalLayoutTransformMode.VisualOnly);
                    if (!restoredModel)
                        modelRestoreFailures.Add($"{instance.Id}: {FirstNonEmpty(instance.LastModelOverrideError, instance.ApplyMdlError, instance.RestoreStatus)}");

                    if (removeAfterRestore && this.instances.Contains(instance))
                        this.instances.Remove(instance);
                    continue;
                }

                this.RestoreOriginal(instance, removeAfterRestore);
                instance.RestoreStatus = "Restored";
            }
            catch (Exception ex)
            {
                instance.RestoreStatus = $"RestoreFailed：{ex.Message}";
                instance.LastError = instance.RestoreStatus;
                modelRestoreFailures.Add($"{instance.Id}: {ex.Message}");
            }
        }

        if (removeAfterRestore)
            this.instances.RemoveAll(item => item.IsRestored || item.IsDuplicate);

        var postRestoreStaleCount = this.RemoveStaleRecords();
        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = removeAfterRestore
            ? $"已自动清理 {preCleanupCount} 条重复/残留记录，恢复原 mdl/transform 并移除 {restoreCount} 个 occupied slot，恢复后清理 {postRestoreStaleCount} 条列表记录，模型恢复失败 {modelRestoreFailures.Count} 个。{string.Join(" | ", modelRestoreFailures)}"
            : $"已自动清理 {preCleanupCount} 条重复/残留记录，恢复原 mdl/transform {restoreCount} 个 occupied slot，恢复后清理 {postRestoreStaleCount} 条列表记录，模型恢复失败 {modelRestoreFailures.Count} 个。{string.Join(" | ", modelRestoreFailures)}";
    }

    public void RestoreAllAndClear() => this.RestoreAll(removeAfterRestore: true, bgParts: [], unsafeEnabled: true, fullLayoutConfirmed: true);

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
        this.DisablePlayback(instance, "恢复/删除实例前停止动画回放。");
        instance.IsRestoring = true;
        instance.PinTransformEnabled = false;
        instance.TransformMonitorActive = false;
        instance.PinTransformReason = "实例恢复/删除时停止 PinTransform。";

        try
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

            this.transformService.RestoreTransform(instance);
            this.RestoreCarrierVisible(instance);

            instance.IsOccupied = false;
            instance.IsRestored = true;
            instance.HasCollisionMoved = false;
            if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var occupiedSlotAddress))
                this.occupiedSlots.Remove(occupiedSlotAddress);
            if (removeAfterRestore)
                this.instances.Remove(instance);

            this.LastStatus = this.transformService.LastResult;
        }
        finally
        {
            instance.IsRestoring = false;
        }
    }

    private void RestoreCarrierVisible(LocalLayoutObjectInstance instance)
    {
        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
            return;

        try
        {
            if (!TryGetGraphicsObjectAddress(pointer, out var graphicsAddress) || graphicsAddress == 0)
                return;

            instance.GraphicsObjectAddress = $"0x{graphicsAddress:X}";
            var bg = (SceneBgObject*)graphicsAddress;
            if (bg->ModelResourceHandle == null || bg->ModelResourceHandle->LoadState != 7)
                return;

            bg->IsVisible = instance.OriginalVisible;
            bg->UpdateRender();
            instance.Visible = instance.OriginalVisible;
        }
        catch (Exception ex)
        {
            instance.LastError = $"恢复 carrier visible 失败：{ex.Message}";
        }
    }

    private void WriteInstanceTransform(LocalLayoutObjectInstance instance, Vector3 position, Vector3 rotationEuler, Vector3 scale, string action)
    {
        instance.CurrentPosition = position;
        instance.CurrentRotationEuler = rotationEuler;
        instance.CurrentScale = scale;
        var applied = this.transformService.ApplyTransform(instance);
        if (applied)
        {
            this.ScheduleTransformMonitor(instance, position, rotationEuler, scale, action);
        }
        else
        {
            instance.TransformMonitorActive = false;
            instance.LastTransformWriteSkippedReason = this.transformService.LastResult;
            instance.TransformOverwriteDetails = "transform 写入未执行，原因见 transform disabled reason / skipped reason。";
        }

        this.LastStatus = $"{action}锛{this.transformService.LastResult}";
    }

    private void ScheduleTransformMonitor(LocalLayoutObjectInstance instance, Vector3 expectedPosition, Vector3 expectedRotationEuler, Vector3 expectedScale, string action)
    {
        instance.ControlledByRuntime = false;
        instance.TransformMonitorActive = true;
        instance.TransformMonitorFrame = 0;
        instance.TransformMonitorExpectedPosition = expectedPosition;
        instance.TransformMonitorExpectedRotationEuler = expectedRotationEuler;
        instance.TransformMonitorExpectedScale = expectedScale;
        instance.PinTargetPosition = expectedPosition;
        instance.PinTargetRotationEuler = expectedRotationEuler;
        instance.PinTargetScale = expectedScale;
        instance.PinFailed = false;
        instance.AppliedTransformPosition = FormatVector(expectedPosition);
        instance.LastTransformWriteSkippedReason = string.Empty;
        instance.TransformOverwriteDetails = $"已写入 {action}，等待 1/5/30 帧 readback。";
        instance.TransformReadbackAfter1Frame = string.Empty;
        instance.TransformReadbackAfter5Frames = string.Empty;
        instance.TransformReadbackAfter30Frames = string.Empty;
        instance.TransformReadbackImmediate = this.ReadCurrentTransformForMonitor(instance, out _) ?? instance.LastReadback;
    }

    private void UpdateTransformMonitor(LocalLayoutObjectInstance instance)
    {
        if (instance.IsInvalid || instance.IsRestored || instance.IsRenderInvalid)
        {
            instance.TransformMonitorActive = false;
            instance.TransformOverwriteDetails = "监控停止：实例已失效、已恢复或 render invalid。";
            return;
        }

        instance.TransformMonitorFrame++;
        var readback = this.ReadCurrentTransformForMonitor(instance, out var readbackPosition);
        if (string.IsNullOrWhiteSpace(readback))
        {
            instance.TransformMonitorActive = false;
            instance.ControlledByRuntime = true;
            instance.TransformOverwriteDetails = "监控停止：readback 失败，可能已被 runtime 或 layout controller 接管。";
            return;
        }

        if (instance.TransformMonitorFrame == 1)
            instance.TransformReadbackAfter1Frame = readback;
        if (instance.TransformMonitorFrame == 5)
            instance.TransformReadbackAfter5Frames = readback;
        if (instance.TransformMonitorFrame == 30)
            instance.TransformReadbackAfter30Frames = readback;

        var distanceFromExpected = Vector3.Distance(readbackPosition, instance.TransformMonitorExpectedPosition);
        if (instance.TransformMonitorFrame >= 1 && distanceFromExpected > 0.75f)
        {
            var sourceDistance = Vector3.Distance(readbackPosition, instance.OccupiedSlotOriginalPosition);
            instance.ControlledByRuntime = true;
            instance.TransformOverwriteDetails =
                $"readback 偏离目标 {distanceFromExpected:F2}m；到原 slot 位置距离 {sourceDistance:F2}m。疑似被 runtime/controller/layout transform 覆盖。";
            if (this.AutoPinDynamicTransforms && instance.PinTransformAutoEnabled && !instance.PinFailed)
            {
                instance.PinTransformEnabled = true;
                instance.PinTransformReason = "下一帧 readback 偏离目标，自动启用 PinTransform。";
                instance.LastPinWriteResult = "等待下一次 Framework.Update 写回 pin target。";
            }
        }

        if (instance.TransformMonitorFrame >= 1 && instance.ControlledByRuntime)
        {
            instance.TransformMonitorActive = false;
            return;
        }

        if (instance.TransformMonitorFrame >= 30)
        {
            instance.TransformMonitorActive = false;
            if (!instance.ControlledByRuntime)
                instance.TransformOverwriteDetails = "1/5/30 帧 readback 均未明显偏离目标，暂未发现 runtime 覆盖。";
        }
    }

    private void ProcessPinTransform(LocalLayoutObjectInstance instance)
    {
        if (!IsActiveInstance(instance, out _) || instance.IsRenderInvalid || instance.PendingVisualTransform)
            return;

        if (instance.PinFailed)
        {
            instance.PinTransformEnabled = false;
            return;
        }

        instance.CurrentPosition = instance.PinTargetPosition;
        instance.CurrentRotationEuler = instance.PinTargetRotationEuler;
        instance.CurrentScale = instance.PinTargetScale;
        var success = this.transformService.ApplyTransform(instance);
        if (success)
        {
            instance.PinWriteFailedCount = 0;
            instance.LastPinWriteResult = $"PinTransform 写回成功：{this.transformService.LastResult}";
            return;
        }

        instance.PinWriteFailedCount++;
        instance.LastPinWriteResult = $"PinTransform 写回失败 {instance.PinWriteFailedCount}/30：{this.transformService.LastResult}";
        if (instance.PinWriteFailedCount < 30)
            return;

        instance.PinFailed = true;
        instance.PinTransformEnabled = false;
        instance.PinTransformReason = "连续 30 次写入失败，已暂停 PinTransform。";
        instance.LastError = instance.PinTransformReason;
    }

    private string? ReadCurrentTransformForMonitor(LocalLayoutObjectInstance instance, out Vector3 position)
    {
        position = Vector3.Zero;
        try
        {
            if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
            {
                if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
                    return null;

                var snapshot = ReadSceneObjectTransform(graphicsAddress);
                if (snapshot == null)
                    return null;

                position = snapshot.Value.Position;
                return FormatSceneSnapshot(snapshot.Value);
            }

            if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
                return null;

            var layout = ReadLayoutTransform(pointer);
            if (layout == null)
                return null;

            position = layout.Value.Position;
            return FormatSnapshot(layout.Value);
        }
        catch
        {
            return null;
        }
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

    private static bool IsSupportedMdlPath(string path)
        => path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase);

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private readonly record struct SceneTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool IsTransformChanged);
}



