using Dalamud.Plugin.Services;
using Yourcraft.Models;
using System.Numerics;
using System.Reflection;

namespace Yourcraft.Services;

public sealed class BrioCapabilityBridgeService
{
    private readonly IPluginLog log;

    public BrioCapabilityBridgeService(IPluginLog log)
    {
        this.log = log;
        this.RefreshDebugTypes();
    }

    public string LastMoveError { get; private set; } = string.Empty;

    public string LastMoveMethod { get; private set; } = "Not moved";

    public IReadOnlyList<string> DebugTypeNames { get; private set; } = [];

    public void RefreshDebugTypes()
    {
        var assembly = FindBrioAssembly();
        if (assembly == null)
        {
            this.DebugTypeNames = ["Brio assembly not found."];
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
        return this.TryApplyModelTransform(actor, position, actor.TransformEditRotationEuler, actor.TransformEditScale, out reason);
    }

    public bool TrySyncTransformAfterNativeMove(RuntimeActorInstance actor, Vector3 worldPosition, out string reason)
    {
        try
        {
            if (actor.CharacterObject == null)
                return this.Fail(actor, "Character object is unavailable.", out reason);

            if (!this.TryGetModelPosing(actor, out var modelPosing, out reason) || modelPosing == null)
                return this.Fail(actor, reason);

            var transformProperty = modelPosing.GetType().GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public);
            if (transformProperty == null || !transformProperty.CanWrite)
                return this.Fail(actor, "ModelPosing.Transform is not writable.", out reason);

            var transform = transformProperty.GetValue(modelPosing) ?? Activator.CreateInstance(transformProperty.PropertyType);
            if (transform == null)
                return this.Fail(actor, "Unable to create Brio Transform.", out reason);

            var normalizedScale = NormalizeScale(actor.TransformEditScale == Vector3.Zero ? Vector3.One : actor.TransformEditScale);
            SetTransformFieldOrProperty(transform, "Position", worldPosition);
            SetTransformFieldOrProperty(transform, "Scale", normalizedScale);
            transformProperty.SetValue(modelPosing, transform);

            var nativeReason = this.TryApplyNativeRootTransform(actor, worldPosition, actor.TransformEditRotationEuler.Y, normalizedScale, out var nativeApplyReason)
                ? nativeApplyReason
                : $"native root skipped: {nativeApplyReason}";

            actor.LastKnownPosition = worldPosition;
            actor.TransformEditPosition = worldPosition;
            actor.LastKnownScale = normalizedScale;
            actor.TransformEditScale = normalizedScale;
            actor.LastTransformReadback = $"position={worldPosition}; yaw={actor.LastKnownRotationEuler.Y:F4}; uniformScale={normalizedScale.X:F4}";
            actor.LastTransformError = string.Empty;
            actor.LastMoveMethod = "Native root + Brio ModelPosing.Transform sync";
            this.LastMoveMethod = actor.LastMoveMethod;
            this.LastMoveError = string.Empty;
            reason = $"Synced Brio ModelPosing.Transform; {nativeReason}; {actor.LastTransformReadback}";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to sync Brio transform after native move. RuntimeId={RuntimeId}", actor.RuntimeId);
            reason = $"Brio transform sync failed: {ex.Message}";
            actor.LastTransformError = reason;
            actor.LastMoveMethod = "Failed";
            this.LastMoveMethod = "Failed";
            this.LastMoveError = reason;
            return false;
        }
    }

    public bool TryReadModelTransform(RuntimeActorInstance actor, out string reason)
        => this.TryReadModelTransform(actor, updateEditingTransform: true, out reason);

    public bool TryReadModelTransform(RuntimeActorInstance actor, bool updateEditingTransform, out string reason)
    {
        try
        {
            if (actor.CharacterObject == null)
                return this.Fail(actor, "Character object is unavailable.", out reason);

            if (!this.TryGetModelPosing(actor, out var modelPosing, out reason) || modelPosing == null)
                return this.Fail(actor, reason);

            var transformProperty = modelPosing.GetType().GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public);
            var transform = transformProperty?.GetValue(modelPosing);
            if (transform == null)
                return this.Fail(actor, "ModelPosing.Transform is unavailable.", out reason);

            if (TryGetTransformFieldOrProperty(transform, "Position", out Vector3 position))
                actor.LastKnownPosition = position;
            if (TryGetTransformFieldOrProperty(transform, "Rotation", out Quaternion rotation))
            {
                actor.LastKnownRotation = Normalize(rotation);
                actor.LastKnownRotationEuler = NormalizeRotation(QuaternionToEuler(actor.LastKnownRotation));
            }
            if (TryGetTransformFieldOrProperty(transform, "Scale", out Vector3 scale))
                actor.LastKnownScale = NormalizeScale(scale);

            if (updateEditingTransform)
            {
                actor.TransformEditPosition = actor.LastKnownPosition;
                actor.TransformEditRotationEuler = actor.LastKnownRotationEuler;
                actor.TransformEditScale = actor.LastKnownScale == Vector3.Zero ? Vector3.One : actor.LastKnownScale;
            }
            actor.LastTransformReadback = $"position={actor.LastKnownPosition}; yaw={actor.LastKnownRotationEuler.Y:F4}; uniformScale={actor.LastKnownScale.X:F4}";
            actor.LastTransformError = string.Empty;
            reason = $"Read Brio ModelPosing.Transform: {actor.LastTransformReadback}";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to read Brio model transform. RuntimeId={RuntimeId}", actor.RuntimeId);
            reason = $"Read Brio transform failed: {ex.Message}";
            actor.LastTransformError = reason;
            return false;
        }
    }

    public bool TryApplyModelTransform(RuntimeActorInstance actor, Vector3 position, Vector3 rotationEuler, Vector3 scale, out string reason)
    {
        try
        {
            if (actor.CharacterObject == null)
                return this.Fail(actor, "Character object is unavailable.", out reason);

            if (!this.TryGetModelPosing(actor, out var modelPosing, out reason) || modelPosing == null)
                return this.Fail(actor, reason);

            var transformProperty = modelPosing.GetType().GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public);
            if (transformProperty == null || !transformProperty.CanWrite)
                return this.Fail(actor, "ModelPosing.Transform is not writable.", out reason);

            var transform = transformProperty.GetValue(modelPosing) ?? Activator.CreateInstance(transformProperty.PropertyType);
            if (transform == null)
                return this.Fail(actor, "Unable to create Brio Transform.", out reason);

            var normalizedRotationEuler = NormalizeRotation(rotationEuler);
            var normalizedScale = NormalizeScale(scale);
            var rotation = Normalize(Quaternion.CreateFromYawPitchRoll(normalizedRotationEuler.Y, 0f, 0f));
            SetTransformFieldOrProperty(transform, "Position", position);
            SetTransformFieldOrProperty(transform, "Rotation", rotation);
            SetTransformFieldOrProperty(transform, "Scale", normalizedScale);
            transformProperty.SetValue(modelPosing, transform);

            var nativeReason = this.TryApplyNativeRootTransform(actor, position, normalizedRotationEuler.Y, normalizedScale, out var nativeApplyReason)
                ? nativeApplyReason
                : $"native root skipped: {nativeApplyReason}";

            actor.LastKnownPosition = position;
            actor.LastKnownRotation = rotation;
            actor.LastKnownRotationEuler = normalizedRotationEuler;
            actor.LastKnownScale = normalizedScale;
            actor.TransformEditPosition = position;
            actor.TransformEditRotationEuler = normalizedRotationEuler;
            actor.TransformEditScale = normalizedScale;
            actor.LastTransformReadback = $"position={position}; yaw={normalizedRotationEuler.Y:F4}; uniformScale={normalizedScale.X:F4}";
            actor.LastTransformError = string.Empty;
            actor.LastMoveMethod = "Native root + Brio ModelPosing.Transform";
            this.LastMoveMethod = actor.LastMoveMethod;
            this.LastMoveError = string.Empty;
            reason = $"Applied Brio ModelPosing.Transform; {nativeReason}; {actor.LastTransformReadback}";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to apply Brio model transform. RuntimeId={RuntimeId}", actor.RuntimeId);
            reason = $"Apply Brio transform failed: {ex.Message}";
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
            reason = "Brio assembly not found.";
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
            reason = "EntityManager.SetSelectedEntity(characterObject) overload was not found.";
            return false;
        }

        setSelectedEntity.Invoke(entityManager, [actor.CharacterObject]);

        var posingCapabilityType = brioAssembly.GetTypes()
            .FirstOrDefault(type => type.FullName?.EndsWith(".PosingCapability", StringComparison.OrdinalIgnoreCase) == true);
        if (posingCapabilityType == null)
        {
            reason = "PosingCapability type was not found.";
            return false;
        }

        var tryGetCapability = entityManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "TryGetCapabilityFromSelectedEntity" && method.IsGenericMethodDefinition);
        if (tryGetCapability == null)
        {
            reason = "TryGetCapabilityFromSelectedEntity<T> was not found.";
            return false;
        }

        var args = new object?[] { null, false, true };
        var result = tryGetCapability.MakeGenericMethod(posingCapabilityType).Invoke(entityManager, args);
        if (result is not bool gotCapability || !gotCapability || args[0] == null)
        {
            reason = $"Unable to get PosingCapability from selected entity. result={result ?? "null"}";
            return false;
        }

        var posing = args[0]!;
        modelPosing = posing.GetType().GetProperty("ModelPosing", BindingFlags.Instance | BindingFlags.Public)?.GetValue(posing);
        if (modelPosing == null)
        {
            reason = "PosingCapability.ModelPosing is unavailable.";
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
            reason = $"Unable to resolve Brio services. Brio.Brio={brioType != null}, EntityManager={entityManagerType != null}";
            return null;
        }

        var tryGetService = brioType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "TryGetService" && method.IsGenericMethodDefinition);
        if (tryGetService == null)
        {
            reason = "Brio.Brio.TryGetService<T> was not found.";
            return null;
        }

        var args = new object?[] { null };
        var result = tryGetService.MakeGenericMethod(entityManagerType).Invoke(null, args);
        if (result is bool success && success && args[0] != null)
            return args[0];

        reason = "Brio.Brio.TryGetService<EntityManager> returned false/null.";
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

    private unsafe bool TryApplyNativeRootTransform(RuntimeActorInstance actor, Vector3 position, float yawRadians, Vector3 scale, out string reason)
    {
        if (!TryParseAddress(actor.Address, out var address) || address == 0)
        {
            reason = $"actor address unavailable: {actor.Address}";
            return false;
        }

        if (!float.IsFinite(yawRadians))
        {
            reason = $"invalid yaw radians: {yawRadians}";
            return false;
        }

        try
        {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
            native->GameObject.SetPosition(position.X, position.Y, position.Z);
            native->GameObject.SetRotation(yawRadians);
            native->GameObject.RotationModified();
            native->GameObject.Scale = MathF.Max(0.01f, scale.Y);
            reason = "native root position/yaw/scale updated";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            this.log.Warning(ex, "Failed to apply native root transform. RuntimeId={RuntimeId}, Address={Address}", actor.RuntimeId, actor.Address);
            return false;
        }
    }

    private static bool TryParseAddress(string? rawAddress, out nint address)
    {
        address = 0;
        var raw = rawAddress?.Trim() ?? string.Empty;
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
        => ActorTransformUtil.NormalizeScale(scale);

    private static Vector3 NormalizeRotation(Vector3 rotationEuler)
        => ActorTransformUtil.NormalizeRotation(rotationEuler);

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
