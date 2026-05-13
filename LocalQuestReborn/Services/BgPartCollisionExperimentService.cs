using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

public sealed unsafe class BgPartCollisionExperimentService
{
    public string LastResult { get; private set; } = "BgPart collision source 单实例实验尚未执行。";

    public bool SaveSnapshot(LocalLayoutObjectInstance instance)
    {
        if (!TryGetInstanceBgPart(instance, out var bgPart, out var error))
            return this.Fail(instance, error);

        try
        {
            var info = ReadInfo(bgPart);
            instance.CollisionSnapshotMeshPathCrc = info.MeshPathCrc;
            instance.CollisionSnapshotAnalyticShapeDataCrc = info.AnalyticShapeDataCrc;
            instance.CollisionSnapshotMaterialIdLow = info.MaterialIdLow;
            instance.CollisionSnapshotMaterialMaskLow = info.MaterialMaskLow;
            instance.CollisionSnapshotMaterialIdHigh = info.MaterialIdHigh;
            instance.CollisionSnapshotMaterialMaskHigh = info.MaterialMaskHigh;
            instance.CollisionSnapshotColliderAddress = info.ColliderAddress;
            instance.CollisionSnapshotColliderType = info.ColliderType;
            instance.CollisionSnapshotSecondaryPath = info.SecondaryPath;
            instance.CollisionExperimentLastError = string.Empty;
            instance.CollisionExperimentLastResult =
                $"已保存 collision 快照：type={info.ColliderType}; meshCrc=0x{info.MeshPathCrc:X8}; analyticCrc=0x{info.AnalyticShapeDataCrc:X8}; collider={info.ColliderAddress}; secondary={info.SecondaryPath}";
            this.LastResult = instance.CollisionExperimentLastResult;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"保存 collision 快照失败：{ex.Message}");
        }
    }

    public bool CaptureSource(LocalLayoutObjectInstance instance, LayoutProbeInstance? source)
    {
        if (!TryGetSourceBgPart(source, out var bgPart, out var error))
            return this.Fail(instance, error);

        try
        {
            var info = ReadInfo(bgPart);
            instance.CollisionSourceBgPartAddress = source!.Address;
            instance.CollisionSourceResourcePath = source.ResourcePath;
            instance.CollisionSourceColliderType = info.ColliderType;
            instance.CollisionSourceMeshPathCrc = info.MeshPathCrc;
            instance.CollisionSourceAnalyticShapeDataCrc = info.AnalyticShapeDataCrc;
            instance.CollisionSourceMaterialIdLow = info.MaterialIdLow;
            instance.CollisionSourceMaterialMaskLow = info.MaterialMaskLow;
            instance.CollisionSourceMaterialIdHigh = info.MaterialIdHigh;
            instance.CollisionSourceMaterialMaskHigh = info.MaterialMaskHigh;
            instance.CollisionSourceSecondaryPath = info.SecondaryPath;
            instance.CollisionExperimentLastError = string.Empty;
            instance.CollisionExperimentLastResult =
                $"已选择 collision source：{source.ResourcePath}; type={info.ColliderType}; meshCrc=0x{info.MeshPathCrc:X8}; analyticCrc=0x{info.AnalyticShapeDataCrc:X8}; secondary={info.SecondaryPath}";
            this.LastResult = instance.CollisionExperimentLastResult;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"读取 source collision 失败：{ex.Message}");
        }
    }

    public bool ApplySourceCollision(
        LocalLayoutObjectInstance instance,
        bool unsafeEnabled,
        bool fullLayoutConfirmed,
        bool confirmed)
    {
        var blockReason = this.GetApplyBlockReason(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        if (!TryGetInstanceBgPart(instance, out var bgPart, out var error))
            return this.Fail(instance, error);

        try
        {
            if (string.IsNullOrWhiteSpace(instance.CollisionSnapshotColliderType) && !this.SaveSnapshot(instance))
                return false;

            var layout = (ILayoutInstance*)bgPart;
            layout->DestroySecondary();

            if (string.Equals(instance.CollisionSourceColliderType, "Analytic", StringComparison.OrdinalIgnoreCase))
            {
                bgPart->CollisionMeshPathCrc = 0;
                bgPart->AnalyticShapeDataCrc = instance.CollisionSourceAnalyticShapeDataCrc;
                CopyMaterial(instance, bgPart, fromSource: true);
                layout->CreateSecondary();
            }
            else if (string.Equals(instance.CollisionSourceColliderType, "Mesh", StringComparison.OrdinalIgnoreCase))
            {
                bgPart->CollisionMeshPathCrc = instance.CollisionSourceMeshPathCrc;
                bgPart->AnalyticShapeDataCrc = 0;
                CopyMaterial(instance, bgPart, fromSource: true);
                layout->CreateSecondary();
            }
            else
            {
                bgPart->CollisionMeshPathCrc = 0;
                bgPart->AnalyticShapeDataCrc = 0;
                CopyMaterial(instance, bgPart, fromSource: true);
            }

            var after = ReadInfo(bgPart);
            StoreAfter(instance, after);
            instance.HasCollisionMoved = after.ColliderAddress != "0x0";
            instance.CollisionExperimentLastError = string.Empty;
            instance.CollisionExperimentLastResult =
                $"已应用 source collision：sourceType={instance.CollisionSourceColliderType}; afterType={after.ColliderType}; afterCollider={after.ColliderAddress}; meshCrc=0x{after.MeshPathCrc:X8}; analyticCrc=0x{after.AnalyticShapeDataCrc:X8}; secondary={after.SecondaryPath}";
            this.LastResult = instance.CollisionExperimentLastResult;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"应用 source collision 失败：{ex}");
        }
    }

    public bool RestoreCollision(LocalLayoutObjectInstance instance, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        var blockReason = this.GetRestoreBlockReason(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        if (!TryGetInstanceBgPart(instance, out var bgPart, out var error))
            return this.Fail(instance, error);

        try
        {
            var layout = (ILayoutInstance*)bgPart;
            layout->DestroySecondary();

            bgPart->CollisionMeshPathCrc = instance.CollisionSnapshotMeshPathCrc;
            bgPart->AnalyticShapeDataCrc = instance.CollisionSnapshotAnalyticShapeDataCrc;
            CopyMaterial(instance, bgPart, fromSource: false);

            if (bgPart->CollisionMeshPathCrc != 0 || bgPart->AnalyticShapeDataCrc != 0)
                layout->CreateSecondary();

            var after = ReadInfo(bgPart);
            StoreAfter(instance, after);
            instance.CollisionExperimentLastError = string.Empty;
            instance.CollisionExperimentLastResult =
                $"已恢复原 collision source：afterType={after.ColliderType}; afterCollider={after.ColliderAddress}; meshCrc=0x{after.MeshPathCrc:X8}; analyticCrc=0x{after.AnalyticShapeDataCrc:X8}; secondary={after.SecondaryPath}";
            this.LastResult = instance.CollisionExperimentLastResult;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"恢复原 collision source 失败：{ex}");
        }
    }

    public string GetApplyBlockReason(LocalLayoutObjectInstance? instance, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        var shared = GetSharedBlockReason(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
        if (!string.IsNullOrWhiteSpace(shared))
            return shared;
        if (string.IsNullOrWhiteSpace(instance!.CollisionSourceBgPartAddress))
            return "请先点击“选择当前 BgPart 为 collision source”。";
        if (string.IsNullOrWhiteSpace(instance.CollisionSourceColliderType))
            return "source collision 类型为空。";
        return string.Empty;
    }

    public string GetRestoreBlockReason(LocalLayoutObjectInstance? instance, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        var shared = GetSharedBlockReason(instance, unsafeEnabled, fullLayoutConfirmed, confirmed);
        if (!string.IsNullOrWhiteSpace(shared))
            return shared;
        if (string.IsNullOrWhiteSpace(instance!.CollisionSnapshotColliderType))
            return "请先保存当前实例 collision 快照。";
        return string.Empty;
    }

    private static string GetSharedBlockReason(LocalLayoutObjectInstance? instance, bool unsafeEnabled, bool fullLayoutConfirmed, bool confirmed)
    {
        if (!unsafeEnabled)
            return "UnsafeMode=false。";
        if (!fullLayoutConfirmed)
            return "FullLayoutWithCollision 危险模式未二次确认。";
        if (!confirmed)
            return "需要勾选 collision source 高风险实验确认。";
        if (instance == null)
            return "未选中 LocalLayoutObjectInstance。";
        if (instance.TransformMode != LocalLayoutTransformMode.FullLayoutWithCollision)
            return "只有 FullLayoutWithCollision 模式允许执行 target collision 实验。VisualOnly 必须保持无碰撞。";
        if (instance.IsInvalid || instance.IsRestored || instance.IsDuplicate)
            return "实例已失效、已恢复或是重复记录。";
        if (string.IsNullOrWhiteSpace(instance.OccupiedSlotAddress))
            return "occupiedSlotAddress 为空。";
        return string.Empty;
    }

    private static void CopyMaterial(LocalLayoutObjectInstance instance, BgPartsLayoutInstance* bgPart, bool fromSource)
    {
        bgPart->CollisionMaterialIdLow = fromSource ? instance.CollisionSourceMaterialIdLow : instance.CollisionSnapshotMaterialIdLow;
        bgPart->CollisionMaterialMaskLow = fromSource ? instance.CollisionSourceMaterialMaskLow : instance.CollisionSnapshotMaterialMaskLow;
        bgPart->CollisionMaterialIdHigh = fromSource ? instance.CollisionSourceMaterialIdHigh : instance.CollisionSnapshotMaterialIdHigh;
        bgPart->CollisionMaterialMaskHigh = fromSource ? instance.CollisionSourceMaterialMaskHigh : instance.CollisionSnapshotMaterialMaskHigh;
    }

    private static void StoreAfter(LocalLayoutObjectInstance instance, CollisionInfo info)
    {
        instance.CollisionAfterColliderAddress = info.ColliderAddress;
        instance.CollisionAfterMeshPathCrc = info.MeshPathCrc;
        instance.CollisionAfterAnalyticShapeDataCrc = info.AnalyticShapeDataCrc;
        instance.CollisionAfterColliderType = info.ColliderType;
        instance.CollisionAfterSecondaryPath = info.SecondaryPath;
    }

    private bool Fail(LocalLayoutObjectInstance instance, string message)
    {
        instance.CollisionExperimentLastError = message;
        this.LastResult = message;
        return false;
    }

    private static CollisionInfo ReadInfo(BgPartsLayoutInstance* bgPart)
    {
        var layout = (ILayoutInstance*)bgPart;
        var meshCrc = bgPart->CollisionMeshPathCrc;
        var analyticCrc = bgPart->AnalyticShapeDataCrc;
        var type = analyticCrc != 0 ? "Analytic" : meshCrc != 0 ? "Mesh" : "None";
        return new CollisionInfo(
            meshCrc,
            analyticCrc,
            bgPart->CollisionMaterialIdLow,
            bgPart->CollisionMaterialMaskLow,
            bgPart->CollisionMaterialIdHigh,
            bgPart->CollisionMaterialMaskHigh,
            $"0x{(nint)bgPart->Collider:X}",
            type,
            ReadSecondaryPath(layout));
    }

    private static string ReadSecondaryPath(ILayoutInstance* layout)
    {
        try
        {
            var path = layout->GetSecondaryPath();
            return path.HasValue ? path.ToString() : "secondary path=null";
        }
        catch (Exception ex)
        {
            return $"secondary path 读取失败：{ex.Message}";
        }
    }

    private static bool TryGetSourceBgPart(LayoutProbeInstance? source, out BgPartsLayoutInstance* bgPart, out string error)
    {
        bgPart = null;
        error = string.Empty;
        if (source == null)
        {
            error = "请先选择一个 BgPart 作为 collision source。";
            return false;
        }

        if (!string.Equals(source.Type, "BgPart", StringComparison.Ordinal))
        {
            error = $"source 不是 BgPart：{source.Type}";
            return false;
        }

        return TryGetBgPart(source.Address, out bgPart, out error);
    }

    private static bool TryGetInstanceBgPart(LocalLayoutObjectInstance? instance, out BgPartsLayoutInstance* bgPart, out string error)
    {
        bgPart = null;
        error = string.Empty;
        if (instance == null)
        {
            error = "未选中 LocalLayoutObjectInstance。";
            return false;
        }

        return TryGetBgPart(instance.OccupiedSlotAddress, out bgPart, out error);
    }

    private static bool TryGetBgPart(string? rawAddress, out BgPartsLayoutInstance* bgPart, out string error)
    {
        bgPart = null;
        error = string.Empty;
        if (!TryParseAddress(rawAddress, out var address) || address == 0)
        {
            error = $"BgPart 地址解析失败：{rawAddress}";
            return false;
        }

        bgPart = (BgPartsLayoutInstance*)address;
        if (((ILayoutInstance*)bgPart)->Id.Type != InstanceType.BgPart)
        {
            error = $"当前地址不是 BgPart：{((ILayoutInstance*)bgPart)->Id.Type}";
            bgPart = null;
            return false;
        }

        return true;
    }

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

    private readonly record struct CollisionInfo(
        uint MeshPathCrc,
        uint AnalyticShapeDataCrc,
        uint MaterialIdLow,
        uint MaterialMaskLow,
        uint MaterialIdHigh,
        uint MaterialMaskHigh,
        string ColliderAddress,
        string ColliderType,
        string SecondaryPath);
}
