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
    private const string DynamicObjectBlockedMessage = "该物体疑似由地图 controller / SharedGroup / 动态材质驱动，当前版本不支持本地移动。为避免闪退已阻止创建。";

    private readonly LayoutObjectTransformService transformService = new();
    private readonly BgObjectModelOverrideService modelOverrideService = new();
    private readonly BgPartRecreateExperimentService recreateExperimentService = new();
    private readonly BgPartCollisionExperimentService collisionExperimentService = new();
    private readonly BgPartCollisionSourceResolver collisionSourceResolver = new();
    private readonly BgPartCarrierAllocator carrierAllocator = new();
    private readonly ProtectedBgPartRegistry? protectedBgParts;
    private readonly PreferredModifyBgPartRegistry? preferredModifyBgParts;
    private readonly AnimatedPlaybackSystem animatedPlaybackSystem = new();
    private readonly List<LocalLayoutObjectInstance> instances = [];
    private readonly Dictionary<ulong, LocalLayoutObjectInstance> occupiedSlots = [];
    private readonly HashSet<ulong> reservedSlots = [];
    private CreateManyJob? activeCreateManyJob;
    private AllocationPlan? lastAllocationPlan;

    public LocalLayoutObjectService(ProtectedBgPartRegistry? protectedBgParts = null, PreferredModifyBgPartRegistry? preferredModifyBgParts = null)
    {
        this.protectedBgParts = protectedBgParts;
        this.preferredModifyBgParts = preferredModifyBgParts;
    }

    public bool IsBusy { get; private set; }

    public bool IsCreateQueueActive => this.activeCreateManyJob != null;

    public int PendingCreateQueueLength => this.activeCreateManyJob?.PendingCount ?? 0;

    public int CreateQueueTotalCount => this.activeCreateManyJob?.TotalCount ?? 0;

    public int CreateQueueCurrentIndex => this.activeCreateManyJob?.CurrentIndex ?? 0;

    public int CreateQueueSuccessCount => this.activeCreateManyJob?.SuccessCount ?? 0;

    public int CreateQueueFailedCount => this.activeCreateManyJob?.FailedCount ?? 0;

    public int CreateQueueWaitingStabilizeCount => this.activeCreateManyJob?.WaitingStabilizeCount ?? 0;

    public string CreateQueueCurrentState => this.activeCreateManyJob?.CurrentStateText ?? string.Empty;

    public string CreateQueueCurrentSlot => this.activeCreateManyJob?.CurrentSlotAddress ?? string.Empty;

    public string CreateQueueLastError => this.activeCreateManyJob?.LastError ?? string.Empty;

    public string CreateQueueLastCreatedId => this.activeCreateManyJob?.LastCreatedId ?? string.Empty;

    public string LastAllocationPlanId => this.lastAllocationPlan?.Id ?? string.Empty;

    public int ReservedSlotCount => this.reservedSlots.Count;

    public bool AutoPinDynamicTransforms { get; set; }

    public string CarrierBlacklistPatternText { get; set; } = string.Empty;

    public string CarrierWhitelistPatternText { get; set; } = string.Empty;

    public IReadOnlyList<LocalLayoutObjectInstance> Instances => this.instances;

    public int ActiveOccupiedSlotCount => this.GetActiveInstances()
        .Select(item => item.SlotAddress)
        .Distinct()
        .Count();

    public int DuplicateSlotCount => this.GetActiveInstances()
        .GroupBy(item => item.SlotAddress)
        .Sum(group => Math.Max(0, group.Count() - 1));

    public string LastStatus { get; private set; } = "尚未创建本地场景物体。";

    public string LastCreateManyDryRunPreview { get; private set; } = string.Empty;

    public string LastRestorePlanPreview { get; private set; } = string.Empty;

    public string LastModelOverrideStatus => this.modelOverrideService.LastResult;

    public string LastRecreateExperimentStatus => this.recreateExperimentService.LastResult;

    public string LastCollisionExperimentStatus => this.collisionExperimentService.LastResult;

    public string LastAnimatedPlaybackStatus => this.animatedPlaybackSystem.LastStatus;

    public int AnimatedPlaybackCount => this.animatedPlaybackSystem.PlaybackCount;

    public int AnimatedGroupCount => this.animatedPlaybackSystem.GroupCount;

    public IReadOnlyList<LocalAnimatedGroupInstance> AnimatedGroups => this.animatedPlaybackSystem.Groups;

    public ProtectedBgPartRegistry? ProtectedBgParts => this.protectedBgParts;

    public PreferredModifyBgPartRegistry? PreferredModifyBgParts => this.preferredModifyBgParts;

    private string ProtectedVersion
        => this.protectedBgParts == null
            ? "none"
            : $"slots:{string.Join("|", this.protectedBgParts.ProtectedSlots.Select(item => $"{item.TerritoryType}:{item.SourceType}:{item.ResourcePath}:{item.OriginalPosition.X:F2},{item.OriginalPosition.Y:F2},{item.OriginalPosition.Z:F2}:{item.ChildIndex}").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))};paths:{string.Join("|", this.protectedBgParts.ProtectedResourcePaths.Select(item => $"{item.TerritoryType}:{item.AppliesToCurrentTerritoryOnly}:{item.ResourcePath}").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}";

    private string PreferredModifyVersion
        => this.preferredModifyBgParts == null
            ? "none"
            : $"slots:{string.Join("|", this.preferredModifyBgParts.PreferredSlots.Select(item => $"{item.TerritoryType}:{item.SourceType}:{item.ResourcePath}:{item.OriginalPosition.X:F2},{item.OriginalPosition.Y:F2},{item.OriginalPosition.Z:F2}:{item.ChildIndex}").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))};paths:{string.Join("|", this.preferredModifyBgParts.PreferredResourcePaths.Select(item => $"{item.TerritoryType}:{item.AppliesToCurrentTerritoryOnly}:{item.ResourcePath}").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}";

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

        this.ProcessCreateManyQueue();

        // v11.8: AnimatedPlayback/PinTransform 正式入口已暂停；Update 不再写动态 transform / visible。
    }

    public bool IsSlotOccupied(string slotAddress)
    {
        this.RebuildOccupiedSlotRegistry();
        return TryNormalizeSlotAddress(slotAddress, out var normalizedAddress)
            && (this.occupiedSlots.ContainsKey(normalizedAddress) || this.reservedSlots.Contains(normalizedAddress));
    }

    private string GetOccupiedRegistryVersion()
    {
        this.RebuildOccupiedSlotRegistry();
        return $"occupied:{string.Join(",", this.occupiedSlots.Keys.Order())};reserved:{string.Join(",", this.reservedSlots.Order())}";
    }

    public LocalLayoutObjectInstance? CreateFromCandidate(LayoutProbeInstance? candidate, Vector3 playerPosition, LocalLayoutTransformMode mode)
        => this.CreateFromCandidate(candidate, playerPosition, mode, template: null, applyTemplateModel: false, allowReservedSlot: false, CarrierAllocationPolicy.PreferredListThenAnyValid);

    public LocalLayoutObjectInstance? CreateFromTemplate(LayoutProbeInstance? template, LayoutProbeInstance? targetSlot, Vector3 position, LocalLayoutTransformMode mode, bool applyTemplateModel = false)
        => this.CreateFromCandidate(targetSlot, position, mode, template, applyTemplateModel, allowReservedSlot: false, CarrierAllocationPolicy.PreferredListThenAnyValid);

    public LocalLayoutObjectInstance? CreateCopyFromTemplate(
        LayoutProbeInstance? template,
        IEnumerable<LayoutProbeInstance> candidateSlots,
        Vector3 position,
        LocalLayoutTransformMode mode,
        CarrierAllocationPolicy carrierPolicy,
        bool unsafeEnabled,
        bool fullLayoutConfirmed,
        Vector3 defaultRotationEuler = default,
        Vector3? defaultScale = null)
    {
        if (template == null)
        {
            this.LastStatus = "请先选择模板 BgPart。";
            return null;
        }
        var templateWarning = this.GetCarrierWarningReason(template);

        var allocation = this.AllocateCarriers(template, candidateSlots, requestedCount: 1, carrierPolicy, position);
        var carrier = allocation.Selected.FirstOrDefault();

        if (carrier == null)
        {
            this.LastStatus = $"没有可用 carrier slot：sameModel={allocation.SameModelAvailable}; preferredModify={allocation.PreferredModifyAvailable}; anyValid={allocation.AnyValidAvailable}。请查看 CreateMany Dry Run Preview。";
            return null;
        }

        var instance = this.CreateFromCandidate(carrier, position, mode, template, applyTemplateModel: false, allowReservedSlot: false, carrierPolicy);
        if (instance == null)
            return null;

        var desiredScale = NormalizeDesiredScale(defaultScale ?? Vector3.One);
        instance.CurrentRotationEuler = defaultRotationEuler;
        instance.CurrentScale = desiredScale;

        if (!string.Equals(carrier.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase))
        {
            instance.CustomModelPath = template.ResourcePath;
            var applied = this.ApplyMdlPath(instance.Id, template.ResourcePath, candidateSlots, unsafeEnabled, fullLayoutConfirmed || mode == LocalLayoutTransformMode.VisualOnly);
            if (!applied && !instance.PendingVisualTransform && !string.Equals(instance.InstanceState, "PendingRecreateStabilize", StringComparison.OrdinalIgnoreCase))
                instance.ApplyMdlError = FirstNonEmpty(instance.ApplyMdlError, this.LastStatus);
        }
        else
        {
            this.WriteInstanceTransform(instance, position, instance.CurrentRotationEuler, instance.CurrentScale, "从模板创建复制体默认 transform");
        }

        this.LastStatus = $"已从模板创建复制体：template={template.Address}; carrier={carrier.Address}; policy={carrierPolicy}; targetMdl={template.ResourcePath}; scale={FormatVector(desiredScale)}; warning={FirstNonEmpty(templateWarning, carrier.CarrierWarningReason, "无")}; {this.LastStatus}";
        return instance;
    }

    private LocalLayoutObjectInstance? CreateFromCandidate(LayoutProbeInstance? candidate, Vector3 playerPosition, LocalLayoutTransformMode mode, LayoutProbeInstance? template, bool applyTemplateModel, bool allowReservedSlot, CarrierAllocationPolicy carrierPolicy)
    {
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，请等待完成后再创建。";
            return null;
        }

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

        var carrierBlockReason = template == null
            ? this.GetCarrierRejectReason(candidate, new HashSet<string>(StringComparer.OrdinalIgnoreCase), carrierPolicy)
            : this.GetCarrierRejectReasonForAllocatedTemplateCarrier(candidate, template, carrierPolicy);
        if (!string.IsNullOrWhiteSpace(carrierBlockReason))
        {
            this.LastStatus = $"当前 carrier 不可用：{carrierBlockReason}";
            candidate.CarrierRejectReason = carrierBlockReason;
            return null;
        }

        if (TryNormalizeSlotAddress(candidate.Address, out var candidateSlotAddress)
            && this.occupiedSlots.TryGetValue(candidateSlotAddress, out var owner))
        {
            this.LastStatus = $"该 BgPart slot 已被实例 {owner.Id} 占用。";
            return owner;
        }

        if (candidateSlotAddress != 0 && this.reservedSlots.Contains(candidateSlotAddress) && !allowReservedSlot)
        {
            this.LastStatus = $"该 BgPart slot 已被批量创建队列预留：0x{candidateSlotAddress:X}。";
            return null;
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

        var originalPrimaryPath = FirstNonEmpty(candidate.ResourcePath, ReadPrimaryPath(pointer));

        if (!TryGetGraphicsObjectAddress(pointer, out var graphicsAddress) || graphicsAddress == 0)
        {
            this.LastStatus = "读取 BgPart GraphicsObject 失败，未创建本地物件。";
            return null;
        }

        var graphicsObjectAddress = $"0x{graphicsAddress:X}";
        var originalVisualMatrix = ReadVisualMatrix(graphicsAddress, VisualMatrixOffset);
        var originalVisualTranslation = GetMatrixTranslation(originalVisualMatrix);
        var originalVisual = ReadSceneObjectTransform(graphicsAddress)
            ?? new SceneTransformSnapshot(originalLayout.Value.Position, originalLayout.Value.Rotation, originalLayout.Value.Scale, false);

        var instance = new LocalLayoutObjectInstance
        {
            Id = $"layout-object-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..45],
            TemplateSourceSlotAddress = template?.Address ?? candidate.Address,
            TemplateResourcePath = template?.ResourcePath ?? candidate.ResourcePath,
            TemplateTransform = template == null
                ? FormatSnapshot(originalLayout.Value)
                : $"position=({FormatVector(template.Position)}), rotation={template.Rotation}, scale=({FormatVector(template.Scale)})",
            SourceResourcePath = candidate.ResourcePath,
            SourceKind = candidate.SourceKind,
            SourceSharedGroupPath = candidate.SharedGroupPath,
            SourceParentAddress = candidate.ParentAddress,
            SourceParentKey = candidate.ParentKey,
            SourceChildIndex = candidate.ChildIndex,
            OriginalResourcePath = originalPrimaryPath,
            CurrentResourcePath = originalPrimaryPath,
            CustomModelPath = string.Empty,
            OriginalModelResourcePath = originalPrimaryPath,
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
            OriginalVisualTransform = FormatSceneSnapshot(originalVisual),
            OriginalVisualTranslation = originalVisualTranslation,
            OriginalVisualPosition = originalVisual.Position,
            OriginalVisualRotation = originalVisual.Rotation,
            OriginalVisualScale = originalVisual.Scale,
            OriginalVisualMatrix = originalVisualMatrix,
            CurrentVisualTranslation = mode == LocalLayoutTransformMode.VisualOnly ? playerPosition : originalVisual.Position,
            CurrentVisualMatrix = originalVisualMatrix,
            CurrentPosition = playerPosition,
            CurrentRotation = Quaternion.Identity,
            CurrentRotationEuler = Vector3.Zero,
            CurrentScale = NormalizeDesiredScale(Vector3.One),
            Visible = candidate.Visible,
            OriginalVisible = candidate.Visible,
            CarrierRejectReason = string.Empty,
            CarrierWarningReason = this.GetCarrierWarningReason(candidate),
            IsOccupied = true,
            CanRestore = true,
            InstanceState = "Ready",
            VisualOnlyVerified = mode == LocalLayoutTransformMode.VisualOnly,
            HasCollisionMoved = mode == LocalLayoutTransformMode.FullLayoutWithCollision,
            Notes = mode == LocalLayoutTransformMode.VisualOnly
                ? "VisualOnly：只写 Graphics.Scene.Object transform，不移动 layout/collision。"
                : "危险：FullLayoutWithCollision 会写 layout transform 并移动碰撞体。",
        };
        if (!string.IsNullOrWhiteSpace(instance.CarrierWarningReason))
            instance.Notes += $" warning={instance.CarrierWarningReason}。该对象会按静态外观尝试复制；动画/controller 效果不保证复制。";

        if (!this.collisionExperimentService.SaveSnapshot(instance))
        {
            this.LastStatus = $"保存原始 collision 快照失败，未创建实例：{this.collisionExperimentService.LastResult}";
            return null;
        }

        instance.OriginalSlotSnapshot = this.BuildOriginalSlotSnapshot(instance, originalPrimaryPath, originalLayout.Value, originalVisual, candidate);
        if (!this.ValidateOriginalSlotSnapshot(instance, out var snapshotError))
        {
            this.LastStatus = $"原始 slot 快照不完整，未创建实例：{snapshotError}";
            return null;
        }

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
        bool fullLayoutConfirmed = false,
        Vector3 defaultRotationEuler = default,
        Vector3? defaultScale = null,
        Vector3? playerPosition = null)
        => this.CreateManyFromTemplate(
            template,
            candidateSlots,
            count,
            basePosition,
            mode,
            spacing,
            allowDifferentResourcePathSlots,
            defaultCustomMdlPath,
            bgParts,
            unsafeEnabled,
            fullLayoutConfirmed,
            defaultRotationEuler,
            defaultScale,
            CarrierAllocationPolicy.PreferredListThenAnyValid,
            createAsManyAsPossible: true,
            playerPosition);

    public IReadOnlyList<LocalLayoutObjectInstance> CreateManyFromTemplate(
        LayoutProbeInstance? template,
        IEnumerable<LayoutProbeInstance> candidateSlots,
        int count,
        Vector3 basePosition,
        LocalLayoutTransformMode mode,
        Vector3 spacing,
        bool allowDifferentResourcePathSlots,
        string defaultCustomMdlPath,
        IEnumerable<LayoutProbeInstance>? bgParts,
        bool unsafeEnabled,
        bool fullLayoutConfirmed,
        Vector3 defaultRotationEuler,
        Vector3? defaultScale,
        CarrierAllocationPolicy carrierPolicy,
        bool createAsManyAsPossible,
        Vector3? playerPosition)
    {
        var created = new List<LocalLayoutObjectInstance>();
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，请等待完成后再创建复制体。";
            return created;
        }
        if (this.activeCreateManyJob != null)
        {
            this.LastStatus = "已有批量创建队列正在执行，请等待完成或先恢复全部。";
            return created;
        }

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

        var templateWarning = this.GetCarrierWarningReason(template);

        var plan = this.GetFreshAllocationPlanOrFail(template, candidateSlots, count, carrierPolicy, playerPosition, createAsManyAsPossible);
        if (plan == null)
            return created;

        var slots = plan.SelectedSlots.ToList();

        if (slots.Count < count)
        {
            if (slots.Count == 0 || !createAsManyAsPossible)
            {
            this.LastStatus = $"可用 carrier 不足：请求 {count}，可用 {plan.TotalAvailable}。sameModel={plan.SameModelAvailable}; preferredModify={plan.PreferredModifyAvailable}; anyValid={plan.AnyValidAvailable}。请查看 CreateMany Dry Run Preview。";
            return created;
        }

            this.LastStatus = $"可用 carrier 不足：请求 {count}，可用 {plan.TotalAvailable}。按“尽可能创建”继续创建 {slots.Count} 个。";
        }

        var targetMdlPath = hasCustomMdlPath ? customMdlPath : template.ResourcePath;
        var dynamicMdlReason = GetDynamicPathBlockReason(targetMdlPath);
        if (!string.IsNullOrWhiteSpace(dynamicMdlReason))
            templateWarning = string.IsNullOrWhiteSpace(templateWarning)
                ? $"DynamicSuspected: {dynamicMdlReason}"
                : $"{templateWarning}; DynamicSuspected: {dynamicMdlReason}";

        var pending = slots
            .Select((slot, index) => new PendingCreateItem(
                slot,
                basePosition + spacing * index,
                defaultRotationEuler,
                NormalizeDesiredScale(defaultScale ?? Vector3.One),
                string.Equals(slot.ResourcePath, targetMdlPath, StringComparison.OrdinalIgnoreCase) ? string.Empty : targetMdlPath))
            .ToList();

        foreach (var pendingSlot in pending)
        {
            if (TryNormalizeSlotAddress(pendingSlot.Slot.Address, out var reserved))
                this.reservedSlots.Add(reserved);
        }

        this.activeCreateManyJob = new CreateManyJob(
            template,
            pending.Select(item => new CreateManyJobItem(item)).ToList(),
            bgParts?.ToList() ?? candidateSlots.ToList(),
            mode,
            unsafeEnabled,
            fullLayoutConfirmed,
            templateWarning,
            count,
            carrierPolicy);
        this.LastStatus = $"已使用 allocation plan id={plan.Id} 创建批量复制队列：requested={count}; queued={pending.Count}; order=SameModel->PreferredModifyList->AnyValidBgPart; targetMdl={targetMdlPath}; 每帧处理 1 个实例。{templateWarning}";
        return created;
    }

    public string BuildCreateManyDryRunPreview(
        LayoutProbeInstance? template,
        IEnumerable<LayoutProbeInstance> candidateSlots,
        int requestedCount,
        bool allowDifferentResourcePathSlots,
        string defaultCustomMdlPath,
        CarrierAllocationPolicy carrierPolicy,
        Vector3? playerPosition)
    {
        this.RebuildOccupiedSlotRegistry();
        var slots = candidateSlots.ToList();
        var bgParts = slots.Where(slot => string.Equals(slot.Type, "BgPart", StringComparison.Ordinal)).ToList();
        var customMdlPath = (defaultCustomMdlPath ?? string.Empty).Trim();
        var hasCustomMdlPath = !string.IsNullOrWhiteSpace(customMdlPath);
        var allocation = this.AllocateCarriers(template, bgParts, requestedCount, carrierPolicy, playerPosition);
        var selected = allocation.Selected;
        var plan = this.CreateAllocationPlan(template, bgParts, requestedCount, carrierPolicy, playerPosition, allocation);
        this.lastAllocationPlan = plan;

        var lines = new List<string>
        {
            $"allocationPlanId={plan.Id}",
            $"playerPosition={(playerPosition.HasValue ? FormatVector(playerPosition.Value) : "unknown")}",
            $"requestedCount={requestedCount}",
            "allocationMode=PreferredListThenAnyValid",
            $"templateSlotAddress={template?.Address ?? "未选择"}",
            $"templateResourcePath={template?.ResourcePath ?? "未选择"}",
            $"excludeTemplateSlot={(template != null ? "是" : "否")}",
            $"candidateCount={bgParts.Count}",
            $"total bgpart slots={bgParts.Count}",
            $"occupied slots={allocation.OccupiedCount}",
            $"reserved slots={allocation.ReservedCount}",
            $"free slots={allocation.FreeCount}",
            $"same model available slot count={allocation.SameModelAvailable}",
            $"preferred modify available slot count={allocation.PreferredModifyAvailable}",
            $"any valid available slot count={allocation.AnyValidAvailable}",
            $"protected rejected count={(allocation.Rejected.TryGetValue("Protected", out var protectedRejectedCount) ? protectedRejectedCount : 0)}",
            $"available under selected policy={allocation.TotalAvailable}",
            "carrierAllocationOrder=SameModel -> PreferredModifyList -> AnyValidBgPart",
            $"allocatedCount={selected.Count}",
            $"orderValidation={allocation.OrderValidationMessage}",
            "rejected slots：",
        };

        if (allocation.Rejected.Count == 0)
            lines.Add("  无");
        else
            lines.AddRange(allocation.Rejected.OrderBy(pair => pair.Key).Select(pair => $"  {pair.Key}: {pair.Value}"));

        lines.Add("accepted with warning：");
        if (allocation.AcceptedWarnings.Count == 0)
            lines.Add("  无");
        else
            lines.AddRange(allocation.AcceptedWarnings.OrderBy(pair => pair.Key).Select(pair => $"  {pair.Key}: {pair.Value}"));

        lines.Add("allocated carrier list：");
        if (selected.Count == 0)
        {
            lines.Add("  无可用 carrier。");
        }
        else
        {
            for (var index = 0; index < selected.Count; index++)
            {
                var slot = selected[index];
                lines.Add($"  [{index}] slot={slot.Address}; path={slot.ResourcePath}; position={FormatVector(slot.Position)}; distance={BgPartCarrierAllocator.GetDistance(slot, playerPosition):F1}; allocationStage={FirstNonEmpty(slot.CarrierAllocationStage, "Unknown")}; warningReason={FirstNonEmpty(slot.CarrierWarningReason, "无")}; protected=false");
            }
        }

        lines.Add("top 10 farthest accepted slots：");
        lines.AddRange(allocation.AcceptedTop10.Select((slot, index) => $"  [{index}] slot={slot.Address}; path={slot.ResourcePath}; position={FormatVector(slot.Position)}; distance={BgPartCarrierAllocator.GetDistance(slot, playerPosition):F1}; allocationStage={FirstNonEmpty(slot.CarrierAllocationStage, "Unknown")}; warningReason={FirstNonEmpty(slot.CarrierWarningReason, "无")}"));
        lines.Add("top 10 farthest rejected slots：");
        lines.AddRange(allocation.RejectedTop10.Select((slot, index) => $"  [{index}] slot={slot.Address}; path={slot.ResourcePath}; position={FormatVector(slot.Position)}; distance={BgPartCarrierAllocator.GetDistance(slot, playerPosition):F1}; rejectReason={slot.CarrierRejectReason}"));

        this.LastCreateManyDryRunPreview = string.Join(Environment.NewLine, lines);
        return this.LastCreateManyDryRunPreview;
    }

    private void ProcessCreateManyQueue()
    {
        var job = this.activeCreateManyJob;
        if (job == null)
            return;

        if (this.IsBusy)
            return;

        var item = job.Items.FirstOrDefault(candidate => candidate.State is not CreateJobState.Ready and not CreateJobState.Failed);
        if (item == null)
        {
            this.LastStatus = this.FormatCreateQueueStatus(job) + "；队列已完成。";
            this.activeCreateManyJob = null;
            this.reservedSlots.Clear();
            this.RebuildOccupiedSlotRegistry();
            return;
        }

        job.CurrentIndex = job.Items.IndexOf(item) + 1;
        item.FrameInState++;
        try
        {
            switch (item.State)
            {
                case CreateJobState.Pending:
                    this.TransitionCreateJob(item, CreateJobState.AllocatingSlot, "等待分配 slot。");
                    this.LastStatus = this.FormatCreateQueueStatus(job);
                    return;

                case CreateJobState.AllocatingSlot:
                    if (!TryNormalizeSlotAddress(item.Slot.Address, out var slotAddress) || slotAddress == 0)
                    {
                        this.FailCreateJob(job, item, $"slot 地址无效：{item.Slot.Address}");
                        return;
                    }

                    this.RebuildOccupiedSlotRegistry();
                    if (this.occupiedSlots.TryGetValue(slotAddress, out var owner))
                    {
                        this.FailCreateJob(job, item, $"slot 已被实例 {owner.Id} 占用：0x{slotAddress:X}");
                        return;
                    }

                    item.SlotAddress = slotAddress;
                    this.TransitionCreateJob(item, CreateJobState.Recreating, $"slot 已预留：0x{slotAddress:X}");
                    this.LastStatus = this.FormatCreateQueueStatus(job);
                    return;

                case CreateJobState.Recreating:
                    this.CreateOneQueuedInstance(job, item);
                    return;

                case CreateJobState.WaitingStabilize:
                    this.AdvanceQueuedInstanceStabilize(job, item);
                    return;

                case CreateJobState.ApplyingTransform:
                    this.ApplyQueuedInstanceTransform(job, item);
                    return;
            }
        }
        catch (Exception ex)
        {
            this.FailCreateJob(job, item, $"slot={item.Slot.Address}: {ex.Message}");
        }
    }

    private string FormatCreateQueueStatus(CreateManyJob job)
        => $"批量创建队列：current={job.CurrentIndex}/{job.TotalCount}; pending={job.PendingCount}; waiting={job.WaitingStabilizeCount}; success={job.SuccessCount}; failed={job.FailedCount}; state={job.CurrentStateText}; slot={job.CurrentSlotAddress}; lastCreated={job.LastCreatedId}; lastError={job.LastError}";

    private void CreateOneQueuedInstance(CreateManyJob job, CreateManyJobItem item)
    {
        if (TryNormalizeSlotAddress(item.Slot.Address, out var reservedSlot))
            this.reservedSlots.Remove(reservedSlot);

        var instance = this.CreateFromCandidate(item.Slot, item.Position, job.Mode, job.Template, applyTemplateModel: false, allowReservedSlot: true, job.Policy);
        if (instance == null)
        {
            this.FailCreateJob(job, item, $"slot={item.Slot.Address}: {this.LastStatus}");
            return;
        }

        item.InstanceId = instance.Id;
        item.SlotAddress = TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var occupiedSlot)
            ? occupiedSlot
            : item.SlotAddress;
        instance.CurrentRotationEuler = item.RotationEuler;
        instance.CurrentScale = item.Scale;

        if (!string.IsNullOrWhiteSpace(item.CustomMdlPath))
        {
            instance.CustomModelPath = item.CustomMdlPath;
            var applied = this.ApplyMdlPath(instance.Id, item.CustomMdlPath, job.BgParts, job.UnsafeEnabled, job.FullLayoutConfirmed || job.Mode == LocalLayoutTransformMode.VisualOnly);
            if (!applied && !instance.PendingVisualTransform && !string.Equals(instance.InstanceState, "PendingRecreateStabilize", StringComparison.OrdinalIgnoreCase))
            {
                instance.ApplyMdlStatus = "Failed";
                instance.ApplyMdlError = FirstNonEmpty(instance.ApplyMdlError, instance.LastModelOverrideError, this.LastStatus);
                this.FailCreateJob(job, item, $"{instance.Id}: {instance.ApplyMdlError}", instance);
                return;
            }
        }

        if (instance.PendingVisualTransform || string.Equals(instance.InstanceState, "PendingRecreateStabilize", StringComparison.OrdinalIgnoreCase))
        {
            this.TransitionCreateJob(item, CreateJobState.WaitingStabilize, $"等待实例 recreate 稳定：{instance.Id}");
            this.LastStatus = this.FormatCreateQueueStatus(job);
            return;
        }

        this.TransitionCreateJob(item, CreateJobState.ApplyingTransform, $"实例已创建：{instance.Id}");
        this.LastStatus = this.FormatCreateQueueStatus(job);
    }

    private void AdvanceQueuedInstanceStabilize(CreateManyJob job, CreateManyJobItem item)
    {
        var instance = this.instances.FirstOrDefault(candidate => string.Equals(candidate.Id, item.InstanceId, StringComparison.Ordinal));
        if (instance == null)
        {
            this.FailCreateJob(job, item, $"等待稳定失败：实例已不存在 {item.InstanceId}");
            return;
        }

        if (IsFailedState(instance) || instance.IsRenderInvalid)
        {
            this.FailCreateJob(job, item, $"{instance.Id}: {FirstNonEmpty(instance.ApplyMdlError, instance.LastError, instance.TransformWriteDisabledReason, instance.PendingVisualTransformResult)}", instance);
            return;
        }

        if (instance.PendingVisualTransform || string.Equals(instance.InstanceState, "PendingRecreateStabilize", StringComparison.OrdinalIgnoreCase))
        {
            if (item.FrameInState >= item.TimeoutFrames)
            {
                instance.ApplyMdlStatus = "Failed";
                instance.ApplyMdlError = $"RecreateStabilizeTimeout: {instance.PendingVisualTransformResult}";
                this.TryRestoreFailedQueuedInstance(instance, job.UnsafeEnabled);
                this.FailCreateJob(job, item, $"{instance.Id}: {instance.ApplyMdlError}", instance);
                return;
            }

            this.LastStatus = $"批量创建等待稳定：{job.CurrentIndex}/{job.TotalCount}; instance={instance.Id}; frame={item.FrameInState}/{item.TimeoutFrames}; {instance.PendingVisualTransformResult}";
            return;
        }

        this.TransitionCreateJob(item, CreateJobState.ApplyingTransform, $"实例已稳定：{instance.Id}");
        this.LastStatus = this.FormatCreateQueueStatus(job);
    }

    private void ApplyQueuedInstanceTransform(CreateManyJob job, CreateManyJobItem item)
    {
        var instance = this.instances.FirstOrDefault(candidate => string.Equals(candidate.Id, item.InstanceId, StringComparison.Ordinal));
        if (instance == null)
        {
            this.FailCreateJob(job, item, $"应用 transform 失败：实例已不存在 {item.InstanceId}");
            return;
        }

        var ok = this.WriteInstanceTransform(instance, item.Position, item.RotationEuler, item.Scale, "批量复制最终 transform");
        if (!ok)
        {
            this.FailCreateJob(job, item, $"{instance.Id}: {FirstNonEmpty(instance.LastError, instance.TransformWriteDisabledReason, "transform 写入失败")}", instance);
            return;
        }

        item.State = CreateJobState.Ready;
        item.LastMessage = $"Ready：{instance.Id}";
        item.FrameInState = 0;
        job.LastCreatedId = instance.Id;
        this.LastStatus = this.FormatCreateQueueStatus(job);
    }

    private void FailCreateJob(CreateManyJob job, CreateManyJobItem item, string reason, LocalLayoutObjectInstance? instance = null)
    {
        item.State = CreateJobState.Failed;
        item.LastError = reason;
        item.FrameInState = 0;
        job.LastError = reason;
        if (TryNormalizeSlotAddress(item.Slot.Address, out var slotAddress))
            this.reservedSlots.Remove(slotAddress);

        if (instance != null)
        {
            instance.InstanceState = "Failed";
            instance.LastError = reason;
            instance.ApplyMdlStatus = string.Equals(instance.ApplyMdlStatus, "Ready", StringComparison.OrdinalIgnoreCase)
                ? "Failed"
                : instance.ApplyMdlStatus;
        }

        this.LastStatus = this.FormatCreateQueueStatus(job);
    }

    private void TransitionCreateJob(CreateManyJobItem item, CreateJobState state, string message)
    {
        item.State = state;
        item.LastMessage = message;
        item.FrameInState = 0;
    }

    private void TryRestoreFailedQueuedInstance(LocalLayoutObjectInstance instance, bool unsafeEnabled)
    {
        try
        {
            this.RestoreOriginalSlotSnapshot(instance, unsafeEnabled, fullLayoutConfirmed: true, removeAfterRestore: true);
        }
        catch (Exception ex)
        {
            instance.LastError = $"批量创建失败实例自动恢复异常：{ex.Message}";
        }
    }

    public IReadOnlyList<LocalLayoutObjectInstance> CreateAnimatedFromSource(
        LayoutProbeInstance? source,
        IEnumerable<LayoutProbeInstance> allBgParts,
        Vector3 basePosition,
        LocalLayoutTransformMode mode,
        bool unsafeEnabled,
        bool fullLayoutConfirmed)
    {
        this.StopAllPlayback("v9.8 静态稳定版已暂停 AnimatedPlayback；动态实例创建入口禁用。");
        this.LastStatus = $"{DynamicObjectBlockedMessage} 请使用静态 VisualOnly / FullLayout 创建本地实例。";
        return [];
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
            .Where(slot => string.IsNullOrWhiteSpace(this.GetCarrierRejectReason(slot, excluded, CarrierAllocationPolicy.SafeOnly)))
            .OrderByDescending(slot => !slot.Visible)
            .ThenBy(slot => IsSmallDecorativeCarrier(slot) ? 0 : 1)
            .ThenByDescending(slot => slot.DistanceToPlayer);
    }

    private BgPartCarrierAllocationResult AllocateCarriers(
        LayoutProbeInstance? template,
        IEnumerable<LayoutProbeInstance> candidateSlots,
        int requestedCount,
        CarrierAllocationPolicy policy,
        Vector3? playerPosition)
    {
        this.RebuildOccupiedSlotRegistry();
        return this.carrierAllocator.AllocateCarriers(
            template,
            candidateSlots,
            requestedCount,
            policy,
            playerPosition,
            this.GetBasicCarrierRejectReason,
            this.GetPreferredModifyReason,
            this.GetCarrierWarningReason,
            address => TryNormalizeSlotAddress(address, out var normalized) && this.occupiedSlots.ContainsKey(normalized),
            address => TryNormalizeSlotAddress(address, out var normalized) && this.reservedSlots.Contains(normalized));
    }

    private AllocationPlan? GetFreshAllocationPlanOrFail(
        LayoutProbeInstance template,
        IEnumerable<LayoutProbeInstance> candidateSlots,
        int requestedCount,
        CarrierAllocationPolicy policy,
        Vector3? playerPosition,
        bool createAsManyAsPossible)
    {
        var slots = candidateSlots.ToList();
        var currentPlan = this.lastAllocationPlan;
        if (currentPlan == null
            || currentPlan.IsStale(template, requestedCount, policy, playerPosition, this.ProtectedVersion, this.PreferredModifyVersion, this.GetOccupiedRegistryVersion(), slots)
            || currentPlan.SelectedSlots.Count == 0)
        {
            this.LastStatus = "AllocationPlan 已过期或不存在，请先点击 CreateMany Dry Run Preview。";
            return null;
        }

        if (!currentPlan.IsOrderValid)
        {
            this.LastStatus = currentPlan.OrderValidationMessage;
            return null;
        }

        if (currentPlan.SelectedSlots.Count < requestedCount && !createAsManyAsPossible)
        {
            this.LastStatus = $"请求 {requestedCount}，AllocationPlan 只有 {currentPlan.SelectedSlots.Count} 个 carrier。请重新 Dry Run 或勾选尽可能创建。";
            return null;
        }

        return currentPlan;
    }

    private AllocationPlan CreateAllocationPlan(
        LayoutProbeInstance? template,
        IReadOnlyList<LayoutProbeInstance> candidateSlots,
        int requestedCount,
        CarrierAllocationPolicy policy,
        Vector3? playerPosition,
        BgPartCarrierAllocationResult allocation)
        => new(
            $"carrier-plan-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..45],
            template?.Address ?? string.Empty,
            template?.ResourcePath ?? string.Empty,
            requestedCount,
            policy,
            playerPosition,
            this.ProtectedVersion,
            this.PreferredModifyVersion,
            this.GetOccupiedRegistryVersion(),
            candidateSlots.Select(slot => slot.Address).OrderBy(address => address, StringComparer.OrdinalIgnoreCase).ToList(),
            allocation.Selected.ToList(),
            allocation.SameModelAvailable,
            allocation.PreferredModifyAvailable,
            allocation.AnyValidAvailable,
            allocation.TotalAvailable,
            allocation.IsOrderValid,
            allocation.OrderValidationMessage);

    public string GetCarrierRejectReason(LayoutProbeInstance? slot)
        => this.GetCarrierRejectReason(slot, CarrierAllocationPolicy.PreferredListThenAnyValid);

    public string GetCarrierRejectReason(LayoutProbeInstance? slot, CarrierAllocationPolicy policy)
        => this.GetCarrierRejectReason(slot, new HashSet<string>(StringComparer.OrdinalIgnoreCase), policy);

    public string GetCarrierWarningReason(LayoutProbeInstance? slot)
    {
        if (slot == null)
            return string.Empty;

        var warnings = new List<string>();
        var structuralReason = GetStructuralCarrierRejectReason(slot);
        if (!string.IsNullOrWhiteSpace(structuralReason))
            warnings.Add(structuralReason);
        if (string.Equals(slot.SourceKind, "SharedGroup", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(slot.ParentAddress)
            || !string.IsNullOrWhiteSpace(slot.SharedGroupPath)
            || slot.ChildIndex >= 0)
        {
            warnings.Add("SharedGroupChild");
        }

        var dynamicReason = GetDynamicPathBlockReason(slot.ResourcePath);
        if (!string.IsNullOrWhiteSpace(dynamicReason))
            warnings.Add($"DynamicSuspected: {dynamicReason}");

        return string.Join("; ", warnings);
    }

    public string GetPreferredModifyReason(LayoutProbeInstance? slot)
    {
        if (slot == null || this.preferredModifyBgParts == null)
            return string.Empty;

        return this.preferredModifyBgParts.IsPreferred(slot, out var reason) ? reason : string.Empty;
    }

    private string GetCarrierRejectReasonForAllocatedTemplateCarrier(LayoutProbeInstance? slot, LayoutProbeInstance template, CarrierAllocationPolicy policy)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { template.Address };
        var basicReject = this.GetBasicCarrierRejectReason(slot, excluded);
        if (!string.IsNullOrWhiteSpace(basicReject))
            return basicReject;

        if (slot != null && string.Equals(slot.ResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase))
            return this.GetFallbackCarrierRejectReason(slot, policy);

        return slot == null ? "空 slot" : this.GetFallbackCarrierRejectReason(slot, policy);
    }

    private bool IsInstanceSlotProtected(LocalLayoutObjectInstance instance, out string reason)
    {
        reason = string.Empty;
        if (this.protectedBgParts == null)
            return false;

        var snapshot = instance.OriginalSlotSnapshot;
        var probe = new LayoutProbeInstance
        {
            Type = "BgPart",
            Address = instance.OccupiedSlotAddress,
            ResourcePath = snapshot?.OriginalResourcePath ?? instance.OriginalResourcePath,
            Position = snapshot?.OriginalLayoutPosition ?? instance.OriginalLayoutPosition,
            Rotation = instance.OriginalLayoutTransform,
            Scale = snapshot?.OriginalLayoutScale ?? instance.OriginalLayoutScale,
            SourceKind = FirstNonEmpty(instance.SourceKind, snapshot?.OriginalSourceType ?? string.Empty, "LoadedLayout"),
            SharedGroupPath = instance.SourceSharedGroupPath,
            ParentAddress = instance.SourceParentAddress,
            ParentKey = instance.SourceParentKey,
            ChildIndex = instance.SourceChildIndex,
            Key = snapshot?.SourceLabel ?? instance.OccupiedSlotAddress,
        };

        return this.protectedBgParts.IsProtected(probe, out reason);
    }

    private string GetCarrierRejectReason(LayoutProbeInstance? slot, ISet<string> excludedAddresses, CarrierAllocationPolicy policy)
    {
        var basicReject = this.GetBasicCarrierRejectReason(slot, excludedAddresses);
        if (!string.IsNullOrWhiteSpace(basicReject))
        {
            if (slot != null)
                slot.CarrierRejectReason = basicReject;
            return basicReject;
        }

        var fallbackReject = this.GetFallbackCarrierRejectReason(slot!, policy);
        slot!.CarrierRejectReason = fallbackReject;
        return fallbackReject;
    }

    private string GetBasicCarrierRejectReason(LayoutProbeInstance? slot, ISet<string> excludedAddresses)
    {
        if (slot == null)
            return "空 slot";
        if (!string.Equals(slot.Type, "BgPart", StringComparison.Ordinal))
            return "不是 BgPart";
        if (this.protectedBgParts != null && this.protectedBgParts.IsProtected(slot, out var protectedReason))
            return protectedReason;
        if (excludedAddresses.Contains(slot.Address))
            return "TemplateSlot";
        if (TryNormalizeSlotAddress(slot.Address, out var normalizedAddress) && this.occupiedSlots.ContainsKey(normalizedAddress))
            return "AlreadyOccupied";
        if (TryNormalizeSlotAddress(slot.Address, out normalizedAddress) && this.reservedSlots.Contains(normalizedAddress))
            return "Reserved";
        if (!IsSupportedMdlPath(slot.ResourcePath))
            return "非 bg/bgcommon mdl";
        if (!this.IsCarrierGraphicsUsable(slot, out var graphicsReason))
            return graphicsReason;
        return string.Empty;
    }

    private string GetFallbackCarrierRejectReason(LayoutProbeInstance slot, CarrierAllocationPolicy policy)
    {
        if (policy == CarrierAllocationPolicy.PreferredListThenAnyValid)
            return string.Empty;
        if (MatchesPatternList(slot.ResourcePath, this.CarrierWhitelistPatternText))
            return string.Empty;
        if (MatchesPatternList(slot.ResourcePath, this.CarrierBlacklistPatternText))
            return "UserBlacklist";
        if (policy == CarrierAllocationPolicy.AnyValidBgPart)
            return string.Empty;

        var structuralReason = GetStructuralCarrierRejectReason(slot);
        if (policy == CarrierAllocationPolicy.SafeOnly)
        {
            if (!string.IsNullOrWhiteSpace(structuralReason))
                return structuralReason;
            return IsSmallDecorativeCarrier(slot) ? string.Empty : "TooLarge";
        }

        return structuralReason;
    }

    private static string GetStructuralCarrierRejectReason(LayoutProbeInstance slot)
    {
        var path = slot.ResourcePath.ToLowerInvariant();
        if (ContainsAny(path, "floor", "flo", "flr", "yuka"))
            return "FloorLike";
        if (ContainsAny(path, "wall", "wal", "kabe"))
            return "WallLike";
        if (ContainsAny(path, "ceil", "ceiling", "tenjo", "roof", "ground", "gnd", "terrain", "land", "road", "base", "bgbase", "map", "sea", "water", "sky", "cliff", "rock_large", "foundation", "field_base"))
            return "TerrainLike";
        if (IsArchitectureLikeCarrier(path))
            return "StructureLike";
        if (IsTooLargeCarrier(slot))
            return "TooLarge";
        if (slot.DistanceToPlayer < 8f && (IsTooLargeCarrier(slot) || ContainsAny(path, "stair", "hashigo", "pillar", "column")))
            return "TooCloseImportantGeometry";
        return string.Empty;
    }

    private bool IsCarrierGraphicsUsable(LayoutProbeInstance slot, out string reason)
    {
        reason = string.Empty;
        if (!TryGetPointer(slot.Address, out var pointer) || pointer == null)
        {
            reason = "InvalidGraphicsObject: slot pointer unreadable";
            return false;
        }

        if (!TryGetGraphicsObjectAddress(pointer, out var graphicsAddress) || graphicsAddress == 0)
        {
            reason = "InvalidGraphicsObject: GraphicsObject=null";
            return false;
        }

        try
        {
            var bg = (SceneBgObject*)graphicsAddress;
            if (bg->ModelResourceHandle == null)
            {
                reason = "InvalidGraphicsObject: ModelResourceHandle=null";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"InvalidGraphicsObject: {ex.Message}";
            return false;
        }

        return true;
    }

    private static bool IsTerrainLikeCarrier(string resourcePath)
        => !string.IsNullOrWhiteSpace(GetStructuralCarrierRejectReason(new LayoutProbeInstance { ResourcePath = resourcePath, Scale = Vector3.One }));

    private static bool IsArchitectureLikeCarrier(string path)
    {
        var fileName = path.Split('/', '\\').LastOrDefault() ?? path;
        if (ContainsAny(path, "building", "bld", "house_base", "room", "room_base", "pillar_large", "arch_large"))
            return true;
        if (path.Contains("/hou/", StringComparison.Ordinal) && path.Contains("/bgparts/", StringComparison.Ordinal))
        {
            if (ContainsAny(fileName, "wall", "floor", "roof", "ceil", "base"))
                return true;
            if (fileName.StartsWith("com_b", StringComparison.Ordinal) && fileName.Contains("_m", StringComparison.Ordinal))
                return true;
            if (fileName.Contains("_m0", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        var parts = text.Split(['/', '\\', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token.Length <= 3)
            {
                if (parts.Any(part => part.Equals(token, StringComparison.Ordinal)
                    || (part.StartsWith(token, StringComparison.Ordinal) && part[token.Length..].All(char.IsDigit))))
                    return true;
                continue;
            }

            if (text.Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool MatchesPatternList(string resourcePath, string patternText)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || string.IsNullOrWhiteSpace(patternText))
            return false;

        var path = resourcePath.Replace('\\', '/');
        var patterns = patternText
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern));
        foreach (var pattern in patterns)
        {
            var normalized = pattern.Replace('\\', '/');
            if (path.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能应用 transform。";
            return;
        }

        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.WriteInstanceTransform(instance, position, rotationEuler, scale, instance.TransformMode == LocalLayoutTransformMode.VisualOnly ? "应用 VisualOnly transform" : "应用 FullLayout transform");
    }

    public void RestoreTransformOnly(string id)
    {
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能恢复 transform。";
            return;
        }

        var instance = this.GetById(id);
        if (instance == null)
            return;

        instance.LastOperation = "RestoreTransformOnly";
        if (!this.TryGetOriginalSlotSnapshot(instance, out var snapshot, out var snapshotError))
        {
            instance.LastError = $"仅恢复 transform 失败：{snapshotError}";
            this.LastStatus = instance.LastError;
            return;
        }

        if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
        {
            this.WriteInstanceTransform(
                instance,
                snapshot.OriginalGraphicsPosition,
                Vector3.Zero,
                snapshot.OriginalGraphicsScale,
                "仅恢复 VisualOnly transform");
            return;
        }

        this.WriteInstanceTransform(
            instance,
            snapshot.OriginalLayoutPosition,
            Vector3.Zero,
            snapshot.OriginalLayoutScale,
            "仅恢复 FullLayout transform");
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
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能应用 mdl path。";
            return false;
        }

        var instance = this.GetById(id);
        if (instance == null)
            return false;

        if (!instance.IsRestoring && this.IsInstanceSlotProtected(instance, out var protectedReason))
        {
            instance.ApplyMdlStatus = "Failed";
            instance.ApplyMdlError = $"该 BgPart 已在保护列表，禁止修改 mdl：{protectedReason}";
            instance.LastModelOverrideError = instance.ApplyMdlError;
            this.LastStatus = instance.ApplyMdlError;
            return false;
        }

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
        instance.InstanceState = "ApplyingModel";
        instance.LastOperation = $"ApplyMdlPath:{modelPath}";
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
                "PendingRecreateStabilize" => "VisualOnly 已 recreate，正在等待 GraphicsObject / ModelResourceHandle 稳定。",
                _ when instance.PendingVisualTransform => "VisualOnly 已 recreate，等待数帧后安全写入 transform。",
                _ => $"VisualOnly 已应用 mdl path：{modelPath}；collision moved=false。",
            };
            instance.ApplyMdlStatus = string.IsNullOrWhiteSpace(instance.ModelApplyStatus)
                ? instance.PendingVisualTransform ? "PendingVisualTransform" : "Applied"
                : instance.ModelApplyStatus;
            instance.ApplyMdlError = instance.IsRenderInvalid ? instance.TransformWriteDisabledReason : string.Empty;

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
        instance.InstanceState = "Ready";
        instance.LastModelOverrideResult = instance.CollisionApplied
            ? $"FullLayoutWithCollision 已应用 mdl path 和 target collision：{modelPath}；source={instance.CollisionSourceBgPartAddress}；type={instance.CollisionSourceColliderType}。"
            : $"FullLayoutWithCollision 已应用 mdl path：{modelPath}；{instance.CollisionError}";
        instance.ApplyMdlStatus = instance.CollisionApplied ? "Applied" : "AppliedWithoutCollisionSource";
        instance.ApplyMdlError = instance.CollisionApplied ? string.Empty : instance.CollisionError;
        this.LastStatus = instance.LastModelOverrideResult;
        return true;
    }

    public bool ChangeCollisionMode(
        string id,
        LocalLayoutTransformMode mode,
        IEnumerable<LayoutProbeInstance> bgParts,
        bool unsafeEnabled,
        bool fullLayoutConfirmed)
    {
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能切换 collision 模式。";
            return false;
        }

        var instance = this.GetById(id);
        if (instance == null)
            return false;

        if (!unsafeEnabled)
            return this.FailCollisionModeChange(instance, "UnsafeMode=false。");
        if (instance.IsInvalid || instance.IsRestored || instance.IsDuplicate)
            return this.FailCollisionModeChange(instance, "实例已失效、已恢复或是重复记录。");
        if (instance.IsRenderInvalid)
            return this.FailCollisionModeChange(instance, "实例 render 已失效，不能安全切换 collision 模式。");
        if (mode == LocalLayoutTransformMode.FullLayoutWithCollision && !fullLayoutConfirmed)
            return this.FailCollisionModeChange(instance, "FullLayoutWithCollision 需要二次确认。");

        if (instance.TransformMode == mode)
        {
            this.WriteInstanceTransform(instance, instance.CurrentPosition, instance.CurrentRotationEuler, instance.CurrentScale, "刷新当前 collision 模式 transform");
            this.LastStatus = $"实例已是 {mode}，已按当前模式重新应用 transform。";
            return true;
        }

        this.DisablePlayback(instance, "切换 collision 模式前停止动画/监控。");

        if (mode == LocalLayoutTransformMode.VisualOnly)
        {
            var previousMode = instance.TransformMode;
            if (previousMode == LocalLayoutTransformMode.FullLayoutWithCollision)
            {
                var collisionRestored = this.collisionExperimentService.RestoreCollision(instance, unsafeEnabled, fullLayoutConfirmed: true, confirmed: true);
                if (!collisionRestored)
                    instance.CollisionError = $"切换 VisualOnly 时恢复原 collision 失败：{this.collisionExperimentService.LastResult}";
            }

            var layoutRestored = this.RestoreOriginalLayoutTransformDirect(instance, out var layoutResult);
            instance.TransformMode = LocalLayoutTransformMode.VisualOnly;
            instance.CollisionApplied = false;
            instance.HasCollisionMoved = false;
            instance.CollisionSourceResolveResult = $"VisualOnly：collision 已恢复/保持在 carrier 原 slot。layoutRestore={layoutResult}";
            this.WriteInstanceTransform(instance, instance.CurrentPosition, instance.CurrentRotationEuler, instance.CurrentScale, "切换到 VisualOnly");
            this.LastStatus = layoutRestored
                ? $"已切换到 VisualOnly：只写 Graphics.Scene.Object，collision 不随复制体移动。"
                : $"已切换到 VisualOnly，但恢复原 layout transform 失败：{layoutResult}";
            return layoutRestored;
        }

        instance.TransformMode = LocalLayoutTransformMode.FullLayoutWithCollision;
        instance.HasCollisionMoved = true;
        var modelPath = FirstNonEmpty(instance.CurrentModelPath, instance.CurrentResourcePath, instance.CustomModelPath, instance.TemplateResourcePath);
        var resolve = this.collisionSourceResolver.Resolve(modelPath, bgParts);
        instance.CollisionSourceResolveResult = resolve.Message;
        if (resolve.Found && resolve.SourceInstance != null)
        {
            var captured = this.collisionExperimentService.CaptureSource(instance, resolve.SourceInstance);
            if (captured)
                this.collisionExperimentService.ApplySourceCollision(instance, unsafeEnabled, fullLayoutConfirmed: true, confirmed: true);

            instance.CollisionApplied = captured && string.IsNullOrWhiteSpace(instance.CollisionExperimentLastError);
            instance.CollisionError = instance.CollisionApplied ? string.Empty : FirstNonEmpty(instance.CollisionExperimentLastError, this.collisionExperimentService.LastResult);
        }
        else
        {
            instance.CollisionApplied = false;
            instance.CollisionError = "未找到当前 mdl 对应的 collision source，已切换为 FullLayout，但 collision 未替换/保持原状态。";
        }

        this.WriteInstanceTransform(instance, instance.CurrentPosition, instance.CurrentRotationEuler, instance.CurrentScale, "切换到 FullLayoutWithCollision");
        this.LastStatus = instance.CollisionApplied
            ? $"已切换到 FullLayoutWithCollision，并应用 target collision：{instance.CollisionSourceResourcePath}"
            : $"已切换到 FullLayoutWithCollision。{instance.CollisionError}";
        return true;
    }

    private bool FailCollisionModeChange(LocalLayoutObjectInstance instance, string message)
    {
        instance.CollisionError = message;
        instance.LastError = message;
        this.LastStatus = message;
        return false;
    }

    public bool RestoreModelAndTransform(
        string id,
        IEnumerable<LayoutProbeInstance> bgParts,
        bool unsafeEnabled,
        bool fullLayoutConfirmed)
    {
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能恢复单个实例。";
            return false;
        }

        var instance = this.GetById(id);
        if (instance == null)
            return false;

        return this.RestoreOriginalSlotSnapshot(instance, unsafeEnabled, fullLayoutConfirmed: true, removeAfterRestore: false);
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
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能单独恢复实例。";
            return;
        }

        var instance = this.GetById(id);
        if (instance == null)
            return;

        this.RestoreOriginal(instance, removeAfterRestore: false);
    }

    public void Delete(string id)
    {
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能删除实例。";
            return;
        }

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
        if (this.IsBusy)
        {
            this.LastStatus = "RestoreAll 已在执行中，忽略重复请求。";
            return;
        }

        this.IsBusy = true;
        try
        {
            this.animatedPlaybackSystem.StopAllAndDetach(this.instances, "RestoreAll 前全局停止动画回放。");
            this.activeCreateManyJob = null;
            this.reservedSlots.Clear();
            foreach (var instance in this.instances)
            {
                instance.PendingVisualTransform = false;
                instance.TransformMonitorActive = false;
            }

            this.BuildRestorePlanPreview();
            var preCleanupCount = this.CleanupDuplicateInstances(auto: true);
            this.RebuildOccupiedSlotRegistry();
            var restoreTargets = this.instances
                .Where(instance => IsActiveInstance(instance, out _))
                .ToList();
            var restoreCount = restoreTargets.Count;
            var restoredCount = 0;
            var skippedCount = 0;
            var modelRestoreFailures = new List<string>();

            foreach (var instance in restoreTargets)
            {
                try
                {
                    if (!IsActiveInstance(instance, out _))
                    {
                        skippedCount++;
                        continue;
                    }

                    var restored = this.RestoreOriginalSlotSnapshot(
                        instance,
                        unsafeEnabled,
                        fullLayoutConfirmed: true,
                        removeAfterRestore);
                    if (restored)
                    {
                        restoredCount++;
                        continue;
                    }

                    modelRestoreFailures.Add($"{instance.Id}: {FirstNonEmpty(instance.RestoreError, instance.LastModelOverrideError, instance.ApplyMdlError, instance.RestoreStatus)}");
                }
                catch (Exception ex)
                {
                    instance.RestoreStatus = $"RestoreFailed：{ex.Message}";
                    instance.RestoreError = ex.Message;
                    instance.LastError = instance.RestoreStatus;
                    modelRestoreFailures.Add($"{instance.Id}: {ex.Message}");
                }
            }

            if (removeAfterRestore)
                this.instances.RemoveAll(item => item.IsRestored || item.IsDuplicate);

            var postRestoreStaleCount = this.RemoveStaleRecords();
            this.RebuildOccupiedSlotRegistry();
            this.LastStatus =
                $"RestoreAll 完成：total={restoreCount}; restored={restoredCount}; failed={modelRestoreFailures.Count}; skipped={skippedCount}; " +
                $"preCleanup={preCleanupCount}; postCleanup={postRestoreStaleCount}; failedIds={string.Join(" | ", modelRestoreFailures)}";
        }
        finally
        {
            this.IsBusy = false;
        }
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

    public void RebuildOccupiedSlotRegistryForUi()
    {
        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = $"已从实例列表重建 occupied registry：active occupied slot count={this.ActiveOccupiedSlotCount}; duplicate slot count={this.DuplicateSlotCount}";
    }

    public int ClearRestoredAndInvalidInstances()
    {
        var removed = this.instances.RemoveAll(item => item.IsRestored || item.IsInvalid || item.IsDuplicate || !item.IsOccupied);
        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = removed == 0
            ? "没有已恢复/无效实例需要清理。"
            : $"已清理已恢复/无效实例 {removed} 条，并重建 occupied registry。";
        return removed;
    }

    public bool ForceRemoveInstance(string id)
    {
        var instance = this.instances.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (instance == null)
        {
            this.LastStatus = $"找不到要强制移除的实例：{id}";
            return false;
        }

        if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var slotAddress))
            this.occupiedSlots.Remove(slotAddress);

        this.instances.Remove(instance);
        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = $"已强制从 UI/registry 移除实例 {id}。未执行任何 native 写入。";
        return true;
    }

    public int ForceClearBadInstances()
    {
        var removed = this.instances.RemoveAll(item => item.IsRestored || item.IsInvalid || item.IsDuplicate || !item.IsOccupied || IsFailedState(item));
        this.RebuildOccupiedSlotRegistry();
        this.LastStatus = removed == 0
            ? "没有 Restored/Invalid/Failed 残留实例需要强制清理。"
            : $"已强制清理 Restored/Invalid/Failed 残留实例 {removed} 条。未执行任何 native 写入。";
        return removed;
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
        if (this.IsBusy)
        {
            this.LastStatus = "当前正在恢复/清理本地场景物体，暂不能移动实例。";
            return;
        }

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
        this.RestoreOriginalSlotSnapshot(instance, unsafeEnabled: true, fullLayoutConfirmed: true, removeAfterRestore);
    }

    private OriginalSlotSnapshot BuildOriginalSlotSnapshot(
        LocalLayoutObjectInstance instance,
        string originalPrimaryPath,
        LayoutTransformSnapshot originalLayout,
        SceneTransformSnapshot originalVisual,
        LayoutProbeInstance candidate)
    {
        var originalHadCollider =
            instance.CollisionSnapshotMeshPathCrc != 0
            || instance.CollisionSnapshotAnalyticShapeDataCrc != 0
            || (!string.IsNullOrWhiteSpace(instance.CollisionSnapshotColliderAddress)
                && !string.Equals(instance.CollisionSnapshotColliderAddress, "0x0", StringComparison.OrdinalIgnoreCase));

        return new OriginalSlotSnapshot
        {
            InstanceId = instance.Id,
            OccupiedSlotAddress = instance.OccupiedSlotAddress,
            OriginalResourcePath = originalPrimaryPath,
            OriginalPrimaryPath = originalPrimaryPath,
            OriginalModelHandlePath = originalPrimaryPath,
            OriginalLayoutPosition = originalLayout.Position,
            OriginalLayoutRotation = originalLayout.Rotation,
            OriginalLayoutScale = originalLayout.Scale,
            OriginalGraphicsPosition = originalVisual.Position,
            OriginalGraphicsRotation = originalVisual.Rotation,
            OriginalGraphicsScale = originalVisual.Scale,
            OriginalVisible = candidate.Visible,
            OriginalCollisionMeshPathCrc = instance.CollisionSnapshotMeshPathCrc,
            OriginalAnalyticShapeDataCrc = instance.CollisionSnapshotAnalyticShapeDataCrc,
            OriginalMaterialIdLow = instance.CollisionSnapshotMaterialIdLow,
            OriginalMaterialMaskLow = instance.CollisionSnapshotMaterialMaskLow,
            OriginalMaterialIdHigh = instance.CollisionSnapshotMaterialIdHigh,
            OriginalMaterialMaskHigh = instance.CollisionSnapshotMaterialMaskHigh,
            OriginalHadCollider = originalHadCollider,
            OriginalSecondaryPath = instance.CollisionSnapshotSecondaryPath,
            OriginalSourceType = candidate.SourceKind,
            SourceLabel = $"{candidate.ResourcePath} | {candidate.Address}",
        };
    }

    private bool ValidateOriginalSlotSnapshot(LocalLayoutObjectInstance instance, out string reason)
    {
        reason = string.Empty;
        if (!this.TryGetOriginalSlotSnapshot(instance, out var snapshot, out reason))
            return false;

        if (!TryNormalizeSlotAddress(snapshot.OccupiedSlotAddress, out var snapshotSlot)
            || !TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var instanceSlot)
            || snapshotSlot != instanceSlot)
        {
            reason = $"OriginalSlotSnapshot slot 不匹配：snapshot={snapshot.OccupiedSlotAddress}; instance={instance.OccupiedSlotAddress}";
            return false;
        }

        if (!IsTransformNormal(snapshot.OriginalLayoutPosition, snapshot.OriginalLayoutRotation, snapshot.OriginalLayoutScale))
        {
            reason = "OriginalSlotSnapshot 原始 layout transform 数值异常。";
            return false;
        }

        if (!IsTransformNormal(snapshot.OriginalGraphicsPosition, snapshot.OriginalGraphicsRotation, snapshot.OriginalGraphicsScale))
        {
            reason = "OriginalSlotSnapshot 原始 Graphics.Scene.Object transform 数值异常。";
            return false;
        }

        if (!IsSupportedMdlPath(snapshot.OriginalResourcePath))
        {
            reason = $"OriginalSlotSnapshot 原始 mdl path 不受支持或异常：{snapshot.OriginalResourcePath}";
            return false;
        }

        return true;
    }

    private bool TryGetOriginalSlotSnapshot(LocalLayoutObjectInstance instance, out OriginalSlotSnapshot snapshot, out string reason)
    {
        snapshot = instance.OriginalSlotSnapshot ?? new OriginalSlotSnapshot();
        reason = string.Empty;

        if (instance.OriginalSlotSnapshot == null)
        {
            reason = "MissingOriginalSlotSnapshot";
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshot.OriginalResourcePath))
        {
            reason = "MissingOriginalResourcePath";
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshot.OccupiedSlotAddress))
        {
            reason = "MissingOriginalOccupiedSlotAddress";
            return false;
        }

        return true;
    }

    private string BuildRestoreDebugInfo(LocalLayoutObjectInstance instance, OriginalSlotSnapshot snapshot, string restoreTargetPath, string step)
        => $"step={step}; instanceId={instance.Id}; slot={instance.OccupiedSlotAddress}; mode={instance.TransformMode}; " +
           $"originalResourcePath={snapshot.OriginalResourcePath}; currentModelPath={instance.CurrentModelPath}; customModelPath={instance.CustomModelPath}; " +
           $"restoreTargetPath={restoreTargetPath}; originalHadCollider={snapshot.OriginalHadCollider}; " +
           $"mesh=0x{snapshot.OriginalCollisionMeshPathCrc:X8}; analytic=0x{snapshot.OriginalAnalyticShapeDataCrc:X8}";

    public string BuildRestorePlanPreview()
    {
        var lines = new List<string>();
        var targets = this.instances.Where(instance => IsActiveInstance(instance, out _)).ToList();
        lines.Add($"RestoreAll plan：active instances={targets.Count}");
        foreach (var instance in targets)
        {
            if (!this.TryGetOriginalSlotSnapshot(instance, out var snapshot, out var snapshotError))
            {
                lines.Add($"- instanceId={instance.Id}; snapshotError={snapshotError}; slot={instance.OccupiedSlotAddress}; currentModelPath={instance.CurrentModelPath}; customModelPath={instance.CustomModelPath}");
                continue;
            }

            var restoreTargetPath = snapshot.OriginalResourcePath;
            var mismatch = string.IsNullOrWhiteSpace(restoreTargetPath)
                || !string.Equals(restoreTargetPath, snapshot.OriginalResourcePath, StringComparison.OrdinalIgnoreCase);
            var currentPath = string.Empty;
            var currentLayout = "readback failed";
            var currentGraphics = "readback failed";
            var currentCollider = "readback failed";
            var currentMesh = 0u;
            var currentAnalytic = 0u;
            if (TryGetPointer(instance.OccupiedSlotAddress, out var pointer) && pointer != null)
            {
                currentPath = ReadPrimaryPath(pointer);
                var layout = ReadLayoutTransform(pointer);
                if (layout != null)
                    currentLayout = FormatSnapshot(layout.Value);
                if (TryGetGraphicsObjectAddress(pointer, out var graphicsAddress) && graphicsAddress != 0)
                {
                    var graphics = ReadSceneObjectTransform(graphicsAddress);
                    if (graphics != null)
                        currentGraphics = FormatSceneSnapshot(graphics.Value);
                }

                try
                {
                    var bgPart = (BgPartsLayoutInstance*)pointer;
                    currentCollider = $"0x{(nint)bgPart->Collider:X}";
                    currentMesh = bgPart->CollisionMeshPathCrc;
                    currentAnalytic = bgPart->AnalyticShapeDataCrc;
                }
                catch (Exception ex)
                {
                    currentCollider = $"readback failed: {ex.Message}";
                }
            }

            lines.Add(
                $"- instanceId={instance.Id}; mode={instance.TransformMode}; slot={instance.OccupiedSlotAddress}; " +
                $"originalResourcePath={snapshot.OriginalResourcePath}; currentModelPath={instance.CurrentModelPath}; customModelPath={instance.CustomModelPath}; templateResourcePath={instance.TemplateResourcePath}; " +
                $"restoreTargetPath={restoreTargetPath}; targetCheck={(mismatch ? "RestoreTargetMismatch" : "OK")}; currentPrimaryPath={currentPath}; " +
                $"originalLayout={FormatSnapshot(new LayoutTransformSnapshot(snapshot.OriginalLayoutPosition, snapshot.OriginalLayoutRotation, snapshot.OriginalLayoutScale))}; currentLayout={currentLayout}; " +
                $"originalGraphics={FormatSceneSnapshot(new SceneTransformSnapshot(snapshot.OriginalGraphicsPosition, snapshot.OriginalGraphicsRotation, snapshot.OriginalGraphicsScale, false))}; currentGraphics={currentGraphics}; " +
                $"originalHadCollider={snapshot.OriginalHadCollider}; currentCollider={currentCollider}; " +
                $"originalMesh=0x{snapshot.OriginalCollisionMeshPathCrc:X8}; currentMesh=0x{currentMesh:X8}; " +
                $"originalAnalytic=0x{snapshot.OriginalAnalyticShapeDataCrc:X8}; currentAnalytic=0x{currentAnalytic:X8}");
        }

        this.LastRestorePlanPreview = string.Join(Environment.NewLine, lines);
        return this.LastRestorePlanPreview;
    }

    private bool RestoreOriginalSlotSnapshot(
        LocalLayoutObjectInstance instance,
        bool unsafeEnabled,
        bool fullLayoutConfirmed,
        bool removeAfterRestore)
    {
        this.DisablePlayback(instance, "恢复/删除实例前停止动画回放。");
        instance.IsRestoring = true;
        instance.InstanceState = "Restoring";
        instance.LastOperation = removeAfterRestore ? "Delete/RestoreInstance" : "RestoreInstance";
        instance.RestoreStatus = "Restoring";
        instance.RestoreError = string.Empty;
        instance.RestoreStep = "Stop playback";
            instance.PendingVisualTransform = false;
            instance.PendingVisualTransformFrameWait = 0;
            instance.PendingRecreateStabilizeAttempts = 0;
            instance.PendingVisualTransformResult = "恢复流程取消待写 VisualOnly transform。";
        instance.PinTransformEnabled = false;
        instance.TransformMonitorActive = false;
        instance.PinTransformReason = "实例恢复/删除时停止 PinTransform。";

        try
        {
            if (instance.IsDuplicate)
                return this.FailRestore(instance, "重复 slot 实例不参与恢复，避免覆盖原始 transform。", removeAfterRestore, markInvalid: true);
            if (!instance.CanRestore)
                return this.FailRestore(instance, "没有可恢复的原始 slot 快照。", removeAfterRestore, markInvalid: false);
            if (!this.TryGetOriginalSlotSnapshot(instance, out var snapshot, out var snapshotError))
                return this.FailRestore(instance, snapshotError, removeAfterRestore, markInvalid: false);
            if (!this.ValidateOriginalSlotSnapshot(instance, out var validateError))
                return this.FailRestore(instance, validateError, removeAfterRestore, markInvalid: false);
            if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer) || pointer == null)
                return this.FailRestore(instance, $"occupied slot 指针重新读取失败：{instance.OccupiedSlotAddress}", removeAfterRestore, markInvalid: true);

            var originalPath = snapshot.OriginalResourcePath.Trim();
            instance.RestoreDebugInfo = this.BuildRestoreDebugInfo(instance, snapshot, originalPath, "Prepare");
            if (string.IsNullOrWhiteSpace(originalPath))
                return this.FailRestore(instance, "MissingOriginalResourcePath", removeAfterRestore, markInvalid: false);
            if (!string.IsNullOrWhiteSpace(instance.SourceResourcePath)
                && !string.Equals(originalPath, instance.SourceResourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return this.FailRestore(instance, $"RestoreTargetPathMismatch: snapshot={originalPath}; sourceSlot={instance.SourceResourcePath}", removeAfterRestore, markInvalid: false);
            }

            var currentPath = ReadPrimaryPath(pointer);
            if (!string.IsNullOrWhiteSpace(originalPath)
                && !string.Equals(currentPath, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                instance.RestoreStep = "Restore original mdl";
                if (!unsafeEnabled)
                    return this.FailRestore(instance, "恢复原 mdl 需要 UnsafeMode=true。", removeAfterRestore, markInvalid: false);

                if (instance.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision)
                {
                    var preRestoreLayout = this.RestoreOriginalLayoutTransformDirect(instance, snapshot, out var preRestoreLayoutResult);
                    instance.RestoreDebugInfo = $"{instance.RestoreDebugInfo}; preCreatePrimaryLayoutRestore={preRestoreLayout}; {preRestoreLayoutResult}";
                    if (!TryGetPointer(instance.OccupiedSlotAddress, out pointer) || pointer == null)
                        return this.FailRestore(instance, "恢复原 mdl 前 Layout transform 写回后 occupied slot 指针重新读取失败。", removeAfterRestore, markInvalid: true);
                }

                var recreated = this.recreateExperimentService.ExecuteDestroyCreate(instance, originalPath, unsafeEnabled, experimentEnabled: true, confirmed: true);
                if (!recreated)
                    return this.FailRestore(instance, $"恢复原 mdl 失败：{this.recreateExperimentService.LastResult}", removeAfterRestore, markInvalid: instance.IsRenderInvalid);

                instance.CurrentResourcePath = originalPath;
                instance.CurrentModelPath = originalPath;
                instance.AfterRestorePath = originalPath;
                if (!TryGetPointer(instance.OccupiedSlotAddress, out pointer) || pointer == null)
                    return this.FailRestore(instance, "恢复原 mdl 后 occupied slot 指针重新读取失败。", removeAfterRestore, markInvalid: true);
            }

            instance.PendingVisualTransform = false;
            instance.PendingVisualTransformFrameWait = 0;
            instance.PendingRecreateStabilizeAttempts = 0;
            instance.PendingVisualTransformResult = "恢复流程已重新接管 transform，取消 recreate 延迟写入。";

            instance.RestoreStep = "Restore original collision";
            var collisionRestored = this.RestoreOriginalCollisionDirect(instance, snapshot, unsafeEnabled, out var collisionResult);
            if (!collisionRestored)
                instance.CollisionError = $"恢复原 collision 失败：{collisionResult}";

            instance.RestoreStep = "Restore original layout transform";
            var layoutRestored = this.RestoreOriginalLayoutTransformDirect(instance, snapshot, out var layoutResult);

            instance.RestoreStep = "Restore original graphics transform";
            var skipGraphicsVerification = instance.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision;
            var graphicsRestored = true;
            var graphicsResult = "FullLayoutWithCollision：跳过直接写 Graphics.Scene.Object transform，使用 LayoutInstance transform 同步视觉。";
            if (instance.TransformMode == LocalLayoutTransformMode.VisualOnly)
                graphicsRestored = this.RestoreOriginalGraphicsTransformDirect(instance, snapshot, out graphicsResult);
            else
                this.RefreshGraphicsObjectAddressForRestore(instance, out graphicsResult);
            if (!graphicsRestored)
            {
                skipGraphicsVerification = true;
                instance.RestoreDebugInfo = $"{instance.RestoreDebugInfo}; graphicsTransformRestoreSkipped={graphicsResult}";
            }

            instance.RestoreStep = "Restore original visible";
            this.RestoreCarrierVisible(instance, snapshot);

            instance.RestoreStep = "Verify restore readback";
            var verifyResult = this.BuildRestoreVerification(instance, snapshot, skipGraphicsVerification);

            instance.ModelOverrideApplied = false;
            instance.CurrentResourcePath = originalPath;
            instance.CurrentModelPath = originalPath;
            instance.CustomModelPath = string.Empty;
            instance.ApplyMdlStatus = "Restored";
            instance.ApplyMdlError = string.Empty;
            instance.AfterRestorePath = originalPath;
            var restoredOk = layoutRestored && collisionRestored && verifyResult.Success;
            instance.RestoreStatus = restoredOk ? "Restored" : "Failed";
            instance.RestoreError = restoredOk ? string.Empty : $"{layoutResult}; {graphicsResult}; {collisionResult}; {verifyResult.Message}";
            instance.AfterRestorePosition = layoutRestored ? layoutResult : graphicsResult;
            instance.AfterRestoreVisible = snapshot.OriginalVisible.ToString();
            if (restoredOk)
            {
                instance.IsOccupied = false;
                instance.IsRestored = true;
                instance.InstanceState = "Restored";
                instance.HasCollisionMoved = false;
                instance.IsRenderInvalid = false;
                if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var occupiedSlotAddress))
                    this.occupiedSlots.Remove(occupiedSlotAddress);
                if (removeAfterRestore)
                    this.instances.Remove(instance);
            }

            this.LastStatus = restoredOk
                ? $"已恢复原 slot：{instance.Id}；model={originalPath}; layout={layoutResult}; graphics={graphicsResult}; collision={collisionResult}; visible={snapshot.OriginalVisible}"
                : $"恢复未通过验证：{instance.Id}；{instance.RestoreError}";
            if (!restoredOk)
                instance.InstanceState = "Failed";
            return restoredOk;
        }
        catch (Exception ex)
        {
            return this.FailRestore(instance, $"恢复异常：{ex.Message}", removeAfterRestore, markInvalid: false);
        }
        finally
        {
            instance.IsRestoring = false;
        }
    }

    private bool FailRestore(LocalLayoutObjectInstance instance, string message, bool removeAfterRestore, bool markInvalid)
    {
        instance.RestoreStatus = "Failed";
        instance.InstanceState = "Failed";
        instance.RestoreError = message;
        instance.LastError = message;
        if (markInvalid)
        {
            instance.IsInvalid = true;
            instance.IsOccupied = false;
            instance.IsRestored = true;
            if (TryNormalizeSlotAddress(instance.OccupiedSlotAddress, out var invalidSlotAddress))
                this.occupiedSlots.Remove(invalidSlotAddress);
            if (removeAfterRestore)
                this.instances.Remove(instance);
        }

        this.LastStatus = message;
        return false;
    }

    private bool RestoreOriginalLayoutTransformDirect(LocalLayoutObjectInstance instance, out string result)
        => this.TryGetOriginalSlotSnapshot(instance, out var snapshot, out _)
            ? this.RestoreOriginalLayoutTransformDirect(instance, snapshot, out result)
            : this.RestoreOriginalLayoutTransformDirect(
                instance,
                new OriginalSlotSnapshot
                {
                    OriginalLayoutPosition = instance.OriginalLayoutPosition,
                    OriginalLayoutRotation = instance.OriginalLayoutRotation,
                    OriginalLayoutScale = instance.OriginalLayoutScale,
                },
                out result);

    private bool RestoreOriginalLayoutTransformDirect(LocalLayoutObjectInstance instance, OriginalSlotSnapshot snapshot, out string result)
    {
        result = string.Empty;
        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer))
        {
            result = $"slot 地址解析失败：{instance.OccupiedSlotAddress}";
            return false;
        }

        var original = new LayoutTransformSnapshot(snapshot.OriginalLayoutPosition, snapshot.OriginalLayoutRotation, snapshot.OriginalLayoutScale);
        var ok = WriteLayoutTransform(pointer, original);
        var readback = ReadLayoutTransform(pointer);
        result = readback == null ? "layout readback failed" : FormatSnapshot(readback.Value);
        return ok && readback != null;
    }

    private bool RestoreOriginalGraphicsTransformDirect(LocalLayoutObjectInstance instance, OriginalSlotSnapshot snapshot, out string result)
    {
        result = string.Empty;
        if (instance.TransformMode == LocalLayoutTransformMode.FullLayoutWithCollision)
            return this.RefreshGraphicsObjectAddressForRestore(instance, out result);

        if (!this.TryGetSafeGraphicsObjectAddressForRestore(instance, out var graphicsAddress, out _, out result))
            return false;

        instance.GraphicsObjectAddress = $"0x{graphicsAddress:X}";
        var original = new SceneTransformSnapshot(snapshot.OriginalGraphicsPosition, snapshot.OriginalGraphicsRotation, snapshot.OriginalGraphicsScale, false);
        try
        {
            WriteSceneObjectTransform(graphicsAddress, original);
            var readback = ReadSceneObjectTransform(graphicsAddress) ?? original;
            instance.CurrentPosition = readback.Position;
            instance.CurrentRotation = readback.Rotation;
            instance.CurrentRotationEuler = Vector3.Zero;
            instance.CurrentScale = readback.Scale;
            instance.CurrentVisualTranslation = readback.Position;
            instance.LastReadback = FormatSceneSnapshot(readback);
            result = FormatSceneSnapshot(readback);
            return true;
        }
        catch (Exception ex)
        {
            result = $"恢复 Graphics.Scene.Object transform 失败：{ex.Message}";
            return false;
        }
    }

    private bool RefreshGraphicsObjectAddressForRestore(LocalLayoutObjectInstance instance, out string result)
    {
        if (!this.TryGetSafeGraphicsObjectAddressForRestore(instance, out var graphicsAddress, out var readback, out result))
        {
            instance.RestoreDebugInfo = $"{instance.RestoreDebugInfo}; graphicsTransformRestoreSkipped={result}";
            return false;
        }

        instance.GraphicsObjectAddress = $"0x{graphicsAddress:X}";
        instance.LastReadback = FormatSceneSnapshot(readback);
        result = $"GraphicsObject 已重新读取但未直接写入：{FormatSceneSnapshot(readback)}";
        return true;
    }

    private bool TryGetSafeGraphicsObjectAddressForRestore(
        LocalLayoutObjectInstance instance,
        out nint graphicsAddress,
        out SceneTransformSnapshot readback,
        out string reason)
    {
        graphicsAddress = 0;
        readback = default;
        reason = string.Empty;

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer) || pointer == null)
        {
            reason = $"slot 地址解析失败：{instance.OccupiedSlotAddress}";
            return false;
        }

        if (!TryGetGraphicsObjectAddress(pointer, out graphicsAddress) || graphicsAddress == 0)
        {
            reason = "GraphicsObject=null";
            return false;
        }

        try
        {
            var bg = (SceneBgObject*)graphicsAddress;
            if (bg->ModelResourceHandle == null)
            {
                reason = "ModelResourceHandle=null";
                return false;
            }

            if (bg->ModelResourceHandle->LoadState < 7)
            {
                reason = $"LoadState={bg->ModelResourceHandle->LoadState}";
                return false;
            }

            var scene = ReadSceneObjectTransform(graphicsAddress);
            if (scene == null)
            {
                reason = "Scene.Object transform readback failed";
                return false;
            }

            if (!IsTransformNormal(scene.Value.Position, scene.Value.Rotation, scene.Value.Scale))
            {
                reason = $"Scene.Object transform 数值异常：{FormatSceneSnapshot(scene.Value)}";
                return false;
            }

            readback = scene.Value;
            reason = "GraphicsObject safe";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"GraphicsObject 安全检查异常：{ex.Message}";
            return false;
        }
    }

    private void RestoreCarrierVisible(LocalLayoutObjectInstance instance, OriginalSlotSnapshot snapshot)
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

            bg->IsVisible = snapshot.OriginalVisible;
            bg->UpdateRender();
            instance.Visible = snapshot.OriginalVisible;
        }
        catch (Exception ex)
        {
            instance.LastError = $"恢复 carrier visible 失败：{ex.Message}";
        }
    }

    private bool RestoreOriginalCollisionDirect(LocalLayoutObjectInstance instance, OriginalSlotSnapshot snapshot, bool unsafeEnabled, out string result)
    {
        result = string.Empty;
        if (!unsafeEnabled)
        {
            result = "UnsafeMode=false，无法恢复原 collision。";
            return false;
        }

        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer) || pointer == null)
        {
            result = $"slot 地址解析失败：{instance.OccupiedSlotAddress}";
            return false;
        }

        try
        {
            var bgPart = (BgPartsLayoutInstance*)pointer;
            pointer->DestroySecondary();
            bgPart->CollisionMeshPathCrc = snapshot.OriginalCollisionMeshPathCrc;
            bgPart->AnalyticShapeDataCrc = snapshot.OriginalAnalyticShapeDataCrc;
            bgPart->CollisionMaterialIdLow = snapshot.OriginalMaterialIdLow;
            bgPart->CollisionMaterialMaskLow = snapshot.OriginalMaterialMaskLow;
            bgPart->CollisionMaterialIdHigh = snapshot.OriginalMaterialIdHigh;
            bgPart->CollisionMaterialMaskHigh = snapshot.OriginalMaterialMaskHigh;

            if (snapshot.OriginalHadCollider
                && (snapshot.OriginalCollisionMeshPathCrc != 0 || snapshot.OriginalAnalyticShapeDataCrc != 0))
            {
                pointer->CreateSecondary();
            }

            instance.CollisionAfterColliderAddress = $"0x{(nint)bgPart->Collider:X}";
            instance.CollisionAfterMeshPathCrc = bgPart->CollisionMeshPathCrc;
            instance.CollisionAfterAnalyticShapeDataCrc = bgPart->AnalyticShapeDataCrc;
            instance.CollisionAfterColliderType = bgPart->Collider == null
                ? "None"
                : snapshot.OriginalCollisionMeshPathCrc != 0 ? "Mesh" : snapshot.OriginalAnalyticShapeDataCrc != 0 ? "Analytic" : "Unknown";
            instance.CollisionAfterSecondaryPath = ReadSecondaryPath(pointer);
            instance.CollisionApplied = false;
            instance.CollisionError = string.Empty;
            result =
                $"collision restored: collider={instance.CollisionAfterColliderAddress}; mesh=0x{instance.CollisionAfterMeshPathCrc:X8}; analytic=0x{instance.CollisionAfterAnalyticShapeDataCrc:X8}; secondary={instance.CollisionAfterSecondaryPath}";
            return true;
        }
        catch (Exception ex)
        {
            result = $"恢复原 collision 异常：{ex.Message}";
            return false;
        }
    }

    private RestoreVerification BuildRestoreVerification(LocalLayoutObjectInstance instance, OriginalSlotSnapshot snapshot, bool skipGraphicsPositionCheck)
    {
        var failures = new List<string>();
        if (!TryGetPointer(instance.OccupiedSlotAddress, out var pointer) || pointer == null)
            return new RestoreVerification(false, "slot 指针 readback 失败。");

        var originalPath = snapshot.OriginalResourcePath;
        var currentPath = ReadPrimaryPath(pointer);
        if (!string.IsNullOrWhiteSpace(originalPath)
            && !string.IsNullOrWhiteSpace(currentPath)
            && !string.Equals(currentPath, originalPath, StringComparison.OrdinalIgnoreCase))
            failures.Add($"path current={currentPath}, original={originalPath}");

        var layout = ReadLayoutTransform(pointer);
        if (layout == null)
        {
            failures.Add("layout readback failed");
        }
        else if (Vector3.Distance(layout.Value.Position, snapshot.OriginalLayoutPosition) > 0.05f)
        {
            failures.Add($"layout position current={FormatVector(layout.Value.Position)}, original={FormatVector(snapshot.OriginalLayoutPosition)}");
        }

        if (!TryGetGraphicsObjectAddress(pointer, out var graphicsAddress) || graphicsAddress == 0)
        {
            if (!skipGraphicsPositionCheck)
                failures.Add("GraphicsObject=null");
        }
        else
        {
            instance.GraphicsObjectAddress = $"0x{graphicsAddress:X}";
            var graphics = ReadSceneObjectTransform(graphicsAddress);
            if (graphics == null)
            {
                if (!skipGraphicsPositionCheck)
                    failures.Add("graphics transform readback failed");
            }
            else if (!skipGraphicsPositionCheck && Vector3.Distance(graphics.Value.Position, snapshot.OriginalGraphicsPosition) > 0.05f)
            {
                failures.Add($"graphics position current={FormatVector(graphics.Value.Position)}, original={FormatVector(snapshot.OriginalGraphicsPosition)}");
            }

            try
            {
                var bg = (SceneBgObject*)graphicsAddress;
                instance.AfterRestoreVisible = bg->IsVisible.ToString();
                if (bg->IsVisible != snapshot.OriginalVisible)
                    failures.Add($"visible current={bg->IsVisible}, original={snapshot.OriginalVisible}");
            }
            catch (Exception ex)
            {
                failures.Add($"visible readback failed: {ex.Message}");
            }
        }

        try
        {
            var bgPart = (BgPartsLayoutInstance*)pointer;
            var currentHasCollider = bgPart->Collider != null;
            if (snapshot.OriginalHadCollider != currentHasCollider)
                failures.Add($"collider current={currentHasCollider}, original={snapshot.OriginalHadCollider}");
            if (bgPart->CollisionMeshPathCrc != snapshot.OriginalCollisionMeshPathCrc)
                failures.Add($"meshCrc current=0x{bgPart->CollisionMeshPathCrc:X8}, original=0x{snapshot.OriginalCollisionMeshPathCrc:X8}");
            if (bgPart->AnalyticShapeDataCrc != snapshot.OriginalAnalyticShapeDataCrc)
                failures.Add($"analyticCrc current=0x{bgPart->AnalyticShapeDataCrc:X8}, original=0x{snapshot.OriginalAnalyticShapeDataCrc:X8}");
        }
        catch (Exception ex)
        {
            failures.Add($"collision readback failed: {ex.Message}");
        }

        var message = string.Join(" | ", failures);
        return new RestoreVerification(failures.Count == 0, string.IsNullOrWhiteSpace(message) ? "restore readback ok" : message);
    }

    private bool WriteInstanceTransform(LocalLayoutObjectInstance instance, Vector3 position, Vector3 rotationEuler, Vector3 scale, string action)
    {
        if (!instance.IsRestoring && this.IsInstanceSlotProtected(instance, out var protectedReason))
        {
            instance.InstanceState = "Failed";
            instance.LastError = $"该 BgPart 已在保护列表，禁止移动 transform：{protectedReason}";
            this.LastStatus = instance.LastError;
            return false;
        }

        instance.CurrentPosition = position;
        instance.CurrentRotationEuler = rotationEuler;
        instance.CurrentScale = scale;
        instance.LastOperation = action;
        var applied = this.transformService.ApplyTransform(instance);
        if (applied)
        {
            instance.InstanceState = "Ready";
            this.ScheduleTransformMonitor(instance, position, rotationEuler, scale, action);
        }
        else
        {
            instance.InstanceState = "Failed";
            instance.TransformMonitorActive = false;
            instance.LastTransformWriteSkippedReason = this.transformService.LastResult;
            instance.TransformOverwriteDetails = "transform 写入未执行，原因见 transform disabled reason / skipped reason。";
        }

        this.LastStatus = $"{action}：{this.transformService.LastResult}";
        return applied;
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

    private static string ReadPrimaryPath(ILayoutInstance* pointer)
    {
        if (pointer == null)
            return string.Empty;

        try
        {
            var path = pointer->GetPrimaryPath();
            return path.HasValue ? path.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadSecondaryPath(ILayoutInstance* pointer)
    {
        if (pointer == null)
            return string.Empty;

        try
        {
            var path = pointer->GetSecondaryPath();
            return path.HasValue ? path.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
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

    private static bool IsFailedState(LocalLayoutObjectInstance instance)
        => string.Equals(instance.InstanceState, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.RestoreStatus, "Failed", StringComparison.OrdinalIgnoreCase);

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

    private static bool IsTransformNormal(Vector3 position, Quaternion rotation, Vector3 scale)
        => IsVectorNormal(position)
            && IsQuaternionNormal(rotation)
            && IsVectorNormal(scale)
            && Math.Abs(scale.X) > 0.0001f
            && Math.Abs(scale.Y) > 0.0001f
            && Math.Abs(scale.Z) > 0.0001f;

    private static Vector3 NormalizeDesiredScale(Vector3 scale)
        => new(
            Math.Abs(scale.X) < 0.0001f ? 1f : Math.Abs(scale.X),
            Math.Abs(scale.Y) < 0.0001f ? 1f : Math.Abs(scale.Y),
            Math.Abs(scale.Z) < 0.0001f ? 1f : Math.Abs(scale.Z));

    private static bool IsVectorNormal(Vector3 value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && Math.Abs(value.X) < 1_000_000f
            && Math.Abs(value.Y) < 1_000_000f
            && Math.Abs(value.Z) < 1_000_000f;

    private static bool IsQuaternionNormal(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W)
            && value.LengthSquared() is > 0.0001f and < 10f;

    private static string GetStaticObjectBlockReason(LayoutProbeInstance? instance)
    {
        if (instance == null)
            return "未选择 BgPart。";
        if (string.Equals(instance.SourceKind, "SharedGroup", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(instance.ParentAddress)
            || !string.IsNullOrWhiteSpace(instance.SharedGroupPath))
            return "SharedGroup child 暂不支持作为本地静态物体 source / carrier。";

        return GetDynamicPathBlockReason(instance.ResourcePath);
    }

    private static string GetStaticObjectBlockReason(LocalLayoutObjectInstance? instance)
    {
        if (instance == null)
            return "未选择本地实例。";
        if (string.Equals(instance.SourceKind, "SharedGroup", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(instance.SourceParentAddress)
            || !string.IsNullOrWhiteSpace(instance.SourceSharedGroupPath))
            return "该实例来自 SharedGroup child，v9.8 静态稳定版禁止继续修改。";

        return GetDynamicPathBlockReason(FirstNonEmpty(instance.CurrentResourcePath, instance.SourceResourcePath, instance.OriginalResourcePath));
    }

    private static string GetDynamicPathBlockReason(string? resourcePath)
    {
        if (string.IsNullOrWhiteSpace(resourcePath))
            return string.Empty;

        var path = resourcePath.Replace('\\', '/').ToLowerInvariant();
        var fileName = path.Split('/').LastOrDefault() ?? path;
        if (path.Contains("/vfx/", StringComparison.Ordinal))
            return "路径包含 /vfx/，疑似 VFX / controller 驱动资源。";
        if (path.Contains("/light/", StringComparison.Ordinal))
            return "路径包含 /light/，疑似灯光或动态场景资源。";
        if (path.Contains("/shared/", StringComparison.Ordinal))
            return "路径包含 /shared/，疑似 SharedGroup / 共享动态资源。";
        if (path.Contains("/evt/", StringComparison.Ordinal))
            return "路径包含 /evt/，疑似事件控制资源。";
        if (path.Contains("/aet/", StringComparison.Ordinal))
            return "路径包含 /aet/，疑似以太/漂浮/旋转类 controller 驱动资源。";
        if (path.Contains("/twn/", StringComparison.Ordinal)
            && (fileName.Contains("scr", StringComparison.Ordinal)
                || fileName.Contains("screen", StringComparison.Ordinal)
                || fileName.Contains("monitor", StringComparison.Ordinal)
                || fileName.Contains("advert", StringComparison.Ordinal)
                || fileName.Contains("_ad", StringComparison.Ordinal)
                || fileName.Contains("ad_", StringComparison.Ordinal)))
        {
            return "疑似城镇动态屏幕/广告牌类资源。";
        }

        return string.Empty;
    }

    private enum CreateJobState
    {
        Pending,
        AllocatingSlot,
        Recreating,
        WaitingStabilize,
        ApplyingTransform,
        Ready,
        Failed,
    }

    private sealed class CreateManyJob(
        LayoutProbeInstance template,
        List<CreateManyJobItem> items,
        IReadOnlyList<LayoutProbeInstance> bgParts,
        LocalLayoutTransformMode mode,
        bool unsafeEnabled,
        bool fullLayoutConfirmed,
        string templateWarning,
        int requestedCount,
        CarrierAllocationPolicy policy)
    {
        public LayoutProbeInstance Template { get; } = template;

        public List<CreateManyJobItem> Items { get; } = items;

        public IReadOnlyList<LayoutProbeInstance> BgParts { get; } = bgParts;

        public LocalLayoutTransformMode Mode { get; } = mode;

        public bool UnsafeEnabled { get; } = unsafeEnabled;

        public bool FullLayoutConfirmed { get; } = fullLayoutConfirmed;

        public string TemplateWarning { get; } = templateWarning;

        public int RequestedCount { get; } = requestedCount;

        public CarrierAllocationPolicy Policy { get; } = policy;

        public int TotalCount => this.Items.Count;

        public int CurrentIndex { get; set; }

        public int SuccessCount => this.Items.Count(item => item.State == CreateJobState.Ready);

        public int FailedCount => this.Items.Count(item => item.State == CreateJobState.Failed);

        public int WaitingStabilizeCount => this.Items.Count(item => item.State == CreateJobState.WaitingStabilize);

        public int PendingCount => this.Items.Count(item => item.State is not CreateJobState.Ready and not CreateJobState.Failed);

        public string CurrentStateText
            => this.CurrentIndex > 0 && this.CurrentIndex <= this.Items.Count
                ? this.Items[this.CurrentIndex - 1].State.ToString()
                : string.Empty;

        public string CurrentSlotAddress
            => this.CurrentIndex > 0 && this.CurrentIndex <= this.Items.Count
                ? this.Items[this.CurrentIndex - 1].Slot.Address
                : string.Empty;

        public string LastError { get; set; } = string.Empty;

        public string LastCreatedId { get; set; } = string.Empty;
    }

    private sealed class CreateManyJobItem(PendingCreateItem item)
    {
        public LayoutProbeInstance Slot { get; } = item.Slot;

        public Vector3 Position { get; } = item.Position;

        public Vector3 RotationEuler { get; } = item.RotationEuler;

        public Vector3 Scale { get; } = item.Scale;

        public string CustomMdlPath { get; } = item.CustomMdlPath;

        public CreateJobState State { get; set; } = CreateJobState.Pending;

        public int FrameInState { get; set; }

        public int TimeoutFrames { get; } = 120;

        public ulong SlotAddress { get; set; }

        public string InstanceId { get; set; } = string.Empty;

        public string LastMessage { get; set; } = string.Empty;

        public string LastError { get; set; } = string.Empty;
    }

    private sealed class AllocationPlan(
        string id,
        string templateAddress,
        string templateResourcePath,
        int requestedCount,
        CarrierAllocationPolicy policy,
        Vector3? playerPosition,
        string protectedVersion,
        string preferredModifyVersion,
        string occupiedRegistryVersion,
        IReadOnlyList<string> candidateAddresses,
        IReadOnlyList<LayoutProbeInstance> selectedSlots,
        int sameModelAvailable,
        int preferredModifyAvailable,
        int anyValidAvailable,
        int totalAvailable,
        bool isOrderValid,
        string orderValidationMessage)
    {
        public string Id { get; } = id;

        public string TemplateAddress { get; } = templateAddress;

        public string TemplateResourcePath { get; } = templateResourcePath;

        public int RequestedCount { get; } = requestedCount;

        public CarrierAllocationPolicy Policy { get; } = policy;

        public Vector3? PlayerPosition { get; } = playerPosition;

        public string ProtectedVersion { get; } = protectedVersion;

        public string PreferredModifyVersion { get; } = preferredModifyVersion;

        public string OccupiedRegistryVersion { get; } = occupiedRegistryVersion;

        public IReadOnlyList<string> CandidateAddresses { get; } = candidateAddresses;

        public IReadOnlyList<LayoutProbeInstance> SelectedSlots { get; } = selectedSlots;

        public int SameModelAvailable { get; } = sameModelAvailable;

        public int PreferredModifyAvailable { get; } = preferredModifyAvailable;

        public int AnyValidAvailable { get; } = anyValidAvailable;

        public int FallbackAvailable { get; } = anyValidAvailable;

        public int TotalAvailable { get; } = totalAvailable;

        public bool IsOrderValid { get; } = isOrderValid;

        public string OrderValidationMessage { get; } = orderValidationMessage;

        public bool IsStale(
            LayoutProbeInstance template,
            int requestedCount,
            CarrierAllocationPolicy policy,
            Vector3? playerPosition,
            string protectedVersion,
            string preferredModifyVersion,
            string occupiedRegistryVersion,
            IReadOnlyList<LayoutProbeInstance> currentCandidates)
        {
            if (!string.Equals(this.TemplateAddress, template.Address, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(this.TemplateResourcePath, template.ResourcePath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (this.RequestedCount != requestedCount || this.Policy != policy)
                return true;
            if (this.ProtectedVersion != protectedVersion || this.PreferredModifyVersion != preferredModifyVersion || this.OccupiedRegistryVersion != occupiedRegistryVersion)
                return true;
            if (this.PlayerPosition.HasValue != playerPosition.HasValue)
                return true;
            if (this.PlayerPosition.HasValue && Vector3.Distance(this.PlayerPosition.Value, playerPosition!.Value) > 0.1f)
                return true;

            var currentAddresses = currentCandidates.Select(slot => slot.Address).OrderBy(address => address, StringComparer.OrdinalIgnoreCase).ToList();
            return currentAddresses.Count != this.CandidateAddresses.Count
                || currentAddresses.Where((address, index) => !string.Equals(address, this.CandidateAddresses[index], StringComparison.OrdinalIgnoreCase)).Any();
        }
    }

    private readonly record struct PendingCreateItem(
        LayoutProbeInstance Slot,
        Vector3 Position,
        Vector3 RotationEuler,
        Vector3 Scale,
        string CustomMdlPath);

    private readonly record struct LayoutTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private readonly record struct SceneTransformSnapshot(Vector3 Position, Quaternion Rotation, Vector3 Scale, bool IsTransformChanged);

    private readonly record struct RestoreVerification(bool Success, string Message);
}



