using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;

namespace LocalQuestReborn.Services;

public sealed class BrioCapabilityBridgeService
{
    private readonly IPluginLog log;

    public BrioCapabilityBridgeService(IPluginLog log)
    {
        this.log = log;
        this.RefreshDebugTypes();
    }

    public string LastMoveError { get; private set; } = string.Empty;

    public string LastMoveMethod { get; private set; } = "未移动";

    public IReadOnlyList<string> DebugTypeNames { get; private set; } = [];

    public void RefreshDebugTypes()
    {
        var assembly = FindBrioAssembly();
        if (assembly == null)
        {
            this.DebugTypeNames = ["未发现 Brio assembly。"];
            return;
        }

        this.DebugTypeNames = assembly.GetTypes()
            .Where(type =>
                type.FullName?.Contains("EntityManager", StringComparison.OrdinalIgnoreCase) == true ||
                type.FullName?.Contains("PosingCapability", StringComparison.OrdinalIgnoreCase) == true ||
                type.FullName?.Contains("ModelPosing", StringComparison.OrdinalIgnoreCase) == true ||
                type.FullName?.Contains("Transform", StringComparison.OrdinalIgnoreCase) == true)
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryMoveActor(RuntimeActorInstance actor, Vector3 position, out string reason)
    {
        reason = "Brio Capability 移动失败，需 native SetPosition 路径。已禁用 capability-only 移动，避免 actor 消失。";
        return this.Fail(actor, reason);
    }

    public bool TrySyncTransformAfterNativeMove(RuntimeActorInstance actor, Vector3 worldPosition, out string reason)
    {
        try
        {
            if (actor.CharacterObject == null)
            {
                reason = "characterObject 不可用。";
                return this.Fail(actor, reason);
            }

            if (!this.TryGetModelPosing(actor, out var modelPosing, out reason) || modelPosing == null)
                return this.Fail(actor, reason);

            var transformProperty = modelPosing.GetType().GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public);
            if (transformProperty == null || !transformProperty.CanWrite)
                return this.Fail(actor, "ModelPosing.Transform 不可写。", out reason);

            var transform = transformProperty.GetValue(modelPosing) ?? Activator.CreateInstance(transformProperty.PropertyType);
            if (transform == null)
                return this.Fail(actor, "无法创建 Brio Transform。", out reason);

            SetTransformFieldOrProperty(transform, "Position", worldPosition);
            transformProperty.SetValue(modelPosing, transform);

            actor.LastMoveMethod = "Native SetPosition + Brio Transform 同步";
            this.LastMoveMethod = actor.LastMoveMethod;
            this.LastMoveError = string.Empty;
            reason = "已在 native SetPosition 后同步 Brio PosingCapability.Transform.Position。";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to sync Brio transform after native move. RuntimeId={RuntimeId}", actor.RuntimeId);
            reason = $"Brio Transform 同步失败：{ex.Message}";
            this.LastMoveMethod = "Native SetPosition";
            this.LastMoveError = reason;
            return false;
        }
    }

    public bool TryReadModelTransform(RuntimeActorInstance actor, out string reason)
    {
        try
        {
            if (actor.CharacterObject == null)
                return this.Fail(actor, "characterObject 不可用。", out reason);

            if (!this.TryGetModelPosing(actor, out var modelPosing, out reason) || modelPosing == null)
                return this.Fail(actor, reason);

            var transformProperty = modelPosing.GetType().GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public);
            var transform = transformProperty?.GetValue(modelPosing);
            if (transform == null)
                return this.Fail(actor, "ModelPosing.Transform 不可读取。", out reason);

            if (TryGetTransformFieldOrProperty(transform, "Position", out Vector3 position))
                actor.LastKnownPosition = position;
            if (TryGetTransformFieldOrProperty(transform, "Rotation", out Quaternion rotation))
            {
                actor.LastKnownRotation = Normalize(rotation);
                actor.LastKnownRotationEuler = QuaternionToEuler(actor.LastKnownRotation);
            }
            if (TryGetTransformFieldOrProperty(transform, "Scale", out Vector3 scale))
                actor.LastKnownScale = NormalizeScale(scale);

            actor.TransformEditPosition = actor.LastKnownPosition;
            actor.TransformEditRotationEuler = actor.LastKnownRotationEuler;
            actor.TransformEditScale = actor.LastKnownScale;
            actor.LastTransformReadback = $"position={actor.LastKnownPosition}; rotationEuler={actor.LastKnownRotationEuler}; scale={actor.LastKnownScale}";
            actor.LastTransformError = string.Empty;
            reason = $"已读取 Brio ModelPosing.Transform：{actor.LastTransformReadback}";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to read Brio model transform. RuntimeId={RuntimeId}", actor.RuntimeId);
            reason = $"读取 Brio Transform 失败：{ex.Message}";
            actor.LastTransformError = reason;
            return false;
        }
    }

    public bool TryApplyModelTransform(RuntimeActorInstance actor, Vector3 position, Vector3 rotationEuler, Vector3 scale, out string reason)
    {
        try
        {
            if (actor.CharacterObject == null)
                return this.Fail(actor, "characterObject 不可用。", out reason);

            if (!this.TryGetModelPosing(actor, out var modelPosing, out reason) || modelPosing == null)
                return this.Fail(actor, reason);

            var transformProperty = modelPosing.GetType().GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public);
            if (transformProperty == null || !transformProperty.CanWrite)
                return this.Fail(actor, "ModelPosing.Transform 不可写。", out reason);

            var transform = transformProperty.GetValue(modelPosing) ?? Activator.CreateInstance(transformProperty.PropertyType);
            if (transform == null)
                return this.Fail(actor, "无法创建 Brio Transform。", out reason);

            var normalizedScale = NormalizeScale(scale);
            var rotation = Normalize(Quaternion.CreateFromYawPitchRoll(rotationEuler.Y, rotationEuler.X, rotationEuler.Z));
            SetTransformFieldOrProperty(transform, "Position", position);
            SetTransformFieldOrProperty(transform, "Rotation", rotation);
            SetTransformFieldOrProperty(transform, "Scale", normalizedScale);
            transformProperty.SetValue(modelPosing, transform);

            actor.LastKnownPosition = position;
            actor.LastKnownRotation = rotation;
            actor.LastKnownRotationEuler = rotationEuler;
            actor.LastKnownScale = normalizedScale;
            actor.TransformEditPosition = position;
            actor.TransformEditRotationEuler = rotationEuler;
            actor.TransformEditScale = normalizedScale;
            actor.LastTransformReadback = $"position={position}; rotationEuler={rotationEuler}; scale={normalizedScale}";
            actor.LastTransformError = string.Empty;
            actor.LastMoveMethod = "Native SetPosition + Brio ModelPosing.Transform";
            this.LastMoveMethod = actor.LastMoveMethod;
            this.LastMoveError = string.Empty;
            reason = $"已应用 Brio ModelPosing.Transform：{actor.LastTransformReadback}";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to apply Brio model transform. RuntimeId={RuntimeId}", actor.RuntimeId);
            reason = $"应用 Brio Transform 失败：{ex.Message}";
            actor.LastTransformError = reason;
            this.LastMoveError = reason;
            return false;
        }
    }

    private bool TryGetModelPosing(RuntimeActorInstance actor, out object? modelPosing, out string reason)
    {
        modelPosing = null;
        reason = string.Empty;

        var brioAssembly = FindBrioAssembly();
        if (brioAssembly == null)
        {
            reason = "未发现 Brio assembly。";
            return false;
        }

        this.RefreshDebugTypes();
        var entityManager = this.ResolveEntityManager(brioAssembly, out reason);
        if (entityManager == null)
            return false;

        var setSelectedEntity = entityManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (method.Name != "SetSelectedEntity")
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(actor.CharacterObject);
            });

        if (setSelectedEntity == null)
        {
            reason = "未找到 EntityManager.SetSelectedEntity(characterObject) 可用重载。";
            return false;
        }

        setSelectedEntity.Invoke(entityManager, [actor.CharacterObject]);

        var posingCapabilityType = brioAssembly.GetTypes()
            .FirstOrDefault(type => type.FullName?.EndsWith(".PosingCapability", StringComparison.OrdinalIgnoreCase) == true);
        if (posingCapabilityType == null)
        {
            reason = "未找到 PosingCapability 类型。";
            return false;
        }

        var tryGetCapability = entityManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "TryGetCapabilityFromSelectedEntity" && method.IsGenericMethodDefinition);
        if (tryGetCapability == null)
        {
            reason = "未找到 TryGetCapabilityFromSelectedEntity<T>。";
            return false;
        }

        var args = new object?[] { null, false, true };
        var result = tryGetCapability.MakeGenericMethod(posingCapabilityType).Invoke(entityManager, args);
        if (result is not bool gotCapability || !gotCapability || args[0] == null)
        {
            reason = $"无法从选中 Entity 获取 PosingCapability。result={result ?? "null"}";
            return false;
        }

        var posing = args[0]!;
        modelPosing = posing.GetType().GetProperty("ModelPosing", BindingFlags.Instance | BindingFlags.Public)?.GetValue(posing);
        if (modelPosing == null)
        {
            reason = "PosingCapability.ModelPosing 不可用。";
            return false;
        }

        return true;
    }

    private object? ResolveEntityManager(Assembly brioAssembly, out string reason)
    {
        reason = string.Empty;
        var accessUtils = brioAssembly.GetType("Brio.BrioAccessUtils");
        var entityManager = accessUtils?.GetProperty("EntityManager", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        if (entityManager != null)
            return entityManager;

        var brioType = brioAssembly.GetType("Brio.Brio");
        var entityManagerType = brioAssembly.GetType("Brio.Entities.EntityManager") ??
            brioAssembly.GetTypes().FirstOrDefault(type => type.FullName?.EndsWith(".EntityManager", StringComparison.OrdinalIgnoreCase) == true);
        if (brioType == null || entityManagerType == null)
        {
            reason = $"无法找到 Brio.Brio 或 EntityManager。Brio.Brio={brioType != null}, EntityManager={entityManagerType != null}";
            return null;
        }

        var tryGetService = brioType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "TryGetService" && method.IsGenericMethodDefinition);
        if (tryGetService == null)
        {
            reason = "未找到 Brio.Brio.TryGetService<T>。";
            return null;
        }

        var args = new object?[] { null };
        var result = tryGetService.MakeGenericMethod(entityManagerType).Invoke(null, args);
        if (result is bool success && success && args[0] != null)
            return args[0];

        reason = "Brio.Brio.TryGetService<EntityManager> 返回 false 或 null。";
        return null;
    }

    private bool Fail(RuntimeActorInstance actor, string reason)
    {
        actor.LastMoveMethod = "Failed";
        actor.LastMoveError = reason;
        actor.LastError = reason;
        this.LastMoveMethod = "Failed";
        this.LastMoveError = reason;
        return false;
    }

    private bool Fail(RuntimeActorInstance actor, string reason, out string outReason)
    {
        outReason = reason;
        return this.Fail(actor, reason);
    }

    private static void SetTransformFieldOrProperty(object transform, string name, object value)
    {
        var type = transform.GetType();
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property != null && property.CanWrite)
        {
            property.SetValue(transform, value);
            return;
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        field?.SetValue(transform, value);
    }

    private static bool TryGetTransformFieldOrProperty<T>(object transform, string name, out T value)
    {
        value = default!;
        var type = transform.GetType();
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(transform) is T propertyValue)
        {
            value = propertyValue;
            return true;
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        if (field?.GetValue(transform) is T fieldValue)
        {
            value = fieldValue;
            return true;
        }

        return false;
    }

    private static Vector3 NormalizeScale(Vector3 scale)
        => new(
            MathF.Max(0.01f, float.IsFinite(scale.X) ? scale.X : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Y) ? scale.Y : 1f),
            MathF.Max(0.01f, float.IsFinite(scale.Z) ? scale.Z : 1f));

    private static Quaternion Normalize(Quaternion rotation)
    {
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
            return Quaternion.Identity;

        return rotation.LengthSquared() < 0.0001f ? Quaternion.Identity : Quaternion.Normalize(rotation);
    }

    private static Vector3 QuaternionToEuler(Quaternion q)
    {
        q = Normalize(q);
        var sinrCosp = 2f * (q.W * q.X + q.Y * q.Z);
        var cosrCosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        var pitch = MathF.Atan2(sinrCosp, cosrCosp);

        var sinp = 2f * (q.W * q.Y - q.Z * q.X);
        var yaw = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);

        var sinyCosp = 2f * (q.W * q.Z + q.X * q.Y);
        var cosyCosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        var roll = MathF.Atan2(sinyCosp, cosyCosp);

        return new Vector3(pitch, yaw, roll);
    }

    private static Assembly? FindBrioAssembly()
        => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name?.Equals("Brio", StringComparison.OrdinalIgnoreCase) == true);
}
