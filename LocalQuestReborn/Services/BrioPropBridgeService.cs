using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class BrioPropBridgeService
{
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;

    public BrioPropBridgeService(BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public string LastMessage { get; private set; } = "Brio Prop bridge 尚未调用。";

    public string LastMethod { get; private set; } = "未调用";

    public string LastPropDataFields { get; private set; } = "未构造";

    public bool TryLoadProp(CustomProp prop, string runtimeId, out RuntimePropInstance instance, out string reason)
    {
        instance = CreateBaseInstance(prop, runtimeId);

        try
        {
            if (!this.TryCreateViaActorContainerCapability(prop, instance, out reason) &&
                !this.TryCreateViaSpawnNewProp(prop, instance, out reason))
            {
                instance.LastError = reason;
                this.LastMessage = reason;
                return false;
            }

            this.brioAssemblyBridge.RefreshProp(instance);
            this.ClassifyResult(instance);
            reason = instance.IsBrioProp
                ? $"Brio PropData 模式已生成 Brio Prop：method={instance.SpawnMethod}，objectIndex={instance.ObjectIndex}"
                : $"Brio 返回对象不是 Prop：{DescribeCloneFailure(instance)}";
            instance.LastError = instance.IsBrioProp ? string.Empty : "这不是 Prop，只是 Character clone。";
            this.LastMessage = reason;
            return instance.IsBrioProp;
        }
        catch (Exception ex)
        {
            reason = $"Brio PropData 模式失败：{ex.Message}";
            instance.LastError = reason;
            this.LastMessage = reason;
            this.log.Error(ex, "Brio Prop bridge failed. PropId={PropId}", prop.Id);
            return false;
        }
    }

    public bool TryDespawn(RuntimePropInstance instance, out string reason)
        => this.brioAssemblyBridge.TryDespawnProp(instance, out reason);

    private bool TryCreateViaActorContainerCapability(CustomProp prop, RuntimePropInstance instance, out string reason)
    {
        reason = string.Empty;
        var brioAssembly = FindBrioAssembly();
        if (brioAssembly == null)
        {
            reason = "未发现 Brio assembly。";
            return false;
        }

        var entityManagerType = brioAssembly.GetType("Brio.Entities.EntityManager");
        var actorContainerEntityType = brioAssembly.GetType("Brio.Entities.Actor.ActorContainerEntity");
        var actorContainerCapabilityType = brioAssembly.GetType("Brio.Capabilities.Actor.ActorContainerCapability");
        if (entityManagerType == null || actorContainerEntityType == null || actorContainerCapabilityType == null)
        {
            reason = "Brio EntityManager / ActorContainerEntity / ActorContainerCapability 类型不完整，无法走 Scene/ActorContainer Prop 路线。";
            return false;
        }

        if (!TryGetBrioService(entityManagerType, out var entityManager, out reason) || entityManager == null)
            return false;

        var getEntity = entityManagerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "GetEntity" &&
                                      method.IsGenericMethodDefinition &&
                                      method.GetParameters().Length == 1 &&
                                      method.GetParameters()[0].ParameterType.Name.Contains("EntityId", StringComparison.OrdinalIgnoreCase));
        if (getEntity == null)
        {
            reason = "未找到 EntityManager.GetEntity<T>(EntityId) 泛型方法。";
            return false;
        }

        var entityIdType = brioAssembly.GetType("Brio.Entities.Core.EntityId");
        if (entityIdType == null)
        {
            reason = "未找到 Brio.Entities.Core.EntityId。";
            return false;
        }

        var actorContainerId = Activator.CreateInstance(entityIdType, "actorContainer");
        var actorContainer = getEntity.MakeGenericMethod(actorContainerEntityType).Invoke(entityManager, [actorContainerId]);
        if (actorContainer == null)
        {
            reason = "Brio actorContainer entity 不存在，可能 Brio 场景/GPose entity 尚未初始化。";
            return false;
        }

        var getCapability = actorContainer.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "GetCapability" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
        if (getCapability == null)
        {
            reason = "ActorContainerEntity 未暴露 GetCapability<T>()。";
            return false;
        }

        var capability = getCapability.MakeGenericMethod(actorContainerCapabilityType).Invoke(actorContainer, null);
        if (capability == null)
        {
            reason = "无法取得 ActorContainerCapability。";
            return false;
        }

        var createProp = actorContainerCapabilityType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "CreateProp" && method.GetParameters().Length == 1);
        if (createProp == null)
        {
            reason = "ActorContainerCapability.CreateProp(bool) 不存在。";
            return false;
        }

        var result = createProp.Invoke(capability, [false]);
        var character = ExtractTupleItem(result, "Item2");
        if (character == null)
        {
            reason = $"CreateProp 返回值无法读取 ICharacter。Result={result ?? "null"}";
            return false;
        }

        instance.CharacterObject = character;
        instance.SpawnMethod = "Brio ActorContainerCapability.CreateProp(false)";
        instance.PropDataFields = CreatePropDataDescription(prop);
        this.LastMethod = instance.SpawnMethod;
        this.LastPropDataFields = instance.PropDataFields;
        return true;
    }

    private bool TryCreateViaSpawnNewProp(CustomProp prop, RuntimePropInstance instance, out string reason)
    {
        reason = string.Empty;
        var brioAssembly = FindBrioAssembly();
        var actorSpawnServiceType = brioAssembly?.GetType("Brio.Game.Actor.ActorSpawnService");
        if (actorSpawnServiceType == null)
        {
            reason = "未找到 ActorSpawnService 类型。";
            return false;
        }

        if (!TryGetBrioService(actorSpawnServiceType, out var actorSpawnService, out reason) || actorSpawnService == null)
            return false;

        var spawnNewProp = actorSpawnServiceType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "SpawnNewProp" &&
                                      method.GetParameters().Length == 1 &&
                                      (method.GetParameters()[0].IsOut || method.GetParameters()[0].ParameterType.IsByRef));
        if (spawnNewProp == null)
        {
            reason = "ActorSpawnService.SpawnNewProp(out ICharacter) 不存在。";
            return false;
        }

        object?[] args = [null];
        var result = spawnNewProp.Invoke(actorSpawnService, args);
        var success = result is bool value && value;
        if (!success || args[0] == null)
        {
            reason = $"SpawnNewProp 调用失败。Result={result ?? "null"}，CharacterIsNull={args[0] == null}";
            return false;
        }

        instance.CharacterObject = args[0];
        instance.SpawnMethod = "Brio ActorSpawnService.SpawnNewProp(out ICharacter)";
        instance.PropDataFields = CreatePropDataDescription(prop);
        this.LastMethod = instance.SpawnMethod;
        this.LastPropDataFields = instance.PropDataFields;
        return true;
    }

    private void ClassifyResult(RuntimePropInstance instance)
    {
        var character = instance.CharacterObject;
        instance.ObjectType = character?.GetType().FullName ?? "null";
        instance.IsCharacterClone = instance.ObjectType.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
                                    instance.ObjectType.Contains("Npc", StringComparison.OrdinalIgnoreCase) ||
                                    instance.ObjectType.Contains("Chara", StringComparison.OrdinalIgnoreCase);
        instance.IsBrioProp = false;

        var brioAssembly = FindBrioAssembly();
        var entityManagerType = brioAssembly?.GetType("Brio.Entities.EntityManager");
        if (brioAssembly == null || entityManagerType == null || character == null)
            return;

        try
        {
            if (!TryGetBrioService(entityManagerType, out var entityManager, out _) || entityManager == null)
                return;

            var entityIdType = brioAssembly.GetType("Brio.Entities.Core.EntityId");
            var actorEntityType = brioAssembly.GetType("Brio.Entities.Actor.ActorEntity");
            if (entityIdType == null || actorEntityType == null)
                return;

            var entityId = Activator.CreateInstance(entityIdType, character);
            var getEntity = entityManagerType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "GetEntity" &&
                                          method.IsGenericMethodDefinition &&
                                          method.GetParameters().Length == 1 &&
                                          method.GetParameters()[0].ParameterType.Name.Contains("EntityId", StringComparison.OrdinalIgnoreCase));
            var actorEntity = getEntity?.MakeGenericMethod(actorEntityType).Invoke(entityManager, [entityId]);
            var isProp = actorEntity?.GetType().GetProperty("IsProp", BindingFlags.Instance | BindingFlags.Public)?.GetValue(actorEntity);
            if (isProp is bool propValue)
                instance.IsBrioProp = propValue;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to classify Brio prop result.");
        }
    }

    private static RuntimePropInstance CreateBaseInstance(CustomProp prop, string runtimeId)
        => new()
        {
            RuntimeId = runtimeId,
            PropId = prop.Id,
            PropName = prop.Name,
            ModelPath = prop.ModelPath,
            Position = new Vector3(prop.Position.X, prop.Position.Y, prop.Position.Z),
            Rotation = prop.Rotation,
            Scale = prop.Scale,
            SpawnMethod = "Brio PropData 模式",
        };

    private static string CreatePropDataDescription(CustomProp prop)
        => $"PropData.PropTransformDifference/Absolute 可表达 Transform；Brio Prop 数据不接受 bg mdl path。当前 modelPath={prop.ModelPath} 仅作为 Raw mdl path 实验字段。";

    private static string DescribeCloneFailure(RuntimePropInstance instance)
        => $"objectType={instance.ObjectType}，objectIndex={instance.ObjectIndex}，IsBrioProp={instance.IsBrioProp}。这不是 Prop，只是 Character clone。";

    private static object? ExtractTupleItem(object? source, string propertyName)
        => source?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(source);

    private static Assembly? FindBrioAssembly()
        => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name?.Equals("Brio", StringComparison.OrdinalIgnoreCase) == true);

    private static bool TryGetBrioService(Type serviceType, out object? service, out string reason)
    {
        service = null;
        reason = string.Empty;
        var brioAssembly = FindBrioAssembly();
        var brioType = brioAssembly?.GetType("Brio.Brio");
        if (brioType == null)
        {
            reason = "未找到 Brio.Brio。";
            return false;
        }

        var tryGetService = brioType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "TryGetService" && method.IsGenericMethodDefinition);
        if (tryGetService == null)
        {
            reason = "未找到 Brio.Brio.TryGetService<T>()。";
            return false;
        }

        object?[] args = [null];
        var result = tryGetService.MakeGenericMethod(serviceType).Invoke(null, args);
        if (result is bool ok && ok && args[0] != null)
        {
            service = args[0];
            return true;
        }

        reason = $"Brio TryGetService<{serviceType.FullName}> 返回 false。";
        return false;
    }
}
