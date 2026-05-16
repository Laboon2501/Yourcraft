using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Yourcraft.Models;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace Yourcraft.Services;

public sealed unsafe class StandaloneBgObjectProbeService
{
    public const string PluginPoolName = "Yourcraft.StandaloneBgObject";
    public const string LayoutBgPartsPoolName = "Client.LayoutEngine.Layer.BgPartsLayoutInstance";

    private const int InitialValidateFrameWait = 6;
    private const int RequiredStableReadFrames = 2;
    private const int MaxValidateAttempts = 60;
    private const int CachedMatricesOffset = 0xA0;
    private const int StainOrBgChangeDataOffset = 0xA8;
    private const int CachedTransformOffset = 0xB0;
    private const int AnimationDataOffset = 0xB8;
    private const int DefaultParentChildScanMaxCount = 64;
    private const int FullParentChildScanMaxCount = 2048;
    private const double DefaultParentChildScanMaxMs = 20;
    private const double FullParentChildScanMaxMs = 60;
    private const string SceneAttachWritePausedReason =
        "Standalone scene attach 写入实验已暂停：AddChild 后 objectFlags 异常，疑似调用签名或入口不安全。当前只保留 CreateOnly、Dump、Validate、Position 单字段写入、Bounds dump 和 Standalone/真实 BgPart 对比。";

    private readonly List<StandaloneObjectInstance> instances = [];
    private int updateFrame;
    private static readonly Lazy<IReadOnlyList<(nint Start, nint End)>> MainModuleRanges = new(BuildMainModuleRanges);

    public IReadOnlyList<StandaloneObjectInstance> Instances => this.instances;

    public string LastProbeResult { get; private set; } = "尚未探测 Standalone BgObject。";

    public string LastCreateResult { get; private set; } = "尚未创建 Standalone BgObject。";

    public string LastError { get; private set; } = string.Empty;

    public StandaloneObjectInstance? GetById(string id)
        => this.instances.FirstOrDefault(instance => string.Equals(instance.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Update()
    {
        this.updateFrame++;
        foreach (var instance in this.instances.ToList())
        {
            if (instance.State is not (StandaloneObjectState.CreatedUnvalidated or StandaloneObjectState.WaitingValidate))
                continue;

            if (instance.ValidateFrameWait > 0)
            {
                instance.State = StandaloneObjectState.WaitingValidate;
                instance.ValidateFrameWait--;
                instance.ValidationStatus = $"等待只读验证：剩余 {instance.ValidateFrameWait} frame。";
                continue;
            }

            this.ValidateReadOnly(instance, automatic: true);
        }
    }

    public void ProbeExistingBgPart(LayoutProbeInstance? bgPart)
    {
        if (bgPart == null)
        {
            this.LastProbeResult = "当前没有选中 BgPart。";
            return;
        }

        if (!TryGetLayoutPointer(bgPart.Address, out var pointer) || pointer == null)
        {
            this.LastProbeResult = $"BgPart 地址解析失败：{bgPart.Address}";
            return;
        }

        try
        {
            if (pointer->Id.Type != InstanceType.BgPart)
            {
                this.LastProbeResult = $"当前实例不是 BgPart：{pointer->Id.Type}";
                return;
            }

            var layoutTransform = ReadLayoutTransform(pointer);
            var bgLayout = (BgPartsLayoutInstance*)pointer;
            var graphicsAddress = (nint)bgLayout->GraphicsObject;
            this.LastProbeResult = graphicsAddress == 0
                ? $"BgPart 没有 GraphicsObject。address={bgPart.Address}; resourcePath={bgPart.ResourcePath}"
                : BuildBgObjectDump(graphicsAddress, $"existing BgPart | layout={layoutTransform} | resourcePath={bgPart.ResourcePath}");
        }
        catch (Exception ex)
        {
            this.LastProbeResult = $"探测现有 BgPart 失败：{ex}";
        }
    }

    public StandaloneObjectInstance? Create(
        string modelPath,
        string poolName,
        Vector3 position,
        Vector3 rotationEuler,
        Vector3 scale,
        bool unsafeEnabled,
        bool confirmed)
    {
        var blockReason = GetCreateBlockReason(modelPath, position, rotationEuler, scale, unsafeEnabled, confirmed);
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            this.LastError = blockReason;
            this.LastCreateResult = blockReason;
            return null;
        }

        modelPath = modelPath.Trim();
        poolName = string.IsNullOrWhiteSpace(poolName) ? PluginPoolName : poolName.Trim();
        try
        {
            var bgObject = SceneBgObject.Create(modelPath, poolName, null);
            if (bgObject == null)
            {
                this.LastError = "BgObject.Create 返回 null。";
                this.LastCreateResult = this.LastError;
                return null;
            }

            var address = (nint)bgObject;
            var instance = new StandaloneObjectInstance
            {
                Id = $"standalone-bg-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..45],
                ObjectAddress = $"0x{address:X}",
                ModelPath = modelPath,
                PoolName = poolName,
                CreatedAt = DateTimeOffset.Now,
                CreatedFrame = this.updateFrame,
                Position = position,
                RotationEuler = rotationEuler,
                Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(rotationEuler.Y, rotationEuler.X, rotationEuler.Z)),
                Scale = NormalizeScale(scale),
                OwnedByPlugin = true,
                IsValid = true,
                State = StandaloneObjectState.CreatedUnvalidated,
                ValidateFrameWait = InitialValidateFrameWait,
                MaxValidateAttempts = MaxValidateAttempts,
                LastOperation = "CreateOnly",
                ValidationStatus = "已创建对象指针，但尚未验证是否可写/可见；创建流程没有写 transform、没有 UpdateRender、没有 NotifyTransformChanged。",
            };

            this.instances.Add(instance);
            this.LastError = string.Empty;
            this.LastCreateResult =
                $"CreateOnly 完成：address={instance.ObjectAddress}; path={modelPath}; poolName={poolName}; state={instance.State}。已禁止同帧 transform 写入，等待延迟只读验证。";
            return instance;
        }
        catch (Exception ex)
        {
            this.LastError = ex.ToString();
            this.LastCreateResult = $"创建 Standalone BgObject 异常：{ex.Message}";
            return null;
        }
    }

    public bool Dump(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        return this.Dump(instance);
    }

    public bool Validate(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        return this.ValidateReadOnly(instance, automatic: false);
    }

    public bool FullParentChildScan(string id)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        try
        {
            var address = ParseRequiredAddress(instance.ObjectAddress);
            var obj = (SceneObject*)address;
            var result = ScanParentChildList(obj->ParentObject, obj, FullParentChildScanMaxCount, FullParentChildScanMaxMs, includeChain: true);
            ApplyAttachScanResult(instance, obj, result);
            instance.FullParentChildScanDump = result.ChainDump;
            instance.LastOperation = "FullParentChildScan";
            this.LastCreateResult = $"完整 parent child scan 完成：hit={result.Hit}; count={result.TotalScanned}; truncated={result.Truncated}; elapsed={result.ElapsedMs:F2}ms; reason={result.EndReason}";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"完整 parent child scan 失败：{ex.Message}");
        }
    }

    public bool TryWritePosition(string id, Vector3 position, bool unsafeEnabled, bool confirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        var blockReason = this.GetWriteBlockReason(instance, unsafeEnabled, confirmed, requirePositionSuccess: false);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        if (!IsVectorNormal(position))
            return this.Fail(instance, $"目标 Position 数值异常：{FormatVector(position)}");

        try
        {
            var before = this.BuildInstanceDump(instance, "position write before");
            var address = ParseRequiredAddress(instance.ObjectAddress);
            var obj = (SceneObject*)address;
            obj->Position = position;
            instance.Position = obj->Position;
            instance.State = StandaloneObjectState.PositionWriteSucceeded;
            instance.TransformReadback = FormatTransform(obj->Position, obj->Rotation, obj->Scale);
            instance.LastDump = before + Environment.NewLine + this.BuildInstanceDump(instance, "position write after");
            instance.LastOperation = "WritePositionOnly";
            instance.LastError = string.Empty;
            this.LastCreateResult = $"已只写 Position 字段：{instance.ObjectAddress}; readback={instance.TransformReadback}";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            instance.State = StandaloneObjectState.Invalid;
            return this.Fail(instance, $"只写 Position 失败：{ex.Message}");
        }
    }

    public bool TryWriteRotationScale(string id, Vector3 rotationEuler, Vector3 scale, bool unsafeEnabled, bool confirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        var blockReason = this.GetWriteBlockReason(instance, unsafeEnabled, confirmed, requirePositionSuccess: true);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        if (!IsVectorNormal(rotationEuler) || !IsVectorNormal(scale))
            return this.Fail(instance, "Rotation / Scale 数值异常。");

        try
        {
            var address = ParseRequiredAddress(instance.ObjectAddress);
            var obj = (SceneObject*)address;
            var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(rotationEuler.Y, rotationEuler.X, rotationEuler.Z));
            if (!IsQuaternionNormal(rotation))
                return this.Fail(instance, $"目标 rotation 数值异常：{rotation}");

            obj->Rotation = rotation;
            obj->Scale = NormalizeScale(scale);
            instance.RotationEuler = rotationEuler;
            instance.Rotation = obj->Rotation;
            instance.Scale = obj->Scale;
            instance.TransformReadback = FormatTransform(obj->Position, obj->Rotation, obj->Scale);
            instance.LastDump = this.BuildInstanceDump(instance, "rotation/scale write after");
            instance.LastOperation = "WriteRotationScaleOnly";
            instance.LastError = string.Empty;
            this.LastCreateResult = $"已写 Rotation/Scale 字段：{instance.ObjectAddress}; readback={instance.TransformReadback}";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            instance.State = StandaloneObjectState.Invalid;
            return this.Fail(instance, $"写 Rotation/Scale 失败：{ex.Message}");
        }
    }

    public bool ExecuteActivationStep(string id, string stepName, bool unsafeEnabled, bool confirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        if (!string.Equals(stepName, "ComputeSphereBounds", StringComparison.Ordinal))
            return this.FailActivation(instance, stepName, "Standalone render/update 写入实验已暂停；当前只保留 ComputeSphereBounds bounds dump。");

        var blockReason = this.GetActivationBlockReason(instance, unsafeEnabled, confirmed);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.FailActivation(instance, stepName, blockReason);

        try
        {
            var address = ParseRequiredAddress(instance.ObjectAddress);
            var bg = (SceneBgObject*)address;
            var detail = stepName switch
            {
                "SetIsTransformChangedTrue" => Invoke("IsTransformChanged=true", () => bg->IsTransformChanged = true),
                "NotifyTransformChanged" => Invoke("NotifyTransformChanged()", () => bg->NotifyTransformChanged()),
                "UpdateTransformsTrue" => Invoke("UpdateTransforms(true)", () => bg->UpdateTransforms(true)),
                "UpdateMaterials" => Invoke("UpdateMaterials()", () => bg->UpdateMaterials()),
                "UpdateRender" => Invoke("UpdateRender()", () => bg->UpdateRender()),
                "ComputeSphereBounds" => this.InvokeComputeSphereBounds(bg, instance),
                "ComputeSphereBoundsThenUpdateCulling" => this.InvokeComputeSphereBoundsThenUpdateCulling(bg, instance),
                "BoundsCullingRebuild" => this.InvokeBoundsCullingRebuild(bg, instance),
                "UpdateTransformsThenUpdateRender" => Invoke("UpdateTransforms(true) -> UpdateRender()", () =>
                {
                    bg->UpdateTransforms(true);
                    bg->UpdateRender();
                }),
                "NotifyTransformChanged_UpdateTransforms_UpdateRender" => Invoke("NotifyTransformChanged() -> UpdateTransforms(true) -> UpdateRender()", () =>
                {
                    bg->NotifyTransformChanged();
                    bg->UpdateTransforms(true);
                    bg->UpdateRender();
                }),
                "ComputeSphereBounds_UpdateCulling_UpdateRender" => this.InvokeComputeSphereBoundsThenUpdateCullingThenUpdateRender(bg, instance),
                _ => $"未知 activation step：{stepName}",
            };

            instance.ActivationStep = stepName;
            instance.ActivationResult = detail;
            instance.ActivationException = string.Empty;
            instance.LastOperation = "ActivationStep";
            instance.LastDump = this.BuildInstanceDump(instance, $"activation after {stepName}");
            this.RefreshAttachState(instance, fullScan: false);
            this.ValidateReadOnly(instance, automatic: false);
            this.LastCreateResult = $"{stepName} 完成：{detail}";
            this.LastError = string.Empty;
            return !detail.StartsWith("未知", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            return this.FailActivation(instance, stepName, ex.ToString());
        }
    }

    public bool CompareWithBgPart(string id, LayoutProbeInstance? bgPart)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        if (bgPart == null)
            return this.Fail(instance, "当前没有选中真实 BgPart，无法对比。");

        if (!TryGetLayoutPointer(bgPart.Address, out var pointer) || pointer == null)
            return this.Fail(instance, $"真实 BgPart 地址解析失败：{bgPart.Address}");

        try
        {
            var bgLayout = (BgPartsLayoutInstance*)pointer;
            var realGraphics = (nint)bgLayout->GraphicsObject;
            if (realGraphics == 0)
                return this.Fail(instance, "真实 BgPart GraphicsObject=null。");

            var standaloneAddress = ParseRequiredAddress(instance.ObjectAddress);
            var standaloneDump = BuildCompactRenderState(standaloneAddress, "Standalone");
            var realDump = BuildCompactRenderState(realGraphics, $"真实 BgPart {bgPart.ResourcePath}");
            instance.LastDump = string.Join(Environment.NewLine, new[]
            {
                "Standalone vs 真实 BgPart render 状态对比",
                standaloneDump,
                realDump,
                BuildRenderStateDiff(standaloneAddress, realGraphics),
            });
            instance.LastOperation = "CompareWithBgPart";
            instance.LastError = string.Empty;
            this.LastCreateResult = "已生成 Standalone 与当前选中真实 BgPart 的 render 状态对比。";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"对比 Standalone / 真实 BgPart 失败：{ex.Message}");
        }
    }

    public bool CompareSceneAttachWithBgPart(string id, LayoutProbeInstance? bgPart)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        if (bgPart == null)
            return this.Fail(instance, "当前没有选中真实 BgPart，无法做 scene attach 对比。");

        if (!TryGetLayoutPointer(bgPart.Address, out var pointer) || pointer == null)
            return this.Fail(instance, $"真实 BgPart 地址解析失败：{bgPart.Address}");

        try
        {
            var bgLayout = (BgPartsLayoutInstance*)pointer;
            var realGraphics = (nint)bgLayout->GraphicsObject;
            if (realGraphics == 0)
                return this.Fail(instance, "真实 BgPart GraphicsObject=null。");

            var standaloneAddress = ParseRequiredAddress(instance.ObjectAddress);
            instance.LastDump = string.Join(Environment.NewLine, new[]
            {
                "Standalone vs 真实 BgPart scene attach / parent-child 对比",
                BuildSceneAttachStateDump(standaloneAddress, "Standalone"),
                BuildSceneAttachStateDump(realGraphics, $"真实 BgPart {bgPart.ResourcePath}"),
                BuildSceneAttachDiff(standaloneAddress, realGraphics),
            });
            instance.LastOperation = "CompareSceneAttach";
            this.RefreshAttachState(instance, fullScan: false);
            instance.LastError = string.Empty;
            this.LastCreateResult = "已生成 Standalone 与当前选中真实 BgPart 的 scene attach 对比。";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"scene attach 对比失败：{ex.Message}");
        }
    }

    public bool DumpRawObjectLayoutComparison(string id, LayoutProbeInstance? bgPart)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        try
        {
            var standaloneAddress = ParseRequiredAddress(instance.ObjectAddress);
            var parts = new List<string>
            {
                "Standalone / BgPart raw object layout 只读取证",
                "字段布局来源：FFXIVClientStructs Graphics.Scene.Object / DrawObject / BgObject",
                BuildFieldOffsetReference(),
                BuildRawObjectLayoutDump(standaloneAddress, "Standalone current"),
            };

            if (bgPart != null
                && TryGetLayoutPointer(bgPart.Address, out var pointer)
                && pointer != null)
            {
                var bgLayout = (BgPartsLayoutInstance*)pointer;
                var realGraphics = (nint)bgLayout->GraphicsObject;
                if (realGraphics != 0)
                {
                    parts.Add(BuildRawObjectLayoutDump(realGraphics, $"真实 BgPart {bgPart.ResourcePath}"));
                    parts.Add(BuildRawObjectLayoutDiff(standaloneAddress, realGraphics, "Standalone", "RealBgPart"));
                }
                else
                {
                    parts.Add("真实 BgPart GraphicsObject=null，无法做 raw diff。");
                }
            }
            else
            {
                parts.Add("未选择真实 BgPart，当前只输出 Standalone raw layout。");
            }

            parts.Add("说明：AddChild/OnAddedToWorld 写入已暂停，当前 raw dump 用于确认 objectFlags/read offset 是否可信，以及观察既有异常字段状态；不会执行任何 scene 链表写入。");
            instance.RawObjectLayoutDump = string.Join(Environment.NewLine, parts);
            instance.LastDump = instance.RawObjectLayoutDump;
            instance.LastOperation = "DumpRawObjectLayout";
            instance.LastError = string.Empty;
            this.LastCreateResult = "已生成 Standalone raw object layout / offset 只读取证。";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"raw object layout dump 失败：{ex.Message}");
        }
    }

    public bool ExecuteSceneAttachStep(
        string id,
        string stepName,
        LayoutProbeInstance? referenceBgPart,
        bool unsafeEnabled,
        bool confirmed,
        bool sceneAttachConfirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        return this.FailSceneAttach(instance, stepName, SceneAttachWritePausedReason);

#pragma warning disable CS0162
        var blockReason = this.GetSceneAttachBlockReason(instance, unsafeEnabled, confirmed, sceneAttachConfirmed);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.FailSceneAttach(instance, stepName, blockReason);

        try
        {
            var standaloneAddress = ParseRequiredAddress(instance.ObjectAddress);
            var standalone = (SceneObject*)standaloneAddress;
            var before = BuildSceneAttachStateDump(standaloneAddress, $"scene attach before {stepName}");
            this.RefreshAttachState(instance, fullScan: false);
            if (stepName == "AddChildToRealBgPartParent")
            {
                if (instance.AttachState == StandaloneAttachState.LinkedAndContained)
                    return this.FailSceneAttach(instance, stepName, "AddChild 已禁止：当前 Standalone 已经能从 parent child list 遍历到，不允许重复 AddChild。");
                if (instance.AttachState is not (StandaloneAttachState.Detached or StandaloneAttachState.LinkedButNotContained))
                    return this.FailSceneAttach(instance, stepName, $"AddChild 已禁止：当前 attachState={instance.AttachState}，需要 Detached 或 LinkedButNotContained。");
            }

            string result;

            switch (stepName)
            {
                case "OnAddedToWorld":
                    standalone->OnAddedToWorld();
                    result = "已调用 Object.OnAddedToWorld()。";
                    break;
                case "OnAddedToWorldUpdateChain":
                    standalone->OnAddedToWorld();
                    result = this.InvokeStandaloneUpdateChain((SceneBgObject*)standalone, instance, "OnAddedToWorld -> update chain");
                    break;
                case "AddChildToRealBgPartParent":
                    result = this.ExecuteAddChildToReferenceParent(referenceBgPart, standalone);
                    if (result.StartsWith("已调用", StringComparison.Ordinal))
                    {
                        standalone->OnAddedToWorld();
                        result += "; " + this.InvokeStandaloneUpdateChain((SceneBgObject*)standalone, instance, "AddChild -> OnAddedToWorld -> update chain");
                    }

                    break;
                default:
                    return this.FailSceneAttach(instance, stepName, $"未知 scene attach step：{stepName}");
            }

            instance.SceneAttachStep = stepName;
            instance.SceneAttachResult = result;
            instance.SceneAttachException = string.Empty;
            instance.LastOperation = "SceneAttachStep";
            instance.LastDump = string.Join(Environment.NewLine, new[]
            {
                before,
                BuildSceneAttachStateDump(standaloneAddress, $"scene attach after {stepName}"),
            });
            this.LastCreateResult = $"{stepName} 完成：{result}";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            return this.FailSceneAttach(instance, stepName, ex.ToString());
        }
    }

#pragma warning restore CS0162
    public bool HideOrRemove(string id, bool unsafeEnabled, bool confirmed)
    {
        var instance = this.GetById(id);
        if (instance == null)
        {
            this.LastError = "未找到选中的 Standalone 对象。";
            return false;
        }

        if (!instance.CanWritePosition)
        {
            instance.State = StandaloneObjectState.LeakedUnmanaged;
            instance.IsValid = false;
            instance.IsVisible = false;
            instance.LastOperation = "RemoveUnvalidated";
            instance.LastError = "Standalone 对象尚未通过只读验证，未调用任何 native 隐藏/销毁；仅标记为 unmanaged/leaked，切图可能自动清理。";
            this.LastCreateResult = instance.LastError;
            return true;
        }

        var blockReason = this.GetWriteBlockReason(instance, unsafeEnabled, confirmed, requirePositionSuccess: false);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        try
        {
            var address = ParseRequiredAddress(instance.ObjectAddress);
            var obj = (SceneObject*)address;
            var bg = (SceneBgObject*)address;
            obj->Position = new Vector3(obj->Position.X, -5000f, obj->Position.Z);
            bg->IsVisible = false;
            instance.Position = obj->Position;
            instance.IsVisible = false;
            instance.IsValid = false;
            instance.State = StandaloneObjectState.Hidden;
            instance.ManualHiddenConfirmed = false;
            instance.LastOperation = "HideOnly";
            instance.LastError = "未调用 CleanupRender/Dtor；当前只是隐藏并移动到地下，仍有泄漏风险。";
            this.LastCreateResult = $"已隐藏 Standalone 对象：{instance.ObjectAddress}。未调用 CleanupRender/Dtor。";
            return true;
        }
        catch (Exception ex)
        {
            instance.State = StandaloneObjectState.Invalid;
            return this.Fail(instance, $"隐藏 Standalone 对象失败：{ex.Message}");
        }
    }

    public void HideAll()
    {
        foreach (var instance in this.instances)
        {
            instance.State = StandaloneObjectState.LeakedUnmanaged;
            instance.IsValid = false;
            instance.IsVisible = false;
            instance.LastOperation = "MarkAllUnmanaged";
            instance.LastError = "未执行 native 隐藏/销毁；仅标记为 unmanaged/leaked。";
        }

        this.LastCreateResult = "已将全部 Standalone 记录标记为 unmanaged/leaked，未执行 native 写入。";
    }

    public void HideAll(bool unsafeEnabled, bool confirmed)
    {
        foreach (var instance in this.instances.ToList())
            this.HideOrRemove(instance.Id, unsafeEnabled, confirmed);
    }

    public void MarkAllInvalid(string reason)
    {
        foreach (var instance in this.instances)
        {
            instance.IsValid = false;
            instance.IsVisible = false;
            instance.State = StandaloneObjectState.Invalid;
            instance.LastOperation = "Marked invalid";
            instance.LastError = reason;
        }

        this.LastCreateResult = reason;
    }

    public bool ForceRemove(string id)
    {
        var removed = this.instances.RemoveAll(instance => string.Equals(instance.Id, id, StringComparison.OrdinalIgnoreCase));
        this.LastCreateResult = removed > 0 ? $"已从列表移除 Standalone 记录：{id}" : $"列表中没有记录：{id}";
        return removed > 0;
    }

    private bool Dump(StandaloneObjectInstance instance)
    {
        try
        {
            instance.LastDump = this.BuildInstanceDump(instance, "manual dump");
            instance.LastOperation = "DumpReadOnly";
            instance.LastError = string.Empty;
            this.LastCreateResult = $"已只读 dump Standalone 对象：{instance.ObjectAddress}";
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            instance.State = StandaloneObjectState.Invalid;
            return this.Fail(instance, $"Dump Standalone 对象失败：{ex.Message}");
        }
    }

    private bool ValidateReadOnly(StandaloneObjectInstance instance, bool automatic)
    {
        instance.ValidateAttempts++;
        try
        {
            if (!this.TryReadSnapshot(instance, out var snapshot, out var reason))
            {
                instance.StableReadFrames = 0;
                instance.ValidationStatus = $"只读验证未通过：{reason}";
                if (instance.ValidateAttempts >= instance.MaxValidateAttempts)
                {
                    instance.State = StandaloneObjectState.Invalid;
                    instance.IsValid = false;
                    instance.LastError = $"只读验证超时：{reason}";
                }
                else
                {
                    instance.State = StandaloneObjectState.WaitingValidate;
                    instance.ValidateFrameWait = 1;
                }

                return false;
            }

            this.ApplySnapshot(instance, snapshot);
            instance.StableReadFrames++;
            instance.ValidationStatus = $"只读验证稳定帧：{instance.StableReadFrames}/{RequiredStableReadFrames}; visible={snapshot.Visible}; loadState={snapshot.LoadState}";
            instance.LastDump = automatic
                ? this.BuildInstanceLightDump(instance, "auto validate")
                : this.BuildInstanceDump(instance, "manual validate");

            if (instance.StableReadFrames < RequiredStableReadFrames)
            {
                instance.State = StandaloneObjectState.WaitingValidate;
                instance.ValidateFrameWait = 1;
                return false;
            }

            if (!snapshot.Visible)
            {
                instance.State = StandaloneObjectState.NeedSceneRegistration;
                instance.IsValid = true;
                instance.LastError = "对象可读且资源稳定，但 visible=false；可能缺少 OnAddedToWorld / scene root / culling registration。禁止 transform 写入。";
                this.LastCreateResult = instance.LastError;
                return false;
            }

            instance.State = StandaloneObjectState.ValidatedReadOnly;
            instance.IsValid = true;
            instance.LastError = string.Empty;
            instance.LastOperation = automatic ? "AutoValidateReadOnly" : "ManualValidateReadOnly";
            this.LastError = string.Empty;
            this.LastCreateResult = $"Standalone 对象只读验证通过：{instance.ObjectAddress}; state={instance.State}; transform 写入按钮已可手动尝试。";
            return true;
        }
        catch (Exception ex)
        {
            instance.State = StandaloneObjectState.Invalid;
            instance.IsValid = false;
            return this.Fail(instance, $"只读验证异常：{ex.Message}");
        }
    }

    private bool TryReadSnapshot(StandaloneObjectInstance instance, out StandaloneSnapshot snapshot, out string reason)
    {
        snapshot = default;
        reason = string.Empty;
        if (!TryParseAddress(instance.ObjectAddress, out var address) || address == 0)
        {
            reason = $"objectAddress 无效：{instance.ObjectAddress}";
            return false;
        }

        if (!IsReasonableProcessAddress(address))
        {
            reason = $"objectAddress 不在合理进程地址范围：0x{address:X}";
            return false;
        }

        var vtable = SafeReadPointer(address);
        if (vtable == 0)
        {
            reason = "vtable=0";
            return false;
        }

        if (!IsPointerInMainModule(vtable))
        {
            reason = $"vtable 不在 ffxiv_dx11/main module 可执行范围：0x{vtable:X}";
            return false;
        }

        var bg = (SceneBgObject*)address;
        if (bg->ModelResourceHandle == null)
        {
            reason = "ModelResourceHandle=null";
            return false;
        }

        var loadState = bg->ModelResourceHandle->LoadState;
        if (loadState < 7)
        {
            reason = $"LoadState={loadState}，等待 >= 7。";
            return false;
        }

        var obj = (SceneObject*)address;
        var position = obj->Position;
        var rotation = obj->Rotation;
        var scale = obj->Scale;
        if (!IsTransformNormal(position, rotation, scale))
        {
            reason = $"Transform 数值异常：{FormatTransform(position, rotation, scale)}";
            return false;
        }

        snapshot = new StandaloneSnapshot(
            address,
            vtable,
            (nint)bg->ModelResourceHandle,
            SafeRead(() => bg->ModelResourceHandle->FileName.ToString()),
            loadState,
            SafeReadBool(() => bg->IsVisible),
            SafeReadBool(() => bg->IsTransformChanged),
            position,
            rotation,
            scale,
            BuildSceneLinkDump(address));
        return true;
    }

    private void ApplySnapshot(StandaloneObjectInstance instance, StandaloneSnapshot snapshot)
    {
        instance.VTableReadback = $"0x{snapshot.VTable:X}";
        instance.ModelResourceHandleAddress = $"0x{snapshot.ModelResourceHandle:X}";
        instance.ModelResourcePathReadback = snapshot.ModelPath;
        instance.LoadStateReadback = snapshot.LoadState.ToString();
        instance.IsVisible = snapshot.Visible;
        instance.Position = snapshot.Position;
        instance.Rotation = snapshot.Rotation;
        instance.Scale = snapshot.Scale;
        instance.TransformReadback = FormatTransform(snapshot.Position, snapshot.Rotation, snapshot.Scale);
        instance.SceneLinkReadback = snapshot.SceneLinks;
    }

    private string BuildInstanceDump(StandaloneObjectInstance instance, string label)
    {
        if (!TryParseAddress(instance.ObjectAddress, out var address) || address == 0)
            return $"{label}: invalid objectAddress={instance.ObjectAddress}";

        return BuildBgObjectDump(address, $"{label} | id={instance.Id} | state={instance.State}");
    }

    private string BuildInstanceLightDump(StandaloneObjectInstance instance, string label)
    {
        if (!TryParseAddress(instance.ObjectAddress, out var address) || address == 0)
            return $"{label}: invalid objectAddress={instance.ObjectAddress}";

        try
        {
            var bg = (SceneBgObject*)address;
            var obj = (SceneObject*)address;
            return string.Join(Environment.NewLine, new[]
            {
                $"{label} | id={instance.Id} | state={instance.State}",
                $"object=0x{address:X}; vtable=0x{SafeReadPointer(address):X}",
                $"modelHandle={(bg->ModelResourceHandle == null ? "0x0" : $"0x{(nint)bg->ModelResourceHandle:X}")}",
                $"path={SafeRead(() => bg->ModelResourceHandle == null ? string.Empty : bg->ModelResourceHandle->FileName.ToString())}",
                $"loadState={SafeRead(() => bg->ModelResourceHandle == null ? "null" : bg->ModelResourceHandle->LoadState.ToString())}",
                $"visible={SafeReadBool(() => bg->IsVisible)}; isTransformChanged={SafeReadBool(() => bg->IsTransformChanged)}",
                $"transform={FormatTransform(obj->Position, obj->Rotation, obj->Scale)}",
                $"scene links: parent=0x{(nint)obj->ParentObject:X}; child=0x{(nint)obj->ChildObject:X}; prev=0x{(nint)obj->PreviousSiblingObject:X}; next=0x{(nint)obj->NextSiblingObject:X}",
            });
        }
        catch (Exception ex)
        {
            return $"{label}: light dump failed: {ex.Message}";
        }
    }

    private string GetWriteBlockReason(StandaloneObjectInstance instance, bool unsafeEnabled, bool confirmed, bool requirePositionSuccess)
    {
        if (!unsafeEnabled)
            return "Unsafe/native 写入未启用。";
        if (!confirmed)
            return "需要勾选 Standalone BgObject 高风险实验确认。";
        if (!instance.IsValid)
            return "Standalone 对象已标记无效。";
        if (requirePositionSuccess && !instance.CanWriteRotationScale)
            return "必须先成功执行 Position 单字段写入，才允许 Rotation/Scale 实验。";
        if (!requirePositionSuccess && !instance.CanWritePosition)
            return $"当前状态不允许写入：{instance.State}。需要 ValidatedReadOnly。";

        if (!this.TryReadSnapshot(instance, out _, out var reason))
            return $"写入前只读验证失败：{reason}";

        return string.Empty;
    }

    private string GetActivationBlockReason(StandaloneObjectInstance instance, bool unsafeEnabled, bool confirmed)
    {
        if (!unsafeEnabled)
            return "Unsafe/native 写入未启用。";
        if (!confirmed)
            return "需要勾选 Standalone BgObject 高风险实验确认。";
        if (!instance.IsValid)
            return "Standalone 对象已标记无效。";
        if (instance.State is not (StandaloneObjectState.ValidatedReadOnly or StandaloneObjectState.PositionWriteSucceeded))
            return $"当前状态不允许 activation step：{instance.State}。需要 ValidatedReadOnly。";
        if (!this.TryReadSnapshot(instance, out _, out var reason))
            return $"activation 前只读验证失败：{reason}";
        return string.Empty;
    }

    private bool FailActivation(StandaloneObjectInstance instance, string stepName, string message)
    {
        instance.ActivationStep = stepName;
        instance.ActivationResult = string.Empty;
        instance.ActivationException = message;
        return this.Fail(instance, $"{stepName} 失败：{message}");
    }

    private bool FailSceneAttach(StandaloneObjectInstance instance, string stepName, string message)
    {
        instance.SceneAttachStep = stepName;
        instance.SceneAttachResult = string.Empty;
        instance.SceneAttachException = message;
        return this.Fail(instance, $"{stepName} 失败：{message}");
    }

    private bool Fail(StandaloneObjectInstance instance, string message)
    {
        instance.LastError = message;
        instance.LastOperation = "Failed";
        this.LastError = message;
        this.LastCreateResult = message;
        return false;
    }

    private string GetSceneAttachBlockReason(
        StandaloneObjectInstance instance,
        bool unsafeEnabled,
        bool confirmed,
        bool sceneAttachConfirmed)
    {
        if (!unsafeEnabled)
            return "Unsafe/native 写入未启用。";
        if (!confirmed)
            return "需要勾选 Standalone BgObject 高风险实验确认。";
        if (!sceneAttachConfirmed)
            return "需要勾选 scene attach / AddChild 极高风险确认。";
        if (!instance.IsValid)
            return "Standalone 对象已标记无效。";
        if (instance.State is not (StandaloneObjectState.ValidatedReadOnly or StandaloneObjectState.PositionWriteSucceeded))
            return $"当前状态不允许 scene attach 实验：{instance.State}。需要 ValidatedReadOnly 或 PositionWriteSucceeded。";
        if (!this.TryReadSnapshot(instance, out _, out var reason))
            return $"scene attach 前只读验证失败：{reason}";
        return string.Empty;
    }

    private string ExecuteAddChildToReferenceParent(LayoutProbeInstance? referenceBgPart, SceneObject* standalone)
    {
        if (referenceBgPart == null)
            return "AddChild 跳过：没有选中的真实 BgPart parent 来源。";

        if (!TryGetLayoutPointer(referenceBgPart.Address, out var pointer) || pointer == null)
            return $"AddChild 跳过：真实 BgPart 地址解析失败：{referenceBgPart.Address}";

        var bgLayout = (BgPartsLayoutInstance*)pointer;
        var realGraphics = (nint)bgLayout->GraphicsObject;
        if (realGraphics == 0)
            return "AddChild 跳过：真实 BgPart GraphicsObject=null。";

        var realObject = (SceneObject*)realGraphics;
        var parent = realObject->ParentObject;
        if (parent == null)
            return "AddChild 跳过：真实 BgPart parent=null，不能推断 scene parent。";

        if (!IsLikelySceneObjectPointer(parent))
            return $"AddChild 跳过：真实 BgPart parent 指针不像 Scene.Object：0x{(nint)parent:X}";

        if (!IsLikelySceneObjectPointer(standalone))
            return $"AddChild 跳过：Standalone 指针不像 Scene.Object：0x{(nint)standalone:X}";

        if (standalone->PreviousSiblingObject == null || standalone->NextSiblingObject == null)
            return "AddChild 跳过：Standalone prev/next sibling 指针为空。AddChild 函数体会先解链旧 sibling ring，不能安全调用。";

        if (!IsLikelySceneObjectPointer(standalone->PreviousSiblingObject) || !IsLikelySceneObjectPointer(standalone->NextSiblingObject))
            return $"AddChild 跳过：Standalone sibling 指针不像 Scene.Object。prev=0x{(nint)standalone->PreviousSiblingObject:X}; next=0x{(nint)standalone->NextSiblingObject:X}";

        var parentAlreadyContains = ParentChildListContains(parent, standalone, out var scanBefore);
        if (parentAlreadyContains)
            return $"AddChild 未调用：真实 BgPart parent 已经能遍历到 Standalone。scan={scanBefore}";

        parent->AddChild(standalone);
        var parentContainsAfter = ParentChildListContains(parent, standalone, out var scanAfter);
        return $"已调用 parent->AddChild(standalone)。parent=0x{(nint)parent:X}; beforeContains={parentAlreadyContains}; afterContains={parentContainsAfter}; beforeScan={scanBefore}; afterScan={scanAfter}";
    }

    private static string GetCreateBlockReason(string modelPath, Vector3 position, Vector3 rotationEuler, Vector3 scale, bool unsafeEnabled, bool confirmed)
    {
        if (!unsafeEnabled)
            return "Unsafe/native 写入未启用。";
        if (!confirmed)
            return "需要勾选 Standalone BgObject 崩溃风险确认。";
        if (!IsSupportedMdlPath(modelPath))
            return "mdl path 必须以 bg/ 或 bgcommon/ 开头，并以 .mdl 结尾。";
        if (!IsVectorNormal(position) || !IsVectorNormal(rotationEuler) || !IsVectorNormal(scale))
            return "position / rotation / scale 数值异常。";
        if (Math.Abs(scale.X) <= 0.0001f || Math.Abs(scale.Y) <= 0.0001f || Math.Abs(scale.Z) <= 0.0001f)
            return "scale 不能为 0。";
        return string.Empty;
    }

    private static bool IsSupportedMdlPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        path = path.Trim();
        return path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
            && (path.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetLayoutPointer(string? raw, out ILayoutInstance* pointer)
    {
        pointer = null;
        if (!TryParseAddress(raw, out var address) || address == 0)
            return false;
        pointer = (ILayoutInstance*)address;
        return true;
    }

    private static nint ParseRequiredAddress(string raw)
        => TryParseAddress(raw, out var address) && address != 0 ? address : throw new InvalidOperationException($"地址解析失败：{raw}");

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

    private static string BuildBgObjectDump(nint graphicsAddress, string label)
    {
        try
        {
            var bg = (SceneBgObject*)graphicsAddress;
            var obj = (SceneObject*)graphicsAddress;
            var vtable = SafeReadPointer(graphicsAddress);
            var handle = bg->ModelResourceHandle == null ? "0x0" : $"0x{(nint)bg->ModelResourceHandle:X}";
            var path = SafeRead(() => bg->ModelResourceHandle == null ? string.Empty : bg->ModelResourceHandle->FileName.ToString());
            var loadState = SafeRead(() => bg->ModelResourceHandle == null ? "null" : bg->ModelResourceHandle->LoadState.ToString());
            var category = SafeRead(() => bg->ModelResourceHandle == null ? "null" : bg->ModelResourceHandle->Type.Category.ToString());
            return string.Join(Environment.NewLine, new[]
            {
                label,
                $"graphicsObject=0x{graphicsAddress:X}",
                $"vtable=0x{vtable:X}; vtableInMainModule={IsPointerInMainModule(vtable)}",
                $"modelHandle={handle}",
                $"modelPath={path}",
                $"loadState={loadState}",
                $"category={category}",
                $"visible={SafeReadBool(() => bg->IsVisible)}",
                $"isTransformChanged={SafeReadBool(() => bg->IsTransformChanged)}",
                $"objectFlags={SafeRead(() => obj->ObjectFlags.ToString())}",
                $"drawFlags={SafeRead(() => bg->Flags.ToString())}",
                $"outlineFlags={SafeRead(() => bg->OutlineFlags.ToString())}",
                $"transform={FormatTransform(obj->Position, obj->Rotation, obj->Scale)}",
                BuildRenderPointerDump(graphicsAddress),
                BuildBoundsDump(graphicsAddress),
                BuildSceneLinkDump(graphicsAddress),
                BuildSceneAttachIntegrityDump(graphicsAddress),
                BuildPointerCandidateDump(graphicsAddress),
            });
        }
        catch (Exception ex)
        {
            return $"BgObject dump 失败：{ex}";
        }
    }

    private static string BuildFieldOffsetReference()
        => string.Join(Environment.NewLine, new[]
        {
            "FFXIVClientStructs field offsets:",
            "- Object: vtable +0x00 (implicit); ParentObject +0x18; PreviousSiblingObject +0x20; NextSiblingObject +0x28; ChildObject +0x30; ObjectFlags +0x38; Position +0x50; Rotation +0x60; Scale +0x70; sizeof(Object)=0x80",
            "- DrawObject: Flags +0x88; OutlineFlags +0x89; sizeof(DrawObject)=0x90",
            "- BgObject: ModelResourceHandle +0x90; CachedTransformMatrices +0xA0; StainBuffer +0xA8; CachedTransform +0xB0; LoadedAnimationData +0xB8; sizeof(BgObject)=0xE0",
            "objectFlags 正式 offset 是 +0x38；如果该字段出现异常大值，优先怀疑 AddChild/OnAddedToWorld 调用污染了对象链路或 ObjectFlags 内的 world/list 指针位，而不是随意换 offset。",
        });

    private static string BuildRawObjectLayoutDump(nint address, string label)
    {
        try
        {
            var bg = (SceneBgObject*)address;
            var obj = (SceneObject*)address;
            var bytes = ReadBytes(address, 0xC0);
            return string.Join(Environment.NewLine, new[]
            {
                $"[{label}] raw +0x00~+0xBF",
                $"address=0x{address:X}; vtable=0x{SafeReadPointer(address):X}; vtableInMainModule={IsPointerInMainModule(SafeReadPointer(address))}",
                $"Parent@+0x18=0x{(nint)obj->ParentObject:X}; Prev@+0x20=0x{(nint)obj->PreviousSiblingObject:X}; Next@+0x28=0x{(nint)obj->NextSiblingObject:X}; Child@+0x30=0x{(nint)obj->ChildObject:X}",
                $"ObjectFlags@+0x38=0x{obj->ObjectFlags:X16} ({obj->ObjectFlags}); lowBits={obj->ObjectFlags & 0xF:X}; rootCandidate={GetObjectFlagsRootCandidate(obj)}",
                $"Position@+0x50={FormatVector(obj->Position)}",
                $"Rotation@+0x60={obj->Rotation}",
                $"Scale@+0x70={FormatVector(obj->Scale)}",
                $"DrawFlags@+0x88=0x{bg->Flags:X2}; OutlineFlags@+0x89=0x{bg->OutlineFlags:X2}",
                $"ModelResourceHandle@+0x90={(bg->ModelResourceHandle == null ? "0x0" : $"0x{(nint)bg->ModelResourceHandle:X}")}; path={SafeRead(() => bg->ModelResourceHandle == null ? string.Empty : bg->ModelResourceHandle->FileName.ToString())}; loadState={SafeRead(() => bg->ModelResourceHandle == null ? "null" : bg->ModelResourceHandle->LoadState.ToString())}",
                $"CachedTransformMatrices@+0xA0={ReadPointerAt(address, 0xA0)}; StainBuffer@+0xA8={ReadPointerAt(address, 0xA8)}; CachedTransform@+0xB0={ReadPointerAt(address, 0xB0)}; LoadedAnimationData@+0xB8={ReadPointerAt(address, 0xB8)}",
                FormatBytes(bytes, address),
            });
        }
        catch (Exception ex)
        {
            return $"[{label}] raw layout dump failed: {ex}";
        }
    }

    private static string BuildRawObjectLayoutDiff(nint leftAddress, nint rightAddress, string leftName, string rightName)
    {
        try
        {
            var left = ReadBytes(leftAddress, 0xC0);
            var right = ReadBytes(rightAddress, 0xC0);
            var diffs = new List<string>
            {
                $"raw diff +0x00~+0xBF: {leftName}=0x{leftAddress:X}; {rightName}=0x{rightAddress:X}",
            };

            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                if (left[i] == right[i])
                    continue;
                diffs.Add($"+0x{i:X3}: {leftName}=0x{left[i]:X2}; {rightName}=0x{right[i]:X2}");
                if (diffs.Count >= 129)
                {
                    diffs.Add("... diff truncated after 128 bytes");
                    break;
                }
            }

            if (diffs.Count == 1)
                diffs.Add("no byte diff in scanned range");

            return string.Join(Environment.NewLine, diffs);
        }
        catch (Exception ex)
        {
            return $"raw layout diff failed: {ex.Message}";
        }
    }

    private static byte[] ReadBytes(nint address, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = *(byte*)(address + i);
        return bytes;
    }

    private static string FormatBytes(byte[] bytes, nint baseAddress)
    {
        var lines = new List<string> { "raw bytes:" };
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var slice = bytes.Skip(offset).Take(16);
            lines.Add($"+0x{offset:X3}  " + string.Join(" ", slice.Select(value => value.ToString("X2"))));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCompactRenderState(nint graphicsAddress, string label)
    {
        var bg = (SceneBgObject*)graphicsAddress;
        var obj = (SceneObject*)graphicsAddress;
        return string.Join(Environment.NewLine, new[]
        {
            $"[{label}] address=0x{graphicsAddress:X}",
            $"vtable=0x{SafeReadPointer(graphicsAddress):X}",
            $"modelHandle={(bg->ModelResourceHandle == null ? "0x0" : $"0x{(nint)bg->ModelResourceHandle:X}")}",
            $"path={SafeRead(() => bg->ModelResourceHandle == null ? string.Empty : bg->ModelResourceHandle->FileName.ToString())}",
            $"loadState={SafeRead(() => bg->ModelResourceHandle == null ? "null" : bg->ModelResourceHandle->LoadState.ToString())}",
            $"visible={SafeReadBool(() => bg->IsVisible)}",
            $"isTransformChanged={SafeReadBool(() => bg->IsTransformChanged)}",
            $"objectFlags={SafeRead(() => obj->ObjectFlags.ToString())}",
            $"drawFlags={SafeRead(() => bg->Flags.ToString())}",
            $"outlineFlags={SafeRead(() => bg->OutlineFlags.ToString())}",
            $"transform={FormatTransform(obj->Position, obj->Rotation, obj->Scale)}",
            BuildRenderPointerDump(graphicsAddress),
            BuildBoundsDump(graphicsAddress),
            BuildSceneLinkDump(graphicsAddress),
            BuildSceneAttachIntegrityDump(graphicsAddress),
        });
    }

    private static string BuildRenderStateDiff(nint standaloneAddress, nint realAddress)
    {
        var standalone = ReadComparableRenderState(standaloneAddress);
        var real = ReadComparableRenderState(realAddress);
        var diffs = new List<string> { "差异：" };
        AddDiff(diffs, "vtable", standalone.VTable, real.VTable);
        AddDiff(diffs, "objectFlags", standalone.ObjectFlags, real.ObjectFlags);
        AddDiff(diffs, "drawFlags", standalone.DrawFlags, real.DrawFlags);
        AddDiff(diffs, "outlineFlags", standalone.OutlineFlags, real.OutlineFlags);
        AddDiff(diffs, "visible", standalone.Visible, real.Visible);
        AddDiff(diffs, "parent", standalone.Parent, real.Parent);
        AddDiff(diffs, "child", standalone.Child, real.Child);
        AddDiff(diffs, "prev", standalone.Prev, real.Prev);
        AddDiff(diffs, "next", standalone.Next, real.Next);
        AddDiff(diffs, "root", standalone.Root, real.Root);
        AddDiff(diffs, "parentContainsThis", standalone.ParentContainsThis, real.ParentContainsThis);
        AddDiff(diffs, "prevNextMatchesThis", standalone.PrevNextMatchesThis, real.PrevNextMatchesThis);
        AddDiff(diffs, "nextPrevMatchesThis", standalone.NextPrevMatchesThis, real.NextPrevMatchesThis);
        AddDiff(diffs, "objectFlagsRootCandidate", standalone.ObjectFlagsRootCandidate, real.ObjectFlagsRootCandidate);
        AddDiff(diffs, "bounds", standalone.Bounds, real.Bounds);
        AddDiff(diffs, "cachedMatrices", standalone.CachedMatrices, real.CachedMatrices);
        AddDiff(diffs, "cachedTransform", standalone.CachedTransform, real.CachedTransform);
        AddDiff(diffs, "stainOrBgChangeData", standalone.StainOrBgChangeData, real.StainOrBgChangeData);
        AddDiff(diffs, "animationData", standalone.AnimationData, real.AnimationData);
        diffs.Add(string.Equals(standalone.Root, real.Root, StringComparison.Ordinal) && standalone.Root != "0x0"
            ? $"- sameSceneRoot: true ({standalone.Root})"
            : $"- sameSceneRoot: false (Standalone={standalone.Root}; Real={real.Root})");
        return diffs.Count == 1 ? "差异：未发现可读字段差异。" : string.Join(Environment.NewLine, diffs);
    }

    private static ComparableRenderState ReadComparableRenderState(nint address)
    {
        var bg = (SceneBgObject*)address;
        var obj = (SceneObject*)address;
        return new ComparableRenderState(
            $"0x{SafeReadPointer(address):X}",
            SafeRead(() => obj->ObjectFlags.ToString()),
            SafeRead(() => bg->Flags.ToString()),
            SafeRead(() => bg->OutlineFlags.ToString()),
            SafeReadBool(() => bg->IsVisible).ToString(),
            $"0x{(nint)obj->ParentObject:X}",
            $"0x{(nint)obj->ChildObject:X}",
            $"0x{(nint)obj->PreviousSiblingObject:X}",
            $"0x{(nint)obj->NextSiblingObject:X}",
            GetRootAddress(obj),
            ParentChildListContains(obj->ParentObject, obj, out _) ? "true" : "false",
            PreviousNextMatches(obj),
            NextPreviousMatches(obj),
            GetObjectFlagsRootCandidate(obj),
            BuildBoundsDump(address).Replace("bounds: ", string.Empty, StringComparison.Ordinal),
            ReadPointerAt(address, CachedMatricesOffset),
            ReadPointerAt(address, CachedTransformOffset),
            ReadPointerAt(address, StainOrBgChangeDataOffset),
            ReadPointerAt(address, AnimationDataOffset));
    }

    private static void AddDiff(List<string> diffs, string name, string left, string right)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal))
            diffs.Add($"- {name}: Standalone={left}; Real={right}");
    }

    private static string BuildRenderPointerDump(nint graphicsAddress)
        => $"render pointers: cachedMatrices={ReadPointerAt(graphicsAddress, CachedMatricesOffset)}; stainOrBgChangeData={ReadPointerAt(graphicsAddress, StainOrBgChangeDataOffset)}; cachedTransform={ReadPointerAt(graphicsAddress, CachedTransformOffset)}; animationData={ReadPointerAt(graphicsAddress, AnimationDataOffset)}";

    private static string ReadPointerAt(nint address, int offset)
    {
        try
        {
            return $"0x{*(nint*)(address + offset):X}";
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    private static string BuildSceneLinkDump(nint graphicsAddress)
    {
        try
        {
            var obj = (SceneObject*)graphicsAddress;
            return $"scene links: parent=0x{(nint)obj->ParentObject:X}; child=0x{(nint)obj->ChildObject:X}; prev=0x{(nint)obj->PreviousSiblingObject:X}; next=0x{(nint)obj->NextSiblingObject:X}; parentChain={BuildParentChain(obj)}";
        }
        catch (Exception ex)
        {
            return $"scene links read failed: {ex.Message}";
        }
    }

    private static string BuildSceneAttachStateDump(nint graphicsAddress, string label)
    {
        try
        {
            var bg = (SceneBgObject*)graphicsAddress;
            var obj = (SceneObject*)graphicsAddress;
            return string.Join(Environment.NewLine, new[]
            {
                $"[{label}] object=0x{graphicsAddress:X}",
                $"vtable=0x{SafeReadPointer(graphicsAddress):X}; vtableInMainModule={IsPointerInMainModule(SafeReadPointer(graphicsAddress))}",
                $"visible={SafeReadBool(() => bg->IsVisible)}; isTransformChanged={SafeReadBool(() => bg->IsTransformChanged)}",
                $"objectFlags={SafeRead(() => obj->ObjectFlags.ToString())}; objectFlagsRootCandidate={GetObjectFlagsRootCandidate(obj)}",
                $"drawFlags={SafeRead(() => bg->Flags.ToString())}; outlineFlags={SafeRead(() => bg->OutlineFlags.ToString())}",
                $"transform={FormatTransform(obj->Position, obj->Rotation, obj->Scale)}",
                BuildRenderPointerDump(graphicsAddress),
                BuildBoundsDump(graphicsAddress),
                BuildSceneLinkDump(graphicsAddress),
                BuildSceneAttachIntegrityDump(graphicsAddress),
            });
        }
        catch (Exception ex)
        {
            return $"scene attach state read failed: {ex.Message}";
        }
    }

    private static string BuildSceneAttachDiff(nint standaloneAddress, nint realAddress)
    {
        var standalone = ReadComparableRenderState(standaloneAddress);
        var real = ReadComparableRenderState(realAddress);
        var diffs = new List<string> { "scene attach 差异：" };
        AddDiff(diffs, "parent", standalone.Parent, real.Parent);
        AddDiff(diffs, "child", standalone.Child, real.Child);
        AddDiff(diffs, "prev", standalone.Prev, real.Prev);
        AddDiff(diffs, "next", standalone.Next, real.Next);
        AddDiff(diffs, "root", standalone.Root, real.Root);
        AddDiff(diffs, "parentContainsThis", standalone.ParentContainsThis, real.ParentContainsThis);
        AddDiff(diffs, "prevNextMatchesThis", standalone.PrevNextMatchesThis, real.PrevNextMatchesThis);
        AddDiff(diffs, "nextPrevMatchesThis", standalone.NextPrevMatchesThis, real.NextPrevMatchesThis);
        AddDiff(diffs, "objectFlagsRootCandidate", standalone.ObjectFlagsRootCandidate, real.ObjectFlagsRootCandidate);
        diffs.Add(string.Equals(standalone.Root, real.Root, StringComparison.Ordinal) && standalone.Root != "0x0"
            ? $"- sameSceneRoot: true ({standalone.Root})"
            : $"- sameSceneRoot: false (Standalone={standalone.Root}; Real={real.Root})");
        return string.Join(Environment.NewLine, diffs);
    }

    private static string BuildSceneAttachIntegrityDump(nint graphicsAddress)
    {
        try
        {
            var obj = (SceneObject*)graphicsAddress;
            var parentContains = ParentChildListContains(obj->ParentObject, obj, out var parentScan);
            return string.Join("; ", new[]
            {
                $"link integrity: prev->next=this? {PreviousNextMatches(obj)}",
                $"next->prev=this? {NextPreviousMatches(obj)}",
                $"parent->child list contains this? {parentContains}",
                $"root={GetRootAddress(obj)}",
                $"parentChildScan={parentScan}",
            });
        }
        catch (Exception ex)
        {
            return $"link integrity read failed: {ex.Message}";
        }
    }

    private static string BuildBoundsDump(nint graphicsAddress)
    {
        try
        {
            var bg = (SceneBgObject*)graphicsAddress;
            if (bg->ModelResourceHandle == null)
                return "bounds: skipped, ModelResourceHandle=null";
            if (bg->ModelResourceHandle->LoadState < 7)
                return $"bounds: skipped, LoadState={bg->ModelResourceHandle->LoadState}";

            FFXIVClientStructs.FFXIV.Common.Math.SphereBounds bounds = default;
            bg->ComputeSphereBounds(&bounds);
            return $"bounds: center=({bounds.CenterPoint.X:F2}, {bounds.CenterPoint.Y:F2}, {bounds.CenterPoint.Z:F2}), radius={bounds.Radius:F2}";
        }
        catch (Exception ex)
        {
            return $"bounds read failed: {ex.Message}";
        }
    }

    private static string BuildPointerCandidateDump(nint graphicsAddress)
    {
        var entries = new List<string>();
        try
        {
            for (var offset = 0; offset <= 0xE0; offset += IntPtr.Size)
            {
                var candidate = *(nint*)(graphicsAddress + offset);
                if (candidate == 0)
                    continue;
                entries.Add($"+0x{offset:X2}=0x{candidate:X}");
                if (entries.Count >= 16)
                    break;
            }
        }
        catch (Exception ex)
        {
            entries.Add($"pointer scan failed: {ex.Message}");
        }

        return "pointer candidates: " + (entries.Count == 0 ? "none" : string.Join("; ", entries));
    }

    private static string BuildParentChain(SceneObject* obj)
    {
        var chain = new List<string>();
        try
        {
            var parent = obj->ParentObject;
            for (var i = 0; i < 12 && parent != null; i++)
            {
                chain.Add($"0x{(nint)parent:X}");
                if (!IsLikelySceneObjectPointer(parent))
                {
                    chain.Add("invalid-parent-pointer");
                    break;
                }

                parent = parent->ParentObject;
            }
        }
        catch
        {
            chain.Add("read failed");
        }

        return chain.Count == 0 ? "none" : string.Join(" -> ", chain);
    }

    private void RefreshAttachState(StandaloneObjectInstance instance, bool fullScan)
    {
        try
        {
            var address = ParseRequiredAddress(instance.ObjectAddress);
            var obj = (SceneObject*)address;
            var result = ScanParentChildList(
                obj->ParentObject,
                obj,
                fullScan ? FullParentChildScanMaxCount : DefaultParentChildScanMaxCount,
                fullScan ? FullParentChildScanMaxMs : DefaultParentChildScanMaxMs,
                includeChain: fullScan);
            ApplyAttachScanResult(instance, obj, result);
            if (fullScan)
                instance.FullParentChildScanDump = result.ChainDump;
        }
        catch (Exception ex)
        {
            instance.AttachState = StandaloneAttachState.Invalid;
            instance.ParentChildScanStatus = $"attach scan failed: {ex.Message}";
        }
    }

    private static void ApplyAttachScanResult(StandaloneObjectInstance instance, SceneObject* obj, ParentChildScanResult result)
    {
        instance.ParentChildScanCount = result.TotalScanned;
        instance.ParentChildScanHit = result.Hit;
        instance.ParentChildScanTruncated = result.Truncated;
        instance.ParentChildScanElapsedMs = (float)result.ElapsedMs;
        instance.ParentChildScanStatus = $"hit={result.Hit}; hitIndex={result.HitIndex}; scanned={result.TotalScanned}; truncated={result.Truncated}; endedBy={result.EndReason}; elapsed={result.ElapsedMs:F2}ms";
        instance.AttachState = ClassifyAttachState(obj, result);
    }

    private static StandaloneAttachState ClassifyAttachState(SceneObject* obj, ParentChildScanResult result)
    {
        if (obj == null || !IsLikelySceneObjectPointer(obj))
            return StandaloneAttachState.Invalid;

        var hasParent = obj->ParentObject != null && IsLikelySceneObjectPointer(obj->ParentObject);
        var hasPrevious = obj->PreviousSiblingObject != null && IsLikelySceneObjectPointer(obj->PreviousSiblingObject);
        var hasNext = obj->NextSiblingObject != null && IsLikelySceneObjectPointer(obj->NextSiblingObject);
        if (!hasParent || !hasPrevious || !hasNext)
            return StandaloneAttachState.Detached;

        return result.Hit ? StandaloneAttachState.LinkedAndContained : StandaloneAttachState.LinkedButNotContained;
    }

    private static bool ParentChildListContains(SceneObject* parent, SceneObject* target, out string scan)
    {
        var result = ScanParentChildList(parent, target, DefaultParentChildScanMaxCount, DefaultParentChildScanMaxMs, includeChain: true);
        scan = result.ToSummary();
        return result.Hit;
    }

    private static ParentChildScanResult ScanParentChildList(
        SceneObject* parent,
        SceneObject* target,
        int maxCount,
        double maxMs,
        bool includeChain)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var addresses = includeChain ? new List<string>(Math.Min(maxCount, 128)) : null;
        var visited = new HashSet<nint>();
        if (parent == null)
            return ParentChildScanResult.Empty("ParentNull", stopwatch.Elapsed.TotalMilliseconds);

        if (!IsLikelySceneObjectPointer(parent))
            return ParentChildScanResult.Empty($"InvalidParent:0x{(nint)parent:X}", stopwatch.Elapsed.TotalMilliseconds);

        var first = parent->ChildObject;
        if (first == null)
            return ParentChildScanResult.Empty("EmptyChildList", stopwatch.Elapsed.TotalMilliseconds);

        var current = first;
        for (var i = 0; current != null; i++)
        {
            if (i >= maxCount)
                return new ParentChildScanResult(false, -1, i, true, false, false, true, "CountLimit", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i));

            if (stopwatch.Elapsed.TotalMilliseconds > maxMs)
                return new ParentChildScanResult(false, -1, i, true, false, false, true, "TimeLimit", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i));

            var currentAddress = (nint)current;
            if (!IsReasonableProcessAddress(currentAddress))
                return new ParentChildScanResult(false, -1, i, false, false, false, false, $"InvalidAddress:0x{currentAddress:X}", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i));

            if (!visited.Add(currentAddress))
                return new ParentChildScanResult(false, -1, i, false, false, true, false, $"Cycle:0x{currentAddress:X}", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i));

            addresses?.Add($"0x{currentAddress:X}");
            if (current == target)
                return new ParentChildScanResult(true, i, i + 1, false, false, false, false, "Hit", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i + 1));

            if (!IsLikelySceneObjectPointer(current))
                return new ParentChildScanResult(false, -1, i + 1, false, false, false, false, $"InvalidObject:0x{currentAddress:X}", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i + 1));

            var next = current->NextSiblingObject;
            if (next == null)
                return new ParentChildScanResult(false, -1, i + 1, false, true, false, false, "EndedByNull", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i + 1));

            if (next == first)
                return new ParentChildScanResult(false, -1, i + 1, false, false, true, false, "ReturnedToStart", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, i + 1));

            current = next;
        }

        return new ParentChildScanResult(false, -1, visited.Count, false, true, false, false, "EndedByNull", stopwatch.Elapsed.TotalMilliseconds, FormatChain(addresses, visited.Count));
    }

    private static string FormatChain(List<string>? addresses, int scanned)
    {
        if (addresses == null || addresses.Count == 0)
            return string.Empty;

        var shown = addresses.Take(64).ToList();
        return string.Join(" -> ", shown) + (addresses.Count > shown.Count || scanned > shown.Count ? $" ... ({scanned} scanned)" : string.Empty);
    }

    private static string PreviousNextMatches(SceneObject* obj)
    {
        try
        {
            var previous = obj->PreviousSiblingObject;
            if (previous == null)
                return "prev=null";
            if (!IsLikelySceneObjectPointer(previous))
                return $"prev invalid: 0x{(nint)previous:X}";
            return previous->NextSiblingObject == obj ? "true" : $"false, prev->next=0x{(nint)previous->NextSiblingObject:X}";
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    private static string NextPreviousMatches(SceneObject* obj)
    {
        try
        {
            var next = obj->NextSiblingObject;
            if (next == null)
                return "next=null";
            if (!IsLikelySceneObjectPointer(next))
                return $"next invalid: 0x{(nint)next:X}";
            return next->PreviousSiblingObject == obj ? "true" : $"false, next->prev=0x{(nint)next->PreviousSiblingObject:X}";
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    private static string GetRootAddress(SceneObject* obj)
    {
        try
        {
            var current = obj;
            for (var i = 0; i < 32 && current != null; i++)
            {
                var parent = current->ParentObject;
                if (parent == null)
                    return $"0x{(nint)current:X}";
                if (!IsLikelySceneObjectPointer(parent))
                    return $"invalid-parent:0x{(nint)parent:X}";
                current = parent;
            }

            return "too-deep";
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    private static string GetObjectFlagsRootCandidate(SceneObject* obj)
    {
        try
        {
            var raw = obj->ObjectFlags & ~3UL;
            return $"0x{raw:X}";
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    private static bool IsLikelySceneObjectPointer(SceneObject* obj)
        => obj != null
            && IsReasonableProcessAddress((nint)obj)
            && IsPointerInMainModule(SafeReadPointer((nint)obj));

    private static nint SafeReadPointer(nint address)
    {
        try
        {
            return *(nint*)address;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsPointerInMainModule(nint address)
    {
        if (address == 0)
            return false;

        foreach (var (start, end) in MainModuleRanges.Value)
        {
            if (address >= start && address < end)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<(nint Start, nint End)> BuildMainModuleRanges()
    {
        var ranges = new List<(nint Start, nint End)>();
        try
        {
            foreach (System.Diagnostics.ProcessModule module in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                var name = module.ModuleName ?? string.Empty;
                if (!name.Contains("ffxiv_dx11", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("ffxiv", StringComparison.OrdinalIgnoreCase))
                    continue;

                var start = module.BaseAddress;
                ranges.Add((start, start + module.ModuleMemorySize));
            }
        }
        catch
        {
        }

        return ranges;
    }

    private static bool IsReasonableProcessAddress(nint address)
        => address.ToInt64() > 0x10000 && address.ToInt64() < 0x0000800000000000;

    private static string ReadLayoutTransform(ILayoutInstance* pointer)
    {
        try
        {
            var transform = pointer->GetTransformImpl();
            return transform == null ? "layout transform=null" : FormatTransform(transform->Translation, transform->Rotation, transform->Scale);
        }
        catch (Exception ex)
        {
            return $"layout transform read failed: {ex.Message}";
        }
    }

    private static Vector3 NormalizeScale(Vector3 scale)
        => new(
            Math.Abs(scale.X) <= 0.0001f ? 1f : scale.X,
            Math.Abs(scale.Y) <= 0.0001f ? 1f : scale.Y,
            Math.Abs(scale.Z) <= 0.0001f ? 1f : scale.Z);

    private static bool IsTransformNormal(Vector3 position, Quaternion rotation, Vector3 scale)
        => IsVectorNormal(position)
            && IsQuaternionNormal(rotation)
            && IsVectorNormal(scale)
            && Math.Abs(scale.X) > 0.0001f
            && Math.Abs(scale.Y) > 0.0001f
            && Math.Abs(scale.Z) > 0.0001f;

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

    private static string FormatTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        => $"position=({FormatVector(position)}), rotation={rotation}, scale=({FormatVector(scale)})";

    private static string FormatVector(Vector3 value)
        => $"X {value.X:F3}, Y {value.Y:F3}, Z {value.Z:F3}";

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    private static bool SafeReadBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }

    private static string Invoke(string label, Action action)
    {
        action();
        return $"{label} 成功";
    }

    private string InvokeComputeSphereBounds(SceneBgObject* bgObject, StandaloneObjectInstance instance)
    {
        if (bgObject->ModelResourceHandle == null)
            return "ComputeSphereBounds 跳过：ModelResourceHandle=null";

        if (bgObject->ModelResourceHandle->LoadState < 7)
            return $"ComputeSphereBounds 跳过：LoadState={bgObject->ModelResourceHandle->LoadState}";

        FFXIVClientStructs.FFXIV.Common.Math.SphereBounds bounds = default;
        bgObject->ComputeSphereBounds(&bounds);
        instance.BoundsReadback = $"center=({bounds.CenterPoint.X:F2}, {bounds.CenterPoint.Y:F2}, {bounds.CenterPoint.Z:F2}), radius={bounds.Radius:F2}";
        return $"ComputeSphereBounds 成功：{instance.BoundsReadback}";
    }

    private string InvokeComputeSphereBoundsThenUpdateCulling(SceneBgObject* bgObject, StandaloneObjectInstance instance)
    {
        var boundsResult = this.InvokeComputeSphereBounds(bgObject, instance);
        if (!boundsResult.Contains("成功", StringComparison.Ordinal))
            return $"{boundsResult}; UpdateCulling 已跳过。";

        bgObject->UpdateCulling();
        return $"{boundsResult}; UpdateCulling() 成功。";
    }

    private string InvokeBoundsCullingRebuild(SceneBgObject* bgObject, StandaloneObjectInstance instance)
    {
        var obj = (SceneObject*)bgObject;
        var positionBefore = obj->Position;
        bgObject->IsTransformChanged = true;
        bgObject->NotifyTransformChanged();
        bgObject->UpdateTransforms(true);
        var boundsResult = this.InvokeComputeSphereBounds(bgObject, instance);
        var distance = TryParseBoundsCenterDistance(instance.BoundsReadback, positionBefore);
        if (!boundsResult.Contains("鎴愬姛", StringComparison.Ordinal))
            return $"bounds/culling rebuild：{boundsResult}; position={FormatVector(positionBefore)}";

        bgObject->UpdateCulling();
        bgObject->UpdateRender();
        return $"bounds/culling rebuild 完成：position={FormatVector(positionBefore)}; {instance.BoundsReadback}; bounds-position distance={distance}";
    }

    private string InvokeStandaloneUpdateChain(SceneBgObject* bgObject, StandaloneObjectInstance instance, string label)
    {
        bgObject->UpdateTransforms(true);
        var boundsResult = this.InvokeComputeSphereBounds(bgObject, instance);
        var cullingCalled = false;
        if (boundsResult.Contains("鎴愬姛", StringComparison.Ordinal))
        {
            bgObject->UpdateCulling();
            cullingCalled = true;
        }

        bgObject->UpdateRender();
        return $"{label} 完成：{boundsResult}; UpdateCulling={(cullingCalled ? "called" : "skipped")}; UpdateRender=called";
    }

    private static string TryParseBoundsCenterDistance(string bounds, Vector3 position)
    {
        try
        {
            var start = bounds.IndexOf("center=(", StringComparison.Ordinal);
            if (start < 0)
                return "unknown";
            start += "center=(".Length;
            var end = bounds.IndexOf(')', start);
            if (end <= start)
                return "unknown";

            var parts = bounds[start..end].Split(',');
            if (parts.Length != 3)
                return "unknown";

            if (!float.TryParse(parts[0].Trim(), out var x)
                || !float.TryParse(parts[1].Trim(), out var y)
                || !float.TryParse(parts[2].Trim(), out var z))
                return "unknown";

            return Vector3.Distance(new Vector3(x, y, z), position).ToString("F3");
        }
        catch
        {
            return "unknown";
        }
    }

    private string InvokeComputeSphereBoundsThenUpdateCullingThenUpdateRender(SceneBgObject* bgObject, StandaloneObjectInstance instance)
    {
        var cullingResult = this.InvokeComputeSphereBoundsThenUpdateCulling(bgObject, instance);
        if (!cullingResult.Contains("UpdateCulling() 成功", StringComparison.Ordinal))
            return $"{cullingResult}; UpdateRender 已跳过。";

        bgObject->UpdateRender();
        return $"{cullingResult}; UpdateRender() 成功。";
    }

    private readonly record struct StandaloneSnapshot(
        nint Address,
        nint VTable,
        nint ModelResourceHandle,
        string ModelPath,
        int LoadState,
        bool Visible,
        bool IsTransformChanged,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale,
        string SceneLinks);

    private readonly record struct ComparableRenderState(
        string VTable,
        string ObjectFlags,
        string DrawFlags,
        string OutlineFlags,
        string Visible,
        string Parent,
        string Child,
        string Prev,
        string Next,
        string Root,
        string ParentContainsThis,
        string PrevNextMatchesThis,
        string NextPrevMatchesThis,
        string ObjectFlagsRootCandidate,
        string Bounds,
        string CachedMatrices,
        string CachedTransform,
        string StainOrBgChangeData,
        string AnimationData);

    private readonly record struct ParentChildScanResult(
        bool Hit,
        int HitIndex,
        int TotalScanned,
        bool Truncated,
        bool EndedByNull,
        bool EndedByCycle,
        bool EndedByLimit,
        string EndReason,
        double ElapsedMs,
        string ChainDump)
    {
        public static ParentChildScanResult Empty(string reason, double elapsedMs)
            => new(false, -1, 0, false, reason.Contains("Null", StringComparison.OrdinalIgnoreCase), false, false, reason, elapsedMs, string.Empty);

        public string ToSummary()
            => $"hit={this.Hit}; hitIndex={this.HitIndex}; scanCount={this.TotalScanned}; truncated={this.Truncated}; endedBy={this.EndReason}; elapsedMs={this.ElapsedMs:F2}; chain={(string.IsNullOrWhiteSpace(this.ChainDump) ? "not expanded" : this.ChainDump)}";
    }
}
