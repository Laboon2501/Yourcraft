using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using Lumina.Excel.Sheets;
using System.Reflection;
using System.Threading.Tasks;

namespace LocalQuestReborn.Services;

public sealed class BrioHumanoidAppearanceApplyService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, object?> enpcBaseCache = new();

    public BrioHumanoidAppearanceApplyService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.Refresh();
    }

    public bool IsAvailable { get; private set; }

    public string LastResult { get; private set; } = string.Empty;

    public string LastException { get; private set; } = string.Empty;

    public string LastSignature { get; private set; } = string.Empty;

    public string? PendingRuntimeId { get; private set; }

    public bool LastApplyPending { get; private set; }

    public bool LastApplySucceeded { get; private set; }

    public DateTime? LastApplyCompletedAt { get; private set; }

    public void Refresh()
    {
        try
        {
            var brioAssembly = FindBrioAssembly();
            if (brioAssembly == null)
            {
                this.IsAvailable = false;
                this.LastResult = "未发现 Brio assembly";
                return;
            }

            var appearanceType = brioAssembly.GetType("Brio.Game.Actor.Appearance.ActorAppearance");
            var capabilityType = FindType(brioAssembly, ".ActorAppearanceCapability");
            var optionsType = FindType(brioAssembly, ".AppearanceImportOptions");
            this.IsAvailable = appearanceType != null && capabilityType != null && optionsType != null;
            this.LastResult = this.IsAvailable
                ? "已发现 Brio ActorAppearanceCapability / ActorAppearance / AppearanceImportOptions"
                : $"Brio 外观类型不完整：ActorAppearance={appearanceType != null}, Capability={capabilityType != null}, Options={optionsType != null}";
        }
        catch (Exception ex)
        {
            this.IsAvailable = false;
            this.LastException = ex.Message;
            this.LastResult = $"探测 Brio Humanoid 外观路径失败：{ex.Message}";
            this.log.Warning(ex, "Failed to probe Brio humanoid appearance path.");
        }
    }

    public bool TryApplyHumanoid(RuntimeActorInstance actor, GameNpcAppearanceResolution resolution, out string reason)
    {
        reason = string.Empty;
        try
        {
            if (actor.CharacterObject == null)
            {
                reason = "actor.characterObject 不可用";
                return this.Fail(reason);
            }

            if (resolution.Appearance.Kind != GameNpcResolvedAppearanceKind.Humanoid)
            {
                reason = $"不是 Humanoid 外观：{resolution.Appearance.Kind}";
                return this.Fail(reason);
            }

            var brioAssembly = FindBrioAssembly();
            if (brioAssembly == null)
            {
                reason = "未发现 Brio assembly";
                return this.Fail(reason);
            }

            var enpcBase = this.FindENpcBase(resolution.BaseRowId);
            if (enpcBase == null)
            {
                reason = $"未找到 ENpcBase RowId={resolution.BaseRowId}，无法调用 ActorAppearance.FromENpc";
                return this.Fail(reason);
            }

            var entityManager = ResolveEntityManager(brioAssembly, out reason);
            if (entityManager == null)
                return this.Fail(reason);

            if (!SetSelectedEntity(entityManager, actor.CharacterObject, out reason))
                return this.Fail(reason);

            var capabilityType = FindType(brioAssembly, ".ActorAppearanceCapability");
            if (capabilityType == null)
            {
                reason = "未找到 Brio ActorAppearanceCapability 类型";
                return this.Fail(reason);
            }

            if (!TryGetSelectedCapability(entityManager, capabilityType, out var capability, out reason) || capability == null)
                return this.Fail(reason);

            var appearanceType = brioAssembly.GetType("Brio.Game.Actor.Appearance.ActorAppearance");
            if (appearanceType == null)
            {
                reason = "未找到 Brio.Game.Actor.Appearance.ActorAppearance 类型";
                return this.Fail(reason);
            }

            var fromENpc = appearanceType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(method =>
                    method.Name == "FromENpc" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType.IsInstanceOfType(enpcBase));

            if (fromENpc == null)
            {
                reason = "未找到 ActorAppearance.FromENpc(ENpcBase) 方法";
                return this.Fail(reason);
            }

            var appearance = fromENpc.Invoke(null, [enpcBase]);
            if (appearance == null)
            {
                reason = "ActorAppearance.FromENpc 返回 null";
                return this.Fail(reason);
            }

            var optionsType = FindType(brioAssembly, ".AppearanceImportOptions");
            if (optionsType == null)
            {
                reason = "未找到 AppearanceImportOptions 类型";
                return this.Fail(reason);
            }

            var allOptions = Enum.Parse(optionsType, "All");
            var setAppearance = capability.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "SetAppearance")
                        return false;

                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                        parameters[0].ParameterType.IsInstanceOfType(appearance) &&
                        parameters[1].ParameterType == optionsType;
                });

            if (setAppearance == null)
            {
                reason = "未找到 ActorAppearanceCapability.SetAppearance(ActorAppearance, AppearanceImportOptions)";
                return this.Fail(reason);
            }

            this.LastSignature = FormatMethod(setAppearance);
            var result = setAppearance.Invoke(capability, [appearance, allOptions]);
            if (result is Task task)
            {
                this.PendingRuntimeId = actor.RuntimeId;
                this.LastApplyPending = true;
                this.LastApplySucceeded = false;
                this.LastApplyCompletedAt = null;
                _ = task.ContinueWith(completedTask =>
                {
                    this.LastApplyPending = false;
                    this.LastApplyCompletedAt = DateTime.Now;
                    if (completedTask.Exception != null)
                    {
                        this.LastException = completedTask.Exception.GetBaseException().Message;
                        this.LastApplySucceeded = false;
                        this.LastResult = $"Brio ActorAppearanceCapability.SetAppearance 异步失败：{this.LastException}";
                    }
                    else
                    {
                        this.LastException = string.Empty;
                        this.LastResult = $"Brio ActorAppearanceCapability.SetAppearance async completed: ENpcBase={resolution.BaseRowId}, runtime={actor.RuntimeId}";
                        this.LastApplySucceeded = true;
                    }
                }, TaskScheduler.Default);
            }
            else
            {
                this.PendingRuntimeId = actor.RuntimeId;
                this.LastApplyPending = false;
                this.LastApplySucceeded = true;
                this.LastApplyCompletedAt = DateTime.Now;
            }

            reason = $"Brio ActorAppearanceCapability.SetAppearance 已触发：ENpcBase={resolution.BaseRowId}, options=All";
            this.LastException = string.Empty;
            this.LastResult = reason;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Brio Humanoid 外观应用失败：{ex.InnerException?.Message ?? ex.Message}";
            this.log.Warning(ex, "Failed to apply humanoid appearance through Brio capability. RuntimeId={RuntimeId}, BaseRowId={BaseRowId}", actor.RuntimeId, resolution.BaseRowId);
            return this.Fail(reason);
        }
    }

    private object? FindENpcBase(uint rowId)
    {
        if (this.enpcBaseCache.TryGetValue(rowId, out var cached))
            return cached;

        try
        {
            var sheet = this.dataManager.GetExcelSheet<ENpcBase>();
            foreach (var row in sheet)
            {
                if (row.RowId == rowId)
                {
                    this.enpcBaseCache[rowId] = row;
                    return row;
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Failed to read ENpcBase for Brio humanoid appearance.");
        }

        this.enpcBaseCache[rowId] = null;
        return null;
    }

    private bool Fail(string reason)
    {
        this.LastResult = reason;
        this.LastException = reason;
        return false;
    }

    private static Assembly? FindBrioAssembly()
        => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name?.Equals("Brio", StringComparison.OrdinalIgnoreCase) == true);

    private static Type? FindType(Assembly assembly, string suffix)
        => assembly.GetTypes().FirstOrDefault(type => type.FullName?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);

    private static object? ResolveEntityManager(Assembly brioAssembly, out string reason)
    {
        reason = string.Empty;
        var accessUtils = brioAssembly.GetType("Brio.BrioAccessUtils");
        var entityManager = accessUtils?.GetProperty("EntityManager", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        if (entityManager != null)
            return entityManager;

        var brioType = brioAssembly.GetType("Brio.Brio");
        var entityManagerType = FindType(brioAssembly, ".EntityManager");
        var tryGetService = brioType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "TryGetService" && method.IsGenericMethodDefinition);
        if (tryGetService == null || entityManagerType == null)
        {
            reason = "未找到 Brio.Brio.TryGetService<T> 或 EntityManager 类型";
            return null;
        }

        var args = new object?[] { null };
        var result = tryGetService.MakeGenericMethod(entityManagerType).Invoke(null, args);
        if (result is bool success && success && args[0] != null)
            return args[0];

        reason = $"TryGetService<EntityManager> 失败：result={result ?? "null"}";
        return null;
    }

    private static bool SetSelectedEntity(object entityManager, object characterObject, out string reason)
    {
        reason = string.Empty;
        var method = entityManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
            {
                if (candidate.Name != "SetSelectedEntity")
                    return false;

                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(characterObject);
            });

        if (method == null)
        {
            reason = "未找到 EntityManager.SetSelectedEntity(characterObject) 可用重载";
            return false;
        }

        method.Invoke(entityManager, [characterObject]);
        return true;
    }

    private static bool TryGetSelectedCapability(object entityManager, Type capabilityType, out object? capability, out string reason)
    {
        capability = null;
        reason = string.Empty;
        var method = entityManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => candidate.Name == "TryGetCapabilityFromSelectedEntity" && candidate.IsGenericMethodDefinition);
        if (method == null)
        {
            reason = "未找到 TryGetCapabilityFromSelectedEntity<T>";
            return false;
        }

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        if (args.Length > 0)
            args[0] = null;

        for (var i = 1; i < args.Length; i++)
            args[i] = parameters[i].ParameterType == typeof(bool) && parameters[i].Name?.Contains("consider", StringComparison.OrdinalIgnoreCase) == true;

        var result = method.MakeGenericMethod(capabilityType).Invoke(entityManager, args);
        capability = args.Length > 0 ? args[0] : null;
        if (result is bool success && success && capability != null)
            return true;

        reason = $"获取 ActorAppearanceCapability 失败：result={result ?? "null"}, capabilityIsNull={capability == null}";
        return false;
    }

    private static string FormatMethod(MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters}) -> {method.ReturnType.Name}";
    }
}
