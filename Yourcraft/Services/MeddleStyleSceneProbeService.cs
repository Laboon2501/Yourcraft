using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Yourcraft.Models;
using System.Numerics;
using System.Reflection;
using SceneDrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;

namespace Yourcraft.Services;

public sealed unsafe class MeddleStyleSceneProbeService
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    public MeddleStyleSceneProbeService(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    public IReadOnlyList<MeddleStyleSceneObject> Objects { get; private set; } = [];

    public string LastStatus { get; private set; } = "尚未执行 Meddle-style 场景读取。";

    public string SearchText { get; set; } = string.Empty;

    public bool NearbyOnly { get; set; }

    public float MaxDistance { get; set; } = 50f;

    public bool SortByDistance { get; set; } = true;

    public void DumpVisibleSceneObjects(Vector3? playerPosition)
    {
        var results = new List<MeddleStyleSceneObject>();
        try
        {
            var gameObjectManager = GameObjectManager.Instance();
            if (gameObjectManager != null)
            {
                for (var idx = 0; idx < gameObjectManager->Objects.GameObjectIdSorted.Length; idx++)
                {
                    var objectPointer = gameObjectManager->Objects.GameObjectIdSorted[idx];
                    if (objectPointer == null || objectPointer.Value == null)
                        continue;

                    AddNativeGameObject(results, objectPointer.Value, idx, playerPosition, "GameObjectManager.Objects.GameObjectIdSorted");
                }
            }
            else
            {
                var fallbackIndex = 0;
                foreach (var obj in this.objectTable)
                {
                    if (obj == null)
                        continue;

                    if (!TryGetAddress(obj, out var objectAddress) || objectAddress == 0)
                        continue;

                    AddNativeGameObject(results, (GameObject*)objectAddress, fallbackIndex++, playerPosition, "IObjectTable fallback");
                }
            }

            IEnumerable<MeddleStyleSceneObject> filtered = results;
            if (this.NearbyOnly && playerPosition.HasValue)
                filtered = filtered.Where(item => item.DistanceToPlayer <= this.MaxDistance);

            if (!string.IsNullOrWhiteSpace(this.SearchText))
            {
                filtered = filtered.Where(item =>
                    item.ModelPath.Contains(this.SearchText, StringComparison.OrdinalIgnoreCase) ||
                    item.ResourcePath.Contains(this.SearchText, StringComparison.OrdinalIgnoreCase) ||
                    item.ObjectType.Contains(this.SearchText, StringComparison.OrdinalIgnoreCase) ||
                    item.ObjectName.Contains(this.SearchText, StringComparison.OrdinalIgnoreCase) ||
                    item.DebugInfo.Contains(this.SearchText, StringComparison.OrdinalIgnoreCase));
            }

            if (this.SortByDistance && playerPosition.HasValue)
                filtered = filtered.OrderBy(item => item.DistanceToPlayer);

            this.Objects = filtered.Take(100).ToList();
            this.LastStatus = $"已按 Meddle 路线读取 DrawObject 非空对象 {results.Count} 个，当前显示 {this.Objects.Count} 个。此探针只读，不写入 native。";
        }
        catch (Exception ex)
        {
            this.LastStatus = $"Meddle-style 场景读取失败：{ex.Message}";
            this.log.Warning(ex, "Failed to dump Meddle-style visible scene objects.");
        }
    }

    private static void AddNativeGameObject(List<MeddleStyleSceneObject> results, GameObject* native, int index, Vector3? playerPosition, string source)
    {
        if (native == null)
            return;

        var drawObject = native->DrawObject;
        if (drawObject == null)
            return;

        var drawObjectAddress = (nint)drawObject;
        var position = drawObject->Position;
        var scale = drawObject->Scale;
        var distance = playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, position) : 0f;
        var readyToDraw = false;
        try
        {
            readyToDraw = native->IsReadyToDraw();
        }
        catch
        {
        }

        string drawObjectType;
        try
        {
            drawObjectType = drawObject->GetObjectType().ToString();
        }
        catch
        {
            drawObjectType = "未读取";
        }

        results.Add(new MeddleStyleSceneObject
        {
            Index = index,
            ObjectType = native->ObjectKind.ToString(),
            ObjectName = native->NameString.ToString(),
            ObjectAddress = $"0x{(nint)native:X}",
            DrawObjectAddress = $"0x{drawObjectAddress:X}",
            DrawObjectType = drawObjectType,
            Position = position,
            Rotation = drawObject->Rotation.ToString(),
            Scale = scale,
            ResourcePath = "未解析：Meddle 对 BgPart 通过 LayoutInstance/ModelResourceHandle 读取，普通 DrawObject 需继续解析 ModelResourceHandle",
            ModelPath = "未解析：可见对象当前只确认 DrawObject，模型路径需继续接入 ParseMaterialUtil/ModelResourceHandle",
            DistanceToPlayer = distance,
            IsReadyToDraw = readyToDraw,
            IsVisible = drawObject->IsVisible,
            Source = source,
            DebugInfo = $"ObjectIndex={native->ObjectIndex}; BaseId={native->BaseId}; EntityId={native->EntityId}; DrawObjectType={drawObjectType}",
        });
    }

    private static bool TryGetAddress(object source, out nint address)
    {
        address = 0;
        var raw = ReadRawMember(source, "Address");
        if (raw == null)
            return false;

        if (raw is nint nativeInt)
        {
            address = nativeInt;
            return address != 0;
        }

        if (raw is IntPtr pointer)
        {
            address = pointer;
            return address != 0;
        }

        var text = raw.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexValue))
        {
            address = (nint)hexValue;
            return address != 0;
        }

        if (ulong.TryParse(text, out var value))
        {
            address = (nint)value;
            return address != 0;
        }

        return false;
    }

    private static string ReadManagedMember(object source, params string[] names)
    {
        var value = ReadRawMember(source, names);
        return value?.ToString() ?? string.Empty;
    }

    private static object? ReadRawMember(object source, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null)
                    return property.GetValue(source);

                var field = source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (field != null)
                    return field.GetValue(source);
            }
            catch
            {
            }
        }

        return null;
    }
}
