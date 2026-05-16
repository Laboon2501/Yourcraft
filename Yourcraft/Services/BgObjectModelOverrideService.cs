using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Common.Math;
using Yourcraft.Models;

namespace Yourcraft.Services;

public sealed unsafe class BgObjectModelOverrideService
{
    public string LastResult { get; private set; } = "尚未执行 mdl override。";

    private const string SetModelPausedMessage = "SetModel 直接调用已暂停：ResourceCategory / 调用签名未确认，会崩溃。";

    public bool ApplyModel(LocalLayoutObjectInstance instance, string modelPath)
    {
        var blockReason = this.GetApplyModelBlockReason(instance, modelPath, unsafeEnabled: true, confirmed: true);
        if (!string.IsNullOrWhiteSpace(blockReason))
            return this.Fail(instance, blockReason);

        modelPath = modelPath.Trim();
        try
        {
            var bgObject = (BgObject*)ParseAddress(instance.GraphicsObjectAddress);
            var before = this.ReadModelInfo(bgObject, instance);
            var category = (ResourceCategory)(ushort)bgObject->ModelResourceHandle->Type.Category;

            instance.BeforeModelPath = before.Path;
            instance.TargetModelPath = modelPath;
            instance.SetModelReturnValue = string.Empty;
            instance.LastSetModelException = string.Empty;

            var success = bgObject->SetModel(&category, modelPath);
            var after = this.ReadModelInfo(bgObject, instance);
            this.StoreBeforeAfterDump(instance, before, after);
            this.ApplyModelInfo(instance, after);
            instance.SetModelReturnValue = success.ToString();
            instance.ModelOverrideApplied = success;
            instance.CurrentResourcePath = success ? modelPath : FirstNonEmpty(instance.CurrentResourcePath, instance.SourceResourcePath);
            instance.LastModelOverrideResult = $"单实例 SetModel 返回 {success}; category={category} ({(int)category}); before={before.Path}; target={modelPath}; after={after.Path}。尚未自动刷新 render。";
            instance.LastModelOverrideError = success ? string.Empty : "SetModel 返回 false。若模型消失或同款一起变化，请立即暂停实验。";
            this.LastResult = success ? instance.LastModelOverrideResult : instance.LastModelOverrideError;
            return success;
        }
        catch (Exception ex)
        {
            instance.LastSetModelException = ex.ToString();
            return this.Fail(instance, $"SetModel 调用异常：{ex.Message}");
        }
    }

    public bool RestoreModel(LocalLayoutObjectInstance instance)
    {
        return this.Fail(instance, SetModelPausedMessage);
    }

    public bool Refresh(LocalLayoutObjectInstance instance)
    {
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            return this.Fail(instance, $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}");

        try
        {
            var bgObject = (BgObject*)graphicsAddress;
            var info = this.ReadModelInfo(bgObject, instance);
            instance.BeforeModelPath = string.IsNullOrWhiteSpace(instance.BeforeModelPath) ? info.Path : instance.BeforeModelPath;
            this.ApplyModelInfo(instance, info);
            instance.SetModelSignatureReadback = "BgObject.SetModel(ResourceCategory* modelResourceCategory, CStringPointer modelResourcePath) -> bool";
            instance.LastModelOverrideResult = $"只读 Dump modelResourceHandle：path={info.Path}; handle={info.HandleAddress}; type={info.ResourceType}; fileType={info.FileType}; loadState={info.LoadState}";
            instance.LastModelOverrideError = string.Empty;
            this.LastResult = instance.LastModelOverrideResult;
            return true;
        }
        catch (Exception ex)
        {
            return this.Fail(instance, $"刷新模型失败：{ex.Message}");
        }
    }

    public bool ExecuteRefreshStep(LocalLayoutObjectInstance instance, string stepName)
    {
        if (stepName == "UpdateCulling")
            return this.Fail(instance, "UpdateCulling 单独调用已禁用：必须先 ComputeSphereBounds。");

        if (stepName == "CleanupRender")
            return this.MarkRenderInvalid(instance, "CleanupRender 会导致模型消失并使实例 transform 写入不安全，已禁用。");
        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            return this.Fail(instance, $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}");

        try
        {
            var bgObject = (BgObject*)graphicsAddress;
            var before = this.ReadModelInfo(bgObject, instance);
            var detail = stepName switch
            {
                "UpdateMaterials" => Invoke("UpdateMaterials()", () => bgObject->UpdateMaterials()),
                "UpdateRender" => Invoke("UpdateRender()", () => bgObject->UpdateRender()),
                "UpdateTransformsTrue" => Invoke("UpdateTransforms(true)", () => bgObject->UpdateTransforms(true)),
                "NotifyTransformChanged" => Invoke("NotifyTransformChanged()", () => bgObject->NotifyTransformChanged()),
                "SetIsTransformChangedTrue" => Invoke("IsTransformChanged=true", () => bgObject->IsTransformChanged = true),
                "CleanupRender" => "CleanupRender 已禁用。",
                "UpdateCulling" => "鏈煡UpdateCulling：单独调用已禁用，必须先 ComputeSphereBounds。",
                "ComputeSphereBounds" => InvokeComputeSphereBounds(bgObject),
                "ComputeSphereBoundsThenUpdateCulling" => InvokeComputeSphereBoundsThenUpdateCulling(bgObject),
                _ => $"未知刷新步骤：{stepName}",
            };

            var after = this.ReadModelInfo(bgObject, instance);
            this.StoreBeforeAfterDump(instance, before, after);
            this.ApplyModelInfo(instance, after);
            instance.LastModelOverrideResult = $"刷新步骤 {detail}; before={before.Path}; after={after.Path}; dump={after.ResourceDump}";
            instance.LastModelOverrideError = string.Empty;
            this.LastResult = instance.LastModelOverrideResult;
            return !detail.StartsWith("未知", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            instance.LastSetModelException = ex.ToString();
            return this.Fail(instance, $"刷新步骤 {stepName} 异常：{ex.Message}");
        }
    }

    public bool ApplyModelWithRefreshChain(LocalLayoutObjectInstance instance, string modelPath, string chainName)
    {
        if (chainName.Contains("CleanupRender", StringComparison.Ordinal))
            return this.MarkRenderInvalid(instance, "CleanupRender 链已禁用：会导致模型消失并使实例 transform 写入不安全。");

        var success = this.ApplyModel(instance, modelPath);
        if (!success)
            return false;

        var steps = chainName switch
        {
            "SetModel_UpdateMaterials_UpdateRender" => new[] { "UpdateMaterials", "UpdateRender" },
            "SetModel_CleanupRender_UpdateRender" => new[] { "CleanupRender", "UpdateRender" },
            "SetModel_CleanupRender_UpdateMaterials_UpdateRender" => new[] { "CleanupRender", "UpdateMaterials", "UpdateRender" },
            "SetModel_CleanupRender_UpdateTransforms_UpdateRender" => new[] { "CleanupRender", "UpdateTransformsTrue", "UpdateRender" },
            "SetModel_CleanupRender_ComputeSphereBounds_UpdateCulling_UpdateRender" => new[] { "CleanupRender", "ComputeSphereBoundsThenUpdateCulling", "UpdateRender" },
            "SetModel_Notify_UpdateTransforms_UpdateRender" => new[] { "NotifyTransformChanged", "UpdateTransformsTrue", "UpdateRender" },
            _ => [],
        };

        if (steps.Length == 0)
            return this.Fail(instance, $"未知组合刷新链：{chainName}");

        var results = new List<string> { instance.LastModelOverrideResult };
        foreach (var step in steps)
        {
            if (!this.ExecuteRefreshStep(instance, step))
                return false;
            results.Add(instance.LastModelOverrideResult);
        }

        instance.LastModelOverrideResult = string.Join(" | ", results);
        this.LastResult = instance.LastModelOverrideResult;
        return true;
    }

    public string GetApplyModelBlockReason(LocalLayoutObjectInstance instance, string modelPath, bool unsafeEnabled, bool confirmed)
    {
        if (!unsafeEnabled)
            return "UnsafeMode=false。";
        if (!confirmed)
            return "需要勾选“我确认单实例 SetModel 仍可能崩溃”。";
        if (instance.IsRestored || instance.IsInvalid || instance.IsDuplicate)
            return "实例已恢复、失效或重复。";
        if (string.IsNullOrWhiteSpace(instance.GraphicsObjectAddress))
            return "graphicsObjectAddress 为空。";
        if (string.IsNullOrWhiteSpace(modelPath))
            return "target mdl path 为空。";

        modelPath = modelPath.Trim();
        if (!modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return "target mdl path 必须以 .mdl 结尾。";

        if (!TryParseAddress(instance.GraphicsObjectAddress, out var graphicsAddress) || graphicsAddress == 0)
            return $"GraphicsObject 地址解析失败：{instance.GraphicsObjectAddress}";

        try
        {
            var bgObject = (BgObject*)graphicsAddress;
            if (bgObject->ModelResourceHandle == null)
                return "ModelResourceHandle=null。";

            var loadState = bgObject->ModelResourceHandle->LoadState;
            if (loadState < 7)
                return $"LoadState={loadState}，必须 >= 7。";

            var category = (ResourceCategory)(ushort)bgObject->ModelResourceHandle->Type.Category;
            return category switch
            {
                ResourceCategory.Bg when !modelPath.StartsWith("bg/", StringComparison.OrdinalIgnoreCase)
                    => "当前 category=Bg，target mdl path 必须以 bg/ 开头。",
                ResourceCategory.BgCommon when !modelPath.StartsWith("bgcommon/", StringComparison.OrdinalIgnoreCase)
                    => "当前 category=BgCommon，target mdl path 必须以 bgcommon/ 开头。",
                ResourceCategory.Bg or ResourceCategory.BgCommon
                    => string.Empty,
                _ => $"当前 category={category} ({(int)category})，本轮只允许 Bg/BgCommon。",
            };
        }
        catch (Exception ex)
        {
            return $"读取 SetModel 安全条件失败：{ex.Message}";
        }
    }

    private bool Fail(LocalLayoutObjectInstance instance, string message)
    {
        instance.LastModelOverrideError = message;
        instance.LastModelOverrideResult = string.Empty;
        this.LastResult = message;
        return false;
    }

    private bool MarkRenderInvalid(LocalLayoutObjectInstance instance, string message)
    {
        instance.IsRenderInvalid = true;
        instance.ModelExperimentFailed = true;
        instance.TransformWriteDisabledReason = message;
        return this.Fail(instance, message);
    }

    private void StoreBeforeAfterDump(LocalLayoutObjectInstance instance, ModelInfo before, ModelInfo after)
    {
        instance.BeforeModelResourceHandleDump = before.ResourceDump;
        instance.AfterModelResourceHandleDump = after.ResourceDump;
        instance.ModelPointerDiff = CompareModelInfo(before, after);
    }

    private static string CompareModelInfo(ModelInfo before, ModelInfo after)
    {
        var diffs = new List<string>();
        AddDiff(diffs, "path", before.Path, after.Path);
        AddDiff(diffs, "handle", before.HandleAddress, after.HandleAddress);
        AddDiff(diffs, "cachedMatrices", before.CachedMatricesAddress, after.CachedMatricesAddress);
        AddDiff(diffs, "stainOrBgChangeData", before.StainOrBgChangeDataAddress, after.StainOrBgChangeDataAddress);
        AddDiff(diffs, "cachedTransform", before.CachedTransformAddress, after.CachedTransformAddress);
        AddDiff(diffs, "animationData", before.AnimationDataAddress, after.AnimationDataAddress);
        AddDiff(diffs, "visible", before.Visible, after.Visible);
        AddDiff(diffs, "transform", before.Transform, after.Transform);
        return diffs.Count == 0 ? "SetModel/刷新前后可观察指针未变化。" : string.Join("; ", diffs);
    }

    private static void AddDiff(List<string> diffs, string name, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
            diffs.Add($"{name}: {before} -> {after}");
    }

    private void ApplyModelInfo(LocalLayoutObjectInstance instance, ModelInfo info)
    {
        instance.AfterModelPath = info.Path;
        instance.ModelResourceHandleAddress = info.HandleAddress;
        instance.ModelVisibilityReadback = info.Visible;
        instance.ModelTransformReadback = info.Transform;
        instance.ModelResourceHandleDump = info.ResourceDump;
        instance.ModelResourceHandleVTable = info.VTableAddress;
        instance.ModelResourceHandleType = info.ResourceType;
        instance.ModelResourceHandleFileType = info.FileType;
        instance.ModelResourceHandleLoadState = info.LoadState;
        instance.ModelResourceHandleId = info.Id;
        instance.ModelResourceCategoryReadback = info.CategoryReadback;
        instance.ModelResourceCategoryGuess = info.CategoryReadback;
        instance.ModelResourceCategoryConfidence = info.CategoryReadback.StartsWith("不可用", StringComparison.Ordinal)
            ? "低：未能从 ResourceHandle.Type.Category 读回。"
            : "高：来自 ModelResourceHandle.ResourceHandle.Type.Category 读回。";

        if (!info.HandleAvailable)
        {
            instance.IsRenderInvalid = true;
            instance.ModelExperimentFailed = true;
            instance.TransformWriteDisabledReason = "实例 render 已失效：ModelResourceHandle=null 或不可读。";
        }
        else if (!info.Loaded)
        {
            instance.IsRenderInvalid = true;
            instance.ModelExperimentFailed = true;
            instance.TransformWriteDisabledReason = $"实例 render 未就绪：LoadState={info.LoadState}。";
        }
        else if (string.Equals(info.Visible, "False", StringComparison.OrdinalIgnoreCase))
        {
            instance.IsRenderInvalid = true;
            instance.ModelExperimentFailed = true;
            instance.TransformWriteDisabledReason = "实例 render 已失效：visible=false。";
        }
    }

    private static string Invoke(string label, Action action)
    {
        action();
        return $"{label} 成功";
    }

    private static string InvokeComputeSphereBounds(BgObject* bgObject)
    {
        if (bgObject->ModelResourceHandle == null)
            return "ComputeSphereBounds 跳过：ModelResourceHandle=null";

        if (bgObject->ModelResourceHandle->LoadState < 7)
            return $"ComputeSphereBounds 跳过：LoadState={bgObject->ModelResourceHandle->LoadState}";

        var bounds = default(SphereBounds);
        bgObject->ComputeSphereBounds(&bounds);
        return $"ComputeSphereBounds 成功：center=({bounds.CenterPoint.X:F2}, {bounds.CenterPoint.Y:F2}, {bounds.CenterPoint.Z:F2}), radius={bounds.Radius:F2}";
    }

    private static string InvokeComputeSphereBoundsThenUpdateCulling(BgObject* bgObject)
    {
        var boundsResult = InvokeComputeSphereBounds(bgObject);
        if (!boundsResult.Contains("成功", StringComparison.Ordinal) &&
            !boundsResult.Contains("鎴愬姛", StringComparison.Ordinal))
            return $"{boundsResult}; UpdateCulling 已跳过。";

        bgObject->UpdateCulling();
        return $"{boundsResult}; UpdateCulling() 成功。";
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

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

    private static nint ParseAddress(string raw)
    {
        if (!TryParseAddress(raw, out var address) || address == 0)
            throw new InvalidOperationException($"地址解析失败：{raw}");
        return address;
    }

    private ModelInfo ReadModelInfo(BgObject* bgObject, LocalLayoutObjectInstance instance)
    {
        var path = FirstNonEmpty(instance.CurrentResourcePath, instance.SourceResourcePath);
        var handleAddress = "不可用";
        var vtableAddress = "不可用";
        var resourceType = "不可用";
        var fileType = "不可用";
        var loadState = "不可用";
        var id = "不可用";
        var categoryReadback = "不可用";
        var resourceDump = string.Empty;
        var handleAvailable = false;
        var loaded = false;
        try
        {
            if (bgObject->ModelResourceHandle != null)
            {
                var handle = bgObject->ModelResourceHandle;
                handleAvailable = true;
                handleAddress = $"0x{(nint)handle:X}";
                vtableAddress = $"0x{*(nint*)handle:X}";
                var fileName = handle->FileName.ToString();
                if (!string.IsNullOrWhiteSpace(fileName))
                    path = fileName;

                resourceType = SafeRead(() => $"Value=0x{handle->Type.Value:X8}; Category={handle->Type.Category} ({(ushort)handle->Type.Category}); Expansion={handle->Type.Expansion}");
                categoryReadback = SafeRead(() => $"{handle->Type.Category} ({(ushort)handle->Type.Category})");
                fileType = SafeRead(() => $"{handle->FileType} / {DecodeFourCc(handle->FileType)}");
                loadState = SafeRead(() => handle->LoadState.ToString() ?? string.Empty);
                loaded = handle->LoadState >= 7;
                id = SafeRead(() => handle->Id.ToString() ?? string.Empty);
                resourceDump = $"handle={handleAddress}; vtable={vtableAddress}; fileName={path}; type={resourceType}; fileType={fileType}; id={id}; loadState={loadState}; category={categoryReadback}";
            }
        }
        catch
        {
        }

        var visible = "不可用";
        var transform = string.Empty;
        var renderPointers = string.Empty;
        var cachedMatricesAddress = string.Empty;
        var stainOrBgChangeDataAddress = string.Empty;
        var cachedTransformAddress = string.Empty;
        var animationDataAddress = string.Empty;
        try
        {
            visible = bgObject->IsVisible.ToString();
            transform = $"pos=({FormatVector(bgObject->Position)}), rot={bgObject->Rotation}, scale=({FormatVector(bgObject->Scale)})";
            var baseAddress = (nint)bgObject;
            var cachedMatrices = *(nint*)(baseAddress + 0xA0);
            var stainOrBgChangeData = *(nint*)(baseAddress + 0xA8);
            var cachedTransform = *(nint*)(baseAddress + 0xB0);
            var animationData = *(nint*)(baseAddress + 0xB8);
            cachedMatricesAddress = $"0x{cachedMatrices:X}";
            stainOrBgChangeDataAddress = $"0x{stainOrBgChangeData:X}";
            cachedTransformAddress = $"0x{cachedTransform:X}";
            animationDataAddress = $"0x{animationData:X}";
            renderPointers = $"cachedMatrices={cachedMatricesAddress}; stainOrBgChangeData={stainOrBgChangeDataAddress}; cachedTransform={cachedTransformAddress}; animationData={animationDataAddress}";
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(renderPointers))
            resourceDump = string.IsNullOrWhiteSpace(resourceDump) ? renderPointers : $"{resourceDump}; {renderPointers}";

        return new ModelInfo(
            path,
            handleAddress,
            vtableAddress,
            resourceType,
            fileType,
            loadState,
            id,
            categoryReadback,
            resourceDump,
            visible,
            transform,
            cachedMatricesAddress,
            stainOrBgChangeDataAddress,
            cachedTransformAddress,
            animationDataAddress,
            handleAvailable,
            loaded);
    }

    private static string FormatVector(System.Numerics.Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";

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

    private static string DecodeFourCc(uint value)
    {
        Span<char> chars =
        [
            (char)(value & 0xFF),
            (char)((value >> 8) & 0xFF),
            (char)((value >> 16) & 0xFF),
            (char)((value >> 24) & 0xFF),
        ];

        for (var index = 0; index < chars.Length; index++)
        {
            if (char.IsControl(chars[index]))
                chars[index] = '.';
        }

        return new string(chars);
    }

    private readonly record struct ModelInfo(
        string Path,
        string HandleAddress,
        string VTableAddress,
        string ResourceType,
        string FileType,
        string LoadState,
        string Id,
        string CategoryReadback,
        string ResourceDump,
        string Visible,
        string Transform,
        string CachedMatricesAddress,
        string StainOrBgChangeDataAddress,
        string CachedTransformAddress,
        string AnimationDataAddress,
        bool HandleAvailable,
        bool Loaded);
}
