using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using LocalQuestReborn.Models;

namespace LocalQuestReborn.Services;

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
            bgObject->NotifyTransformChanged();
            bgObject->IsTransformChanged = true;
            bgObject->UpdateMaterials();
            bgObject->UpdateRender();
            bgObject->UpdateTransforms(true);

            var after = this.ReadModelInfo(bgObject, instance);
            instance.AfterModelPath = after.Path;
            instance.ModelResourceHandleAddress = after.HandleAddress;
            instance.ModelResourceHandleDump = after.ResourceDump;
            instance.ModelResourceHandleVTable = after.VTableAddress;
            instance.ModelResourceHandleType = after.ResourceType;
            instance.ModelResourceHandleFileType = after.FileType;
            instance.ModelResourceHandleLoadState = after.LoadState;
            instance.ModelResourceHandleId = after.Id;
            instance.ModelResourceCategoryReadback = after.CategoryReadback;
            instance.ModelResourceCategoryGuess = after.CategoryReadback;
            instance.ModelResourceCategoryConfidence = after.CategoryReadback.StartsWith("不可用", StringComparison.Ordinal)
                ? "低：未能从 ResourceHandle.Type.Category 读回。"
                : "高：来自 ModelResourceHandle.ResourceHandle.Type.Category 读回。";
            instance.ModelVisibilityReadback = after.Visible;
            instance.ModelTransformReadback = after.Transform;
            instance.SetModelReturnValue = success.ToString();
            instance.ModelOverrideApplied = success;
            instance.CurrentResourcePath = success ? modelPath : FirstNonEmpty(instance.CurrentResourcePath, instance.SourceResourcePath);
            instance.LastModelOverrideResult = $"单实例 SetModel 返回 {success}; category={category} ({(int)category}); before={before.Path}; target={modelPath}; after={after.Path}";
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
                : "高：来自 ModelResourceHandle.ResourceHandle.Type.Category 读回，不再按路径前缀猜测。";
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
        try
        {
            if (bgObject->ModelResourceHandle != null)
            {
                var handle = bgObject->ModelResourceHandle;
                handleAddress = $"0x{(nint)handle:X}";
                vtableAddress = $"0x{*(nint*)handle:X}";
                var fileName = handle->FileName.ToString();
                if (!string.IsNullOrWhiteSpace(fileName))
                    path = fileName;

                resourceType = SafeRead(() => $"Value=0x{handle->Type.Value:X8}; Category={handle->Type.Category} ({(ushort)handle->Type.Category}); Expansion={handle->Type.Expansion}");
                categoryReadback = SafeRead(() => $"{handle->Type.Category} ({(ushort)handle->Type.Category})");
                fileType = SafeRead(() => $"{handle->FileType} / {DecodeFourCc(handle->FileType)}");
                loadState = SafeRead(() => handle->LoadState.ToString() ?? string.Empty);
                id = SafeRead(() => handle->Id.ToString() ?? string.Empty);
                resourceDump = $"handle={handleAddress}; vtable={vtableAddress}; fileName={path}; type={resourceType}; fileType={fileType}; id={id}; loadState={loadState}; category={categoryReadback}";
            }
        }
        catch
        {
        }

        var visible = "不可用";
        var transform = string.Empty;
        try
        {
            visible = bgObject->IsVisible.ToString();
            transform = $"pos=({FormatVector(bgObject->Position)}), rot={bgObject->Rotation}, scale=({FormatVector(bgObject->Scale)})";
        }
        catch
        {
        }

        return new ModelInfo(path, handleAddress, vtableAddress, resourceType, fileType, loadState, id, categoryReadback, resourceDump, visible, transform);
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
        string Transform);
}
