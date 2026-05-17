using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Terrain;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Yourcraft.Models;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Yourcraft.Services;

public sealed unsafe class LayoutProbeService
{
    private readonly IPluginLog log;

    public LayoutProbeService(IObjectTable objectTable, IPluginLog log)
    {
        this.log = log;
    }

    public IReadOnlyList<LayoutProbeInstance> Instances { get; private set; } = [];

    public List<LayoutProbeInstance> SelectedInstances { get; } = [];

    public string LastStatus { get; private set; } = "尚未执行 Layout dump。";

    public string ManagerDump { get; private set; } = "尚未读取。";

    public string LayerManagerDump { get; private set; } = "尚未读取。";

    public string TypeDump { get; private set; } = "尚未读取。";

    public string LastSelectedDump { get; private set; } = "尚未 Dump selected layout instance。";

    public bool NearbyOnly { get; set; }

    public void ClearRuntimeCache(string reason)
    {
        this.Instances = [];
        this.SelectedInstances.Clear();
        this.LastStatus = $"Layout runtime cache cleared: {reason}";
    }

    public float MaxDistance { get; set; } = 100f;

    public int MaxResults { get; set; } = 250;

    public bool SortByDistance { get; set; } = true;

    public bool ShowBgPart { get; set; } = true;

    public bool ShowSharedGroup { get; set; } = true;

    public bool ShowLight { get; set; } = true;

    public bool ShowTerrain { get; set; } = true;

    public bool ShowCamera { get; set; } = true;

    public bool ShowCharacter { get; set; }

    public string TypeFilter { get; set; } = string.Empty;

    public void DumpLayoutManagers()
    {
        try
        {
            var assembly = typeof(GameObject).Assembly;
            var layoutTypes = assembly.GetTypes()
                .Where(type => type.FullName?.Contains("FFXIVClientStructs.FFXIV.Client.LayoutEngine", StringComparison.Ordinal) == true)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            this.TypeDump = string.Join(Environment.NewLine, layoutTypes.Select(type => type.FullName).Take(240));
            this.ManagerDump = DescribeType(layoutTypes, "LayoutManager", "LayoutWorld", "LayoutEngine");
            this.LayerManagerDump = DescribeType(layoutTypes, "LayerManager", "Layer");
            this.LastStatus = $"已读取 LayoutEngine 类型 {layoutTypes.Count} 个。实例列表使用 Meddle LayoutWorld/ILayoutInstance 只读路径。";
        }
        catch (Exception ex)
        {
            this.LastStatus = $"读取 Layout manager 类型失败：{ex.Message}";
            this.log.Warning(ex, "Failed to dump LayoutEngine manager types.");
        }
    }

    public void EnumerateInstances(Vector3? playerPosition)
    {
        var results = new List<LayoutProbeInstance>();
        try
        {
            var searchOrigin = playerPosition ?? Vector3.Zero;
            var layoutWorld = LayoutWorld.Instance();
            if (layoutWorld == null)
            {
                this.LastStatus = "LayoutWorld.Instance() 不可用。";
                this.Instances = [];
                return;
            }

            var index = 0;
            foreach (var (_, layoutPtr) in layoutWorld->LoadedLayouts)
                ParseLayout(layoutPtr.Value, searchOrigin, results, ref index, "LoadedLayout");

            ParseLayout(layoutWorld->GlobalLayout, searchOrigin, results, ref index, "GlobalLayout");

            if (this.ShowCamera)
                AddCamera(results, ref index, searchOrigin);

            if (this.ShowCharacter)
                AddCharacters(results, ref index, searchOrigin);

            IEnumerable<LayoutProbeInstance> filtered = results;
            if (this.NearbyOnly && playerPosition.HasValue)
                filtered = filtered.Where(instance => instance.DistanceToPlayer <= this.MaxDistance);

            if (!string.IsNullOrWhiteSpace(this.TypeFilter))
            {
                filtered = filtered.Where(instance =>
                    instance.Type.Contains(this.TypeFilter, StringComparison.OrdinalIgnoreCase) ||
                    instance.ResourcePath.Contains(this.TypeFilter, StringComparison.OrdinalIgnoreCase) ||
                    instance.Key.Contains(this.TypeFilter, StringComparison.OrdinalIgnoreCase) ||
                    instance.DebugInfo.Contains(this.TypeFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (this.SortByDistance && playerPosition.HasValue)
                filtered = filtered.OrderBy(instance => instance.DistanceToPlayer);

            this.Instances = filtered.Take(Math.Max(1, this.MaxResults)).ToList();
            this.LastStatus = $"已按 Meddle Layout 页路线读取 Layout instances {results.Count} 个，当前显示 {this.Instances.Count} 个。未读取 EventNPC/ObjectTable。";
        }
        catch (Exception ex)
        {
            this.LastStatus = $"枚举 Layout instances 失败：{ex.Message}";
            this.log.Warning(ex, "Failed to enumerate LayoutWorld instances.");
        }
    }

    public void AddToSelection(LayoutProbeInstance instance)
    {
        if (this.SelectedInstances.Any(item => item.Key == instance.Key && item.Address == instance.Address))
            return;

        this.SelectedInstances.Add(instance);
    }

    public void ClearSelection()
    {
        this.SelectedInstances.Clear();
    }

    private static string BuildStableLayoutKey(
        string type,
        string sourceKind,
        string source,
        string resourcePath,
        string sharedGroupPath,
        string parentKey,
        int childIndex)
    {
        return string.Join(
            "|",
            NormalizeKeyPart(type),
            NormalizeKeyPart(sourceKind),
            NormalizeKeyPart(source),
            NormalizeKeyPart(resourcePath),
            NormalizeKeyPart(sharedGroupPath),
            NormalizeKeyPart(parentKey),
            childIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static string NormalizeKeyPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";

        var sanitized = StripVolatileHexAddresses(value.Trim());
        return sanitized.Length <= 260 ? sanitized : sanitized[..260];
    }

    private static string StripVolatileHexAddresses(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length;)
        {
            if (i + 2 < value.Length &&
                value[i] == '0' &&
                (value[i + 1] == 'x' || value[i + 1] == 'X') &&
                IsHexDigit(value[i + 2]))
            {
                var j = i + 2;
                while (j < value.Length && IsHexDigit(value[j]))
                    j++;

                builder.Append("0x*");
                i = j;
                continue;
            }

            builder.Append(value[i]);
            i++;
        }

        return builder.ToString();
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    public void DumpSelected(LayoutProbeInstance instance)
    {
        this.LastSelectedDump = string.Join(Environment.NewLine, new[]
        {
            $"type={instance.Type}",
            $"key={instance.Key}",
            $"address={instance.Address}",
            $"resourcePath={instance.ResourcePath}",
            $"visible={instance.Visible}",
            $"position={instance.Position}",
            $"rotation={instance.Rotation}",
            $"scale={instance.Scale}",
            $"distance={instance.DistanceToPlayer:F2}",
            $"source={instance.Source}",
            $"debug={instance.DebugInfo}",
        });
    }

    private void ParseLayout(LayoutManager* activeLayout, Vector3 searchOrigin, List<LayoutProbeInstance> results, ref int index, string source)
    {
        if (activeLayout == null)
            return;

        foreach (var (layerKey, layerPtr) in activeLayout->Layers)
        {
            if (layerPtr == null || layerPtr.Value == null)
                continue;

            foreach (var (instanceKey, instancePtr) in layerPtr.Value->Instances)
            {
                if (instancePtr == null || instancePtr.Value == null)
                    continue;

                ParseInstanceTree(
                    instancePtr.Value,
                    searchOrigin,
                    results,
                    ref index,
                    $"{source}; Layer={layerKey}; Instance={instanceKey}",
                    sourceKind: "LoadedLayout",
                    sharedGroupPath: string.Empty,
                    parentAddress: string.Empty,
                    parentKey: string.Empty,
                    childIndex: -1,
                    depth: 0);
            }
        }

        foreach (var (terrainKey, terrainPtr) in activeLayout->Terrains)
        {
            if (!this.ShowTerrain || terrainPtr == null || terrainPtr.Value == null)
                continue;

            var terrain = terrainPtr.Value;
            var path = terrain->PathString;
            var position = Vector3.Zero;
            results.Add(new LayoutProbeInstance
            {
                Index = index++,
                Key = $"Terrain={terrainKey}",
                Type = "Terrain",
                InstanceType = "TerrainManager",
                Address = $"0x{(nint)terrain:X}",
                LayerAddress = "Terrain",
                Position = position,
                Rotation = "Identity",
                Scale = Vector3.One,
                ResourcePath = path,
                Visible = true,
                LayerId = "Terrain",
                GroupId = terrainKey.ToString(),
                DistanceToPlayer = Vector3.Distance(searchOrigin, position),
                Source = $"{source} -> LayoutManager.Terrains",
                DebugInfo = $"GfxTerrain=0x{(nint)terrain->GfxTerrain:X}",
            });
        }
    }

    private void ParseInstanceTree(
        ILayoutInstance* instanceLayout,
        Vector3 searchOrigin,
        List<LayoutProbeInstance> results,
        ref int index,
        string source,
        string sourceKind,
        string sharedGroupPath,
        string parentAddress,
        string parentKey,
        int childIndex,
        int depth)
    {
        if (instanceLayout == null)
            return;

        if (depth > 4)
            return;

        var type = instanceLayout->Id.Type;
        if (type == InstanceType.SharedGroup)
        {
            var sharedGroup = (SharedGroupLayoutInstance*)instanceLayout;
            var parent = this.ParseSharedGroup(
                sharedGroup,
                searchOrigin,
                index,
                source,
                sourceKind,
                sharedGroupPath,
                parentAddress,
                parentKey,
                childIndex);
            if (parent != null)
            {
                results.Add(parent);
                index++;
            }

            this.ParseSharedGroupChildren(sharedGroup, searchOrigin, results, ref index, source, depth);
            return;
        }

        var parsed = this.ParseInstance(
            instanceLayout,
            searchOrigin,
            index,
            source,
            sourceKind,
            sharedGroupPath,
            parentAddress,
            parentKey,
            childIndex);
        if (parsed == null)
            return;

        results.Add(parsed);
        index++;
    }

    private LayoutProbeInstance? ParseInstance(
        ILayoutInstance* instanceLayout,
        Vector3 searchOrigin,
        int index,
        string source,
        string sourceKind,
        string sharedGroupPath,
        string parentAddress,
        string parentKey,
        int childIndex)
    {
        if (instanceLayout == null)
            return null;

        var type = instanceLayout->Id.Type;
        return type switch
        {
            InstanceType.BgPart when this.ShowBgPart => ParseBgPart((BgPartsLayoutInstance*)instanceLayout, searchOrigin, index, source, sourceKind, sharedGroupPath, parentAddress, parentKey, childIndex),
            InstanceType.SharedGroup when this.ShowSharedGroup => ParseSharedGroup((SharedGroupLayoutInstance*)instanceLayout, searchOrigin, index, source, sourceKind, sharedGroupPath, parentAddress, parentKey, childIndex),
            InstanceType.Light when this.ShowLight => ParseLight(instanceLayout, searchOrigin, index, source, sourceKind, sharedGroupPath, parentAddress, parentKey, childIndex),
            _ => null,
        };
    }

    private LayoutProbeInstance? ParseBgPart(
        BgPartsLayoutInstance* bgPart,
        Vector3 searchOrigin,
        int index,
        string source,
        string sourceKind,
        string sharedGroupPath,
        string parentAddress,
        string parentKey,
        int childIndex)
    {
        if (bgPart == null)
            return null;

        var transform = bgPart->GetTransformImpl();
        if (transform == null)
            return null;

        var path = ReadPrimaryPath((ILayoutInstance*)bgPart);
        var layoutPosition = transform->Translation;
        var layoutRotation = transform->Rotation;
        var layoutScale = transform->Scale;
        var position = layoutPosition;
        var rotation = layoutRotation;
        var scale = layoutScale;
        var transformSource = "LayoutInstance";
        var visible = bgPart->GraphicsObject != null && bgPart->GraphicsObject->IsVisible;
        if (bgPart->GraphicsObject != null)
        {
            try
            {
                var sceneObject = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)bgPart->GraphicsObject;
                position = sceneObject->Position;
                rotation = sceneObject->Rotation;
                scale = sceneObject->Scale;
                transformSource = "GraphicsObject";
            }
            catch
            {
                position = layoutPosition;
                rotation = layoutRotation;
                scale = layoutScale;
                transformSource = "LayoutInstanceFallback";
            }
        }

        var modelPath = ReadBgPartModelPath(bgPart);
        var modelHandle = ReadBgPartModelHandle(bgPart);
        var stableKey = BuildStableLayoutKey("BgPart", sourceKind, source, path, sharedGroupPath, parentKey, childIndex);

        return new LayoutProbeInstance
            {
                Index = index,
                Key = $"BgPart:{(nint)bgPart:X}",
                StableKey = stableKey,
                Type = "BgPart",
                InstanceType = bgPart->Id.Type.ToString(),
                Address = $"0x{(nint)bgPart:X}",
                LayerAddress = $"0x{(nint)bgPart->Layer:X}",
            Position = position,
            RotationQuaternion = rotation,
            Rotation = rotation.ToString(),
            Scale = scale,
            ResourcePath = path,
            ModelResourcePath = modelPath,
            Visible = visible,
            LayerId = "LayerManager",
            GroupId = string.IsNullOrWhiteSpace(parentAddress) ? "BgPart" : "SharedGroupChild",
            DistanceToPlayer = Vector3.Distance(searchOrigin, position),
            Source = $"{source} -> BgPartsLayoutInstance",
            SourceKind = sourceKind,
            SharedGroupPath = sharedGroupPath,
            ParentAddress = parentAddress,
            ParentKey = parentKey,
            ChildIndex = childIndex,
            DebugInfo = $"GraphicsObject=0x{(nint)bgPart->GraphicsObject:X}; ModelResourceHandle={modelHandle}; modelResourcePath={modelPath}; transformSource={transformSource}; layoutPosition={layoutPosition}; visualPosition={position}; sourceKind={sourceKind}; sharedGroupPath={sharedGroupPath}; parent={parentAddress}; parentKey={parentKey}; childIndex={childIndex}; stableKey={stableKey}",
        };
    }

    private LayoutProbeInstance? ParseSharedGroup(
        SharedGroupLayoutInstance* sharedGroup,
        Vector3 searchOrigin,
        int index,
        string source,
        string sourceKind,
        string parentSharedGroupPath,
        string parentAddress,
        string parentKey,
        int childIndex)
    {
        if (sharedGroup == null)
            return null;

        var transform = sharedGroup->GetTransformImpl();
        if (transform == null)
            return null;

        var path = ReadPrimaryPath((ILayoutInstance*)sharedGroup);
        var position = transform->Translation;
        var stableKey = BuildStableLayoutKey("SharedGroup", sourceKind, source, path, parentSharedGroupPath, parentKey, childIndex);
        var childCount = 0;
        try
        {
            foreach (var _ in sharedGroup->Instances.Instances)
                childCount++;
        }
        catch
        {
        }

        return new LayoutProbeInstance
            {
                Index = index,
                Key = $"SharedGroup:{(nint)sharedGroup:X}",
                StableKey = stableKey,
                Type = "SharedGroup",
                InstanceType = sharedGroup->Id.Type.ToString(),
                Address = $"0x{(nint)sharedGroup:X}",
                LayerAddress = $"0x{(nint)sharedGroup->Layer:X}",
            Position = position,
            RotationQuaternion = transform->Rotation,
            Rotation = transform->Rotation.ToString(),
            Scale = transform->Scale,
            ResourcePath = path,
            Visible = true,
            LayerId = "LayerManager",
            GroupId = "SharedGroup",
            DistanceToPlayer = Vector3.Distance(searchOrigin, position),
            Source = $"{source} -> SharedGroupLayoutInstance",
            SourceKind = sourceKind,
            SharedGroupPath = path,
            ParentAddress = parentAddress,
            ParentKey = parentKey,
            ChildIndex = childIndex,
            DebugInfo = $"Children={childCount}; sharedGroupPath={path}; parentSharedGroupPath={parentSharedGroupPath}; parent={parentAddress}; parentKey={parentKey}; childIndex={childIndex}; stableKey={stableKey}",
        };
    }

    private void ParseSharedGroupChildren(SharedGroupLayoutInstance* sharedGroup, Vector3 searchOrigin, List<LayoutProbeInstance> results, ref int index, string source, int depth)
    {
        if (sharedGroup == null || depth > 4)
            return;

        var sharedGroupPath = ReadPrimaryPath((ILayoutInstance*)sharedGroup);
        var parentAddress = $"0x{(nint)sharedGroup:X}";
        var parentKey = $"SharedGroup:{(nint)sharedGroup:X}";
        var childIndex = 0;
        try
        {
            foreach (var instanceDataPtr in sharedGroup->Instances.Instances)
            {
                if (instanceDataPtr == null || instanceDataPtr.Value == null)
                    continue;

                var childNode = instanceDataPtr.Value;
                var childInstance = childNode->Instance;
                if (childInstance == null)
                    continue;

                ParseInstanceTree(
                    childInstance,
                    searchOrigin,
                    results,
                    ref index,
                    $"{source}; SharedGroup={parentAddress}; Child={childIndex}",
                    sourceKind: "SharedGroup",
                    sharedGroupPath,
                    parentAddress,
                    parentKey,
                    childIndex,
                    depth + 1);
                childIndex++;
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Failed to expand SharedGroup children.");
        }
    }

    private LayoutProbeInstance? ParseLight(
        ILayoutInstance* light,
        Vector3 searchOrigin,
        int index,
        string source,
        string sourceKind,
        string sharedGroupPath,
        string parentAddress,
        string parentKey,
        int childIndex)
    {
        if (light == null)
            return null;

        var transform = light->GetTransformImpl();
        if (transform == null)
            return null;

        var position = transform->Translation;
        var path = ReadPrimaryPath((ILayoutInstance*)light);
        var stableKey = BuildStableLayoutKey("Light", sourceKind, source, path, sharedGroupPath, parentKey, childIndex);
        return new LayoutProbeInstance
            {
                Index = index,
                Key = $"Light:{(nint)light:X}",
                StableKey = stableKey,
                Type = "Light",
                InstanceType = light->Id.Type.ToString(),
                Address = $"0x{(nint)light:X}",
                LayerAddress = $"0x{(nint)light->Layer:X}",
            Position = position,
            RotationQuaternion = transform->Rotation,
            Rotation = transform->Rotation.ToString(),
            Scale = transform->Scale,
            ResourcePath = path,
            Visible = true,
            LayerId = "LayerManager",
            GroupId = "Light",
            DistanceToPlayer = Vector3.Distance(searchOrigin, position),
            Source = $"{source} -> LightLayoutInstance",
            SourceKind = sourceKind,
            SharedGroupPath = sharedGroupPath,
            ParentAddress = parentAddress,
            ParentKey = parentKey,
            ChildIndex = childIndex,
            DebugInfo = "当前 FFXIVClientStructs 未公开 LightLayoutInstance 类型，按 ILayoutInstance 只读显示。",
        };
    }

    private void AddCamera(List<LayoutProbeInstance> results, ref int index, Vector3 searchOrigin)
    {
        try
        {
            var manager = CameraManager.Instance();
            if (manager == null || manager->CurrentCamera == null)
                return;

            var camera = manager->CurrentCamera;
            var position = camera->Position;
            results.Add(new LayoutProbeInstance
            {
                Index = index++,
                Key = $"Camera:{(nint)camera:X}",
                Type = "Camera",
                InstanceType = "Camera",
                Address = $"0x{(nint)camera:X}",
                LayerAddress = "CameraManager",
                Position = position,
                Rotation = camera->Rotation.ToString(),
                Scale = camera->Scale,
                ResourcePath = "CurrentCamera",
                Visible = true,
                LayerId = "Graphics.Scene",
                GroupId = "Camera",
                DistanceToPlayer = Vector3.Distance(searchOrigin, position),
                Source = "CameraManager.CurrentCamera",
                DebugInfo = $"RenderCamera=0x{(nint)camera->RenderCamera:X}; LookAt={camera->LookAtVector}",
            });
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Failed to add camera layout probe item.");
        }
    }

    private void AddCharacters(List<LayoutProbeInstance> results, ref int index, Vector3 searchOrigin)
    {
        try
        {
            var manager = GameObjectManager.Instance();
            if (manager == null)
                return;

            for (var idx = 0; idx < manager->Objects.GameObjectIdSorted.Length; idx++)
            {
                var objectPtr = manager->Objects.GameObjectIdSorted[idx];
                if (objectPtr == null || objectPtr.Value == null)
                    continue;

                var obj = objectPtr.Value;
                if (!IsCharacterKind(obj->ObjectKind) || obj->DrawObject == null)
                    continue;

                var drawObject = obj->DrawObject;
                var position = drawObject->Position;
                results.Add(new LayoutProbeInstance
                {
                    Index = index++,
                    Key = $"Character:{(nint)obj:X}",
                    Type = "Character",
                    InstanceType = obj->ObjectKind.ToString(),
                    Address = $"0x{(nint)obj:X}",
                    LayerAddress = "GameObjectManager",
                    Position = position,
                    Rotation = drawObject->Rotation.ToString(),
                    Scale = drawObject->Scale,
                    ResourcePath = $"Character DrawObject ({obj->ObjectKind})",
                    Visible = drawObject->IsVisible,
                    LayerId = "GameObjectManager",
                    GroupId = "Character",
                    DistanceToPlayer = Vector3.Distance(searchOrigin, position),
                    Source = "GameObjectManager.Objects.GameObjectIdSorted (optional Character)",
                    DebugInfo = $"Name={obj->NameString}; ObjectKind={obj->ObjectKind}; DrawObject=0x{(nint)drawObject:X}",
                });
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Failed to add optional character layout probe items.");
        }
    }

    private static bool IsCharacterKind(ObjectKind kind)
        => kind is ObjectKind.Pc or ObjectKind.Mount or ObjectKind.Companion or ObjectKind.Retainer or ObjectKind.BattleNpc or ObjectKind.EventNpc or ObjectKind.Ornament;

    private static string ReadPrimaryPath(ILayoutInstance* instance)
    {
        try
        {
            var path = instance->GetPrimaryPath();
            return path.HasValue ? path.ToString() : "无 primary path";
        }
        catch (Exception ex)
        {
            return $"读取 primary path 失败：{ex.Message}";
        }
    }

    private static string ReadBgPartModelHandle(BgPartsLayoutInstance* bgPart)
    {
        try
        {
            if (bgPart->GraphicsObject == null)
                return "GraphicsObject=null";

            var graphics = (MeddleBgObject*)bgPart->GraphicsObject;
            if (graphics->ModelResourceHandle == null)
                return "ModelResourceHandle=null";

            var fileName = graphics->ModelResourceHandle->FileName.ToString();
            return $"0x{(nint)graphics->ModelResourceHandle:X}; FileName={fileName}; LoadState={graphics->ModelResourceHandle->LoadState}";
        }
        catch (Exception ex)
        {
            return $"读取 ModelResourceHandle 失败：{ex.Message}";
        }
    }

    private static string ReadBgPartModelPath(BgPartsLayoutInstance* bgPart)
    {
        try
        {
            if (bgPart->GraphicsObject == null)
                return string.Empty;

            var graphics = (MeddleBgObject*)bgPart->GraphicsObject;
            return graphics->ModelResourceHandle == null
                ? string.Empty
                : graphics->ModelResourceHandle->FileName.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeType(IReadOnlyList<Type> types, params string[] keywords)
    {
        var matches = types
            .Where(type => keywords.Any(keyword => type.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Take(40)
            .Select(type =>
            {
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property or MemberTypes.Method)
                    .Select(member => $"{member.MemberType}:{member.Name}")
                    .Take(35);
                return $"{type.FullName}{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", members)}";
            });

        return string.Join(Environment.NewLine + Environment.NewLine, matches);
    }

    [StructLayout(LayoutKind.Explicit, Size = 0xD0)]
    private unsafe struct MeddleBgObject
    {
        [FieldOffset(0x90)] public ModelResourceHandle* ModelResourceHandle;
    }
}
