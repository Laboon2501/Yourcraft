using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LocalQuestReborn.Services;

public sealed unsafe class DrawObjectUpdateDirtyProbeService
{
    private const int VisualMatrixOffset = 0x20;
    private const int TinyRotationFloatOffset = 0x3C;

    private nint graphicsObjectAddress;
    private float? originalTinyRotationFloat;

    public string LastProbe { get; private set; } = "尚未探测 DrawObject / dirty 入口。";

    public string CandidateTable { get; private set; } = "尚未扫描 candidate pointer。";

    public string MethodTable { get; private set; } = "尚未扫描 DrawObject 方法。";

    public string LastCallResult { get; private set; } = "尚未调用 DrawObject 方法。";

    public string LastException { get; private set; } = string.Empty;

    public string LastVisualTranslationReport { get; private set; } = "尚未读取 visual translation。";

    public string TransformSourceDump { get; private set; } = "尚未读取 DrawObject / BgObject / Model transform 来源。";

    public string UpdateTransformDiffTrace { get; private set; } = "尚未执行 UpdateTransforms 差分追踪。";

    public string RotationCandidateConclusion { get; private set; } = "dirty/update 可用，但当前 rotation candidate 无视觉效果。+0x03C/+0x040 不再作为推荐写入入口。";

    public bool HasCurrentDrawObjectCandidate => this.graphicsObjectAddress != 0;

    public bool LastUpdateCallSucceeded { get; private set; }

    public void Probe(LayoutProbeInstance? instance)
    {
        this.graphicsObjectAddress = 0;
        this.LastUpdateCallSucceeded = false;
        this.originalTinyRotationFloat = null;

        if (instance == null)
        {
            this.LastProbe = "请先选择一个 BgPart。";
            return;
        }

        if (!string.Equals(instance.Type, "BgPart", StringComparison.Ordinal))
        {
            this.LastProbe = $"当前选择不是 BgPart：{instance.Type}";
            return;
        }

        if (!TryParseAddress(instance.Address, out var address) || address == 0)
        {
            this.LastProbe = $"BgPart 地址解析失败：{instance.Address}";
            return;
        }

        try
        {
            var bgPart = (BgPartsLayoutInstance*)address;
            var graphicsObject = bgPart->GraphicsObject;
            if (graphicsObject == null)
            {
                this.LastProbe = "BgPart.GraphicsObject=null，无法继续。";
                return;
            }

            this.graphicsObjectAddress = (nint)graphicsObject;
            var modelHandle = ReadModelHandle(bgPart);
            var visualTranslation = ReadVisualTranslation(this.graphicsObjectAddress);
            var vtable = ReadPointer(this.graphicsObjectAddress);

            this.CandidateTable = BuildCandidateTable(this.graphicsObjectAddress);
            this.MethodTable = BuildMethodTable();
            this.LastVisualTranslationReport = $"当前 +0x20 row translation：{FormatVector(visualTranslation)}";
            this.LastProbe = string.Join(Environment.NewLine, new[]
            {
                $"layoutInstanceAddress={instance.Address}",
                $"graphicsObjectAddress=0x{this.graphicsObjectAddress:X}",
                $"drawObjectCandidate=GraphicsObject as BgObject/DrawObject",
                $"candidateVTable=0x{vtable:X}",
                $"modelResourceHandle={modelHandle}",
                $"visualTranslation(+0x20 row)={FormatVector(visualTranslation)}",
                "说明：BgPart.GraphicsObject 在 FFXIVClientStructs 中是 BgObject*，继承 DrawObject；本探针不写 layout/collision/resourcePath。",
            });
        }
        catch (Exception ex)
        {
            this.LastProbe = $"DrawObject probe 失败：{ex.Message}";
            this.LastException = ex.ToString();
        }
    }

    public void ReadIsTransformChanged()
        => this.InvokeOnBgObject("get_IsTransformChanged", obj => $"IsTransformChanged={obj->IsTransformChanged}");

    public void SetIsTransformChangedTrue()
        => this.InvokeOnBgObject("set_IsTransformChanged(true)", obj =>
        {
            obj->IsTransformChanged = true;
            return $"写入后 IsTransformChanged={obj->IsTransformChanged}";
        });

    public void NotifyTransformChanged()
        => this.InvokeOnBgObject("NotifyTransformChanged()", obj =>
        {
            obj->NotifyTransformChanged();
            return $"调用完成；IsTransformChanged={obj->IsTransformChanged}";
        });

    public void UpdateTransforms()
        => this.InvokeOnBgObject("UpdateTransforms(true)", obj =>
        {
            obj->UpdateTransforms(true);
            return $"调用完成；IsTransformChanged={obj->IsTransformChanged}";
        });

    public void UpdateRender()
        => this.InvokeOnBgObject("UpdateRender()", obj =>
        {
            obj->UpdateRender();
            return $"调用完成；IsTransformChanged={obj->IsTransformChanged}";
        });

    public void ComputeSphereBounds()
        => this.InvokeOnBgObject("ComputeSphereBounds()", obj =>
        {
            var graphics = (MeddleBgObject*)this.graphicsObjectAddress;
            if (graphics->ModelResourceHandle == null)
                return "已跳过：ModelResourceHandle=null。";

            var loadStateValue = Convert.ToInt32(graphics->ModelResourceHandle->LoadState);
            if (loadStateValue != 7)
                return $"已跳过：ModelResourceHandle.LoadState={graphics->ModelResourceHandle->LoadState} ({loadStateValue})，未确认 loaded。FFXIVClientStructs 注释提示未加载 BgObject 调用 ComputeSphereBounds 可能 AccessViolation。";

            FFXIVClientStructs.FFXIV.Common.Math.SphereBounds bounds = default;
            obj->ComputeSphereBounds(&bounds);
            return $"调用完成；SphereBounds center={bounds.CenterPoint}, radius={bounds.Radius:F3}";
        });

    public void DumpTransformSources()
    {
        if (this.graphicsObjectAddress == 0)
        {
            this.TransformSourceDump = "没有当前 DrawObject candidate。请先探测。";
            return;
        }

        try
        {
            var obj = (BgObject*)this.graphicsObjectAddress;
            var graphics = (MeddleBgObject*)this.graphicsObjectAddress;
            var visualTranslation = ReadVisualTranslation(this.graphicsObjectAddress);
            var lines = new List<string>
            {
                $"graphicsObjectAddress=0x{this.graphicsObjectAddress:X}",
                $"vtable=0x{ReadPointer(this.graphicsObjectAddress):X}",
                $"ModelResourceHandle=0x{(nint)graphics->ModelResourceHandle:X}",
                graphics->ModelResourceHandle == null
                    ? "ModelResourceHandle=null"
                    : $"ModelResourceHandle.FileName={graphics->ModelResourceHandle->FileName}; LoadState={graphics->ModelResourceHandle->LoadState}",
                $"IsTransformChanged={obj->IsTransformChanged}",
                $"IsVisible={obj->IsVisible}",
                $"visualTranslation(+0x20 row)={FormatVector(visualTranslation)}",
                "GetAttachBoneWorldTransform:",
                TryGetAttachBoneWorldTransform(obj, 0),
                "DrawObject/BgObject transform-like memory:",
                ScanTransformLikeMemory(this.graphicsObjectAddress, 0x500),
                "字段/属性/方法取证:",
                BuildTransformMemberTable(),
                "结论：本轮继续找 UpdateTransforms 真正使用的源字段；不再直接写 +0x03C/+0x040 rotation candidate。",
            };

            this.TransformSourceDump = string.Join(Environment.NewLine, lines);
            this.LastException = string.Empty;
        }
        catch (Exception ex)
        {
            this.TransformSourceDump = $"读取 transform 来源失败：{ex.Message}";
            this.LastException = ex.ToString();
        }
    }

    public void TraceUpdateTransformsDiff()
    {
        if (this.graphicsObjectAddress == 0)
        {
            this.UpdateTransformDiffTrace = "没有当前 DrawObject candidate。请先探测。";
            return;
        }

        try
        {
            var obj = (BgObject*)this.graphicsObjectAddress;
            var beforeTranslation = ReadVisualTranslation(this.graphicsObjectAddress);
            var beforeChanged = obj->IsTransformChanged;
            var before = SnapshotBytes(this.graphicsObjectAddress, 0x500);

            obj->IsTransformChanged = true;
            var afterSetChanged = obj->IsTransformChanged;
            obj->UpdateTransforms(true);

            var afterChanged = obj->IsTransformChanged;
            var afterTranslation = ReadVisualTranslation(this.graphicsObjectAddress);
            var after = SnapshotBytes(this.graphicsObjectAddress, 0x500);
            var diff = DiffSnapshots(before, after);

            this.UpdateTransformDiffTrace = string.Join(Environment.NewLine, new[]
            {
                "UpdateTransforms 差分追踪完成。",
                $"graphicsObjectAddress=0x{this.graphicsObjectAddress:X}",
                $"IsTransformChanged: before={beforeChanged}, afterSetTrue={afterSetChanged}, afterUpdate={afterChanged}",
                $"visualTranslation before={FormatVector(beforeTranslation)}",
                $"visualTranslation after ={FormatVector(afterTranslation)}",
                $"changed4ByteFields={diff.Count}",
                "疑似 computed transform / dirty / bounds 字段：",
                diff.Count == 0 ? "  未发现 0x000-0x500 内 4-byte 字段变化。" : string.Join(Environment.NewLine, diff.Take(80)),
                "说明：本追踪只写 IsTransformChanged=true 并调用 UpdateTransforms(true)，不写 layout/collision/resourcePath。",
            });
            this.LastException = string.Empty;
        }
        catch (Exception ex)
        {
            this.UpdateTransformDiffTrace = $"UpdateTransforms 差分追踪失败：{ex.Message}";
            this.LastException = ex.ToString();
        }
    }

    public void RunTinyRotationWithNotify()
    {
        if (this.graphicsObjectAddress == 0)
        {
            this.LastCallResult = "没有当前 DrawObject candidate。请先探测。";
            return;
        }

        if (!this.LastUpdateCallSucceeded)
        {
            this.LastCallResult = "尚未有成功的 dirty/update 调用。本按钮要求先成功调用 NotifyTransformChanged / UpdateTransforms / UpdateRender 之一。";
            return;
        }

        try
        {
            var beforeTranslation = ReadVisualTranslation(this.graphicsObjectAddress);
            var floatPtr = (float*)((byte*)this.graphicsObjectAddress + TinyRotationFloatOffset);
            this.originalTinyRotationFloat ??= *floatPtr;
            var beforeFloat = *floatPtr;
            *floatPtr = beforeFloat + 0.001f;

            var obj = (BgObject*)this.graphicsObjectAddress;
            obj->NotifyTransformChanged();
            obj->UpdateTransforms(true);
            WriteVisualTranslation(this.graphicsObjectAddress, beforeTranslation);

            var afterTranslation = ReadVisualTranslation(this.graphicsObjectAddress);
            var afterFloat = *floatPtr;
            this.LastVisualTranslationReport = $"tiny rotation 前 translation={FormatVector(beforeTranslation)}；后 translation={FormatVector(afterTranslation)}";
            this.LastCallResult = string.Join(Environment.NewLine, new[]
            {
                "已执行历史 rotation candidate + dirty/update，但当前结论是无视觉旋转，保留此按钮仅用于复核。",
                $"floatOffset=+0x{TinyRotationFloatOffset:X}",
                $"beforeFloat={beforeFloat:F6}",
                $"afterFloat={afterFloat:F6}",
                $"translationKept={Vector3.Distance(beforeTranslation, afterTranslation) <= 0.01f}",
                this.RotationCandidateConclusion,
                "注意：这不是正式功能，不再推荐继续点击；不写 layout/collision/resourcePath。",
            });
            this.LastException = string.Empty;
        }
        catch (Exception ex)
        {
            this.LastCallResult = $"极小 rotation candidate 调用失败：{ex.Message}";
            this.LastException = ex.ToString();
        }
    }

    public void RestoreTinyRotationFloat()
    {
        if (this.graphicsObjectAddress == 0 || !this.originalTinyRotationFloat.HasValue)
        {
            this.LastCallResult = "没有可恢复的 tiny rotation 原值。";
            return;
        }

        try
        {
            var beforeTranslation = ReadVisualTranslation(this.graphicsObjectAddress);
            *(float*)((byte*)this.graphicsObjectAddress + TinyRotationFloatOffset) = this.originalTinyRotationFloat.Value;
            WriteVisualTranslation(this.graphicsObjectAddress, beforeTranslation);
            var obj = (BgObject*)this.graphicsObjectAddress;
            obj->NotifyTransformChanged();
            this.LastCallResult = $"已恢复 +0x{TinyRotationFloatOffset:X} float={this.originalTinyRotationFloat.Value:F6}，并保持 +0x20 translation={FormatVector(ReadVisualTranslation(this.graphicsObjectAddress))}";
            this.LastException = string.Empty;
        }
        catch (Exception ex)
        {
            this.LastCallResult = $"恢复 tiny rotation float 失败：{ex.Message}";
            this.LastException = ex.ToString();
        }
    }

    private void InvokeOnBgObject(string actionName, BgObjectAction action)
    {
        if (this.graphicsObjectAddress == 0)
        {
            this.LastCallResult = "没有当前 DrawObject candidate。请先探测。";
            return;
        }

        try
        {
            var before = ReadVisualTranslation(this.graphicsObjectAddress);
            var obj = (BgObject*)this.graphicsObjectAddress;
            var detail = action(obj);
            var after = ReadVisualTranslation(this.graphicsObjectAddress);
            this.LastUpdateCallSucceeded = actionName.Contains("Notify", StringComparison.OrdinalIgnoreCase) ||
                                           actionName.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                                           this.LastUpdateCallSucceeded;
            this.LastVisualTranslationReport = $"beforeVisualTranslation={FormatVector(before)}{Environment.NewLine}afterVisualTranslation={FormatVector(after)}";
            this.LastCallResult = $"{actionName} 成功。{detail}";
            this.LastException = string.Empty;
        }
        catch (Exception ex)
        {
            this.LastCallResult = $"{actionName} 失败：{ex.Message}";
            this.LastException = ex.ToString();
        }
    }

    private static string BuildCandidateTable(nint graphicsObjectAddress)
    {
        var lines = new List<string>
        {
            "possibleDrawObjectPointers:",
            $"  self GraphicsObject/BgObject/DrawObject: address=0x{graphicsObjectAddress:X}; vtable=0x{ReadPointer(graphicsObjectAddress):X}; vtableCheck=已读取",
            "  pointer scan around GraphicsObject 0x00-0x180:",
        };

        try
        {
            var basePtr = (byte*)graphicsObjectAddress;
            for (var offset = 0; offset <= 0x180 - sizeof(nint); offset += sizeof(nint))
            {
                var value = *(nint*)(basePtr + offset);
                if (!LooksLikePointer(value))
                    continue;

                var marker = value == graphicsObjectAddress ? "self" : "candidate";
                lines.Add($"  +0x{offset:X3}: 0x{value:X} ({marker}; vtable 未解引用，避免误读未知指针)");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"  pointer scan 失败：{ex.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildMethodTable()
    {
        try
        {
            var bgObjectType = typeof(BgObject);
            var drawObjectType = typeof(DrawObject);
            var objectType = typeof(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object);
            var names = new[]
            {
                "IsTransformChanged",
                "NotifyTransformChanged",
                "UpdateTransforms",
                "UpdateRender",
                "ComputeSphereBounds",
                "ComputeAxisAlignedBounds",
                "ComputeOrientedBounds",
                "GetAttachBoneWorldTransform",
            };

            var lines = new List<string>
            {
                $"BgObject type={bgObjectType.FullName}",
                $"DrawObject type={drawObjectType.FullName}",
                $"Scene.Object type={objectType.FullName}",
                "可见成员：",
            };

            foreach (var type in new[] { bgObjectType, drawObjectType, objectType })
            {
                lines.Add($"[{type.Name}]");
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Where(member => names.Any(name => member.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    .Select(DescribeMember)
                    .Distinct()
                    .OrderBy(member => member);

                foreach (var member in members)
                    lines.Add($"  {member}");
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"扫描 DrawObject 方法失败：{ex.Message}";
        }
    }

    private static string DescribeMember(MemberInfo member)
    {
        if (member is MethodInfo method)
        {
            var parameters = string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
            return $"Method {method.ReturnType.Name} {method.Name}({parameters})";
        }

        if (member is PropertyInfo property)
            return $"Property {property.PropertyType.Name} {property.Name} canWrite={property.CanWrite}";

        if (member is FieldInfo field)
            return $"Field {field.FieldType.Name} {field.Name}";

        return $"{member.MemberType} {member.Name}";
    }

    private static string TryGetAttachBoneWorldTransform(BgObject* obj, int boneIndex)
    {
        try
        {
            FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 matrix = default;
            obj->GetAttachBoneWorldTransform(&matrix, boneIndex);
            return $"  bone={boneIndex}; matrix={matrix}";
        }
        catch (Exception ex)
        {
            return $"  读取失败：{ex.Message}";
        }
    }

    private static string ScanTransformLikeMemory(nint address, int length)
    {
        var lines = new List<string>();
        try
        {
            var basePtr = (byte*)address;
            var vectorHits = new List<string>();
            var matrixHits = new List<string>();

            for (var offset = 0; offset <= length - sizeof(float) * 3; offset += 4)
            {
                var value = *(Vector3*)(basePtr + offset);
                if (!IsReasonableVector(value))
                    continue;

                vectorHits.Add($"  Vector3 @ +0x{offset:X3}: {FormatVector(value)}");
            }

            for (var offset = 0; offset <= length - sizeof(float) * 16; offset += 4)
            {
                var matrix = *(Matrix4x4*)(basePtr + offset);
                if (!IsReasonableMatrix(matrix))
                    continue;

                var rowT = new Vector3(matrix.M41, matrix.M42, matrix.M43);
                var columnT = new Vector3(matrix.M14, matrix.M24, matrix.M34);
                matrixHits.Add($"  Matrix4x4 @ +0x{offset:X3}: rowT={FormatVector(rowT)}; colT={FormatVector(columnT)}; diag={matrix.M11:F3}/{matrix.M22:F3}/{matrix.M33:F3}; M44={matrix.M44:F3}");
            }

            lines.Add($"scanRange=0x000-0x{length:X3}");
            lines.Add($"vectorCandidateCount={vectorHits.Count}");
            lines.AddRange(vectorHits.Take(40));
            lines.Add($"matrixCandidateCount={matrixHits.Count}");
            lines.AddRange(matrixHits.Take(40));
        }
        catch (Exception ex)
        {
            lines.Add($"transform-like memory scan 失败：{ex.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildTransformMemberTable()
    {
        try
        {
            var keywords = new[]
            {
                "Transform",
                "Matrix",
                "Position",
                "Rotation",
                "Scale",
                "Bound",
                "Render",
                "Model",
                "Resource",
                "Changed",
                "Update",
                "Notify",
            };

            var lines = new List<string>();
            foreach (var type in new[]
            {
                typeof(BgObject),
                typeof(DrawObject),
                typeof(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object),
                typeof(ModelResourceHandle),
            })
            {
                lines.Add($"[{type.FullName}]");
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(member => keywords.Any(keyword => member.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    .Select(DescribeMember)
                    .Distinct()
                    .OrderBy(member => member)
                    .Take(80);

                foreach (var member in members)
                    lines.Add($"  {member}");
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"成员表扫描失败：{ex.Message}";
        }
    }

    private static byte[] SnapshotBytes(nint address, int length)
    {
        var result = new byte[length];
        var source = (byte*)address;
        for (var i = 0; i < length; i++)
            result[i] = source[i];

        return result;
    }

    private static List<string> DiffSnapshots(byte[] before, byte[] after)
    {
        var lines = new List<string>();
        var length = Math.Min(before.Length, after.Length);
        for (var offset = 0; offset <= length - 4; offset += 4)
        {
            var oldUInt = BitConverter.ToUInt32(before, offset);
            var newUInt = BitConverter.ToUInt32(after, offset);
            if (oldUInt == newUInt)
                continue;

            var oldFloat = BitConverter.ToSingle(before, offset);
            var newFloat = BitConverter.ToSingle(after, offset);
            var oldText = IsFinite(oldFloat) && Math.Abs(oldFloat) < 100000f ? $"{oldFloat:F6}" : "not-float";
            var newText = IsFinite(newFloat) && Math.Abs(newFloat) < 100000f ? $"{newFloat:F6}" : "not-float";
            lines.Add($"  +0x{offset:X3}: u32 0x{oldUInt:X8} -> 0x{newUInt:X8}; float {oldText} -> {newText}");
        }

        return lines;
    }

    private static bool IsReasonableVector(Vector3 value)
    {
        return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z) &&
               Math.Abs(value.X) < 100000f &&
               Math.Abs(value.Y) < 100000f &&
               Math.Abs(value.Z) < 100000f &&
               value.LengthSquared() > 0.0001f;
    }

    private static bool IsReasonableMatrix(Matrix4x4 matrix)
    {
        return IsFinite(matrix.M11) && IsFinite(matrix.M12) && IsFinite(matrix.M13) && IsFinite(matrix.M14) &&
               IsFinite(matrix.M21) && IsFinite(matrix.M22) && IsFinite(matrix.M23) && IsFinite(matrix.M24) &&
               IsFinite(matrix.M31) && IsFinite(matrix.M32) && IsFinite(matrix.M33) && IsFinite(matrix.M34) &&
               IsFinite(matrix.M41) && IsFinite(matrix.M42) && IsFinite(matrix.M43) && IsFinite(matrix.M44) &&
               Math.Abs(matrix.M44) < 10f;
    }

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string ReadModelHandle(BgPartsLayoutInstance* bgPart)
    {
        try
        {
            if (bgPart->GraphicsObject == null)
                return "GraphicsObject=null";

            var graphics = (MeddleBgObject*)bgPart->GraphicsObject;
            if (graphics->ModelResourceHandle == null)
                return "ModelResourceHandle=null";

            return $"0x{(nint)graphics->ModelResourceHandle:X}; FileName={graphics->ModelResourceHandle->FileName}; LoadState={graphics->ModelResourceHandle->LoadState}";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    private static Vector3 ReadVisualTranslation(nint graphicsObjectAddress)
    {
        var matrix = *(Matrix4x4*)((byte*)graphicsObjectAddress + VisualMatrixOffset);
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }

    private static void WriteVisualTranslation(nint graphicsObjectAddress, Vector3 translation)
    {
        var basePtr = (byte*)graphicsObjectAddress + VisualMatrixOffset;
        *(float*)(basePtr + 0x30) = translation.X;
        *(float*)(basePtr + 0x34) = translation.Y;
        *(float*)(basePtr + 0x38) = translation.Z;
    }

    private static nint ReadPointer(nint address)
    {
        try
        {
            return address == 0 ? 0 : *(nint*)address;
        }
        catch
        {
            return 0;
        }
    }

    private static bool LooksLikePointer(nint value)
    {
        var raw = (ulong)value;
        return raw > 0x10000UL &&
               raw < 0x0000800000000000UL &&
               (raw & 0x7UL) == 0;
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

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F3}, Y {vector.Y:F3}, Z {vector.Z:F3}";

    private unsafe delegate string BgObjectAction(BgObject* obj);

    [StructLayout(LayoutKind.Explicit, Size = 0xD0)]
    private unsafe struct MeddleBgObject
    {
        [FieldOffset(0x90)] public ModelResourceHandle* ModelResourceHandle;
    }
}
