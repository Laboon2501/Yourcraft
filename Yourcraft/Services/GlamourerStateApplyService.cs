using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Reflection;

namespace Yourcraft.Services;

public sealed class GlamourerStateApplyService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private object? applyStateSubscriber;
    private MethodInfo? applyStateInvoke;

    public GlamourerStateApplyService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.Refresh();
    }

    public bool IsAvailable { get; private set; }

    public string Signature { get; private set; } = "未探测";

    public string LastResult { get; private set; } = string.Empty;

    public string LastException { get; private set; } = string.Empty;

    public void Refresh()
    {
        try
        {
            this.applyStateSubscriber = null;
            this.applyStateInvoke = null;
            this.IsAvailable = false;
            this.LastException = string.Empty;

            var applyStateType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(type =>
                    type.FullName?.Equals("Glamourer.Api.IpcSubscribers.ApplyState", StringComparison.OrdinalIgnoreCase) == true ||
                    type.FullName?.EndsWith(".IpcSubscribers.ApplyState", StringComparison.OrdinalIgnoreCase) == true);

            if (applyStateType == null)
            {
                this.Signature = "未发现 Glamourer.Api.IpcSubscribers.ApplyState wrapper";
                this.LastResult = this.Signature;
                return;
            }

            this.applyStateSubscriber = Activator.CreateInstance(applyStateType, this.pluginInterface);
            this.applyStateInvoke = applyStateType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "Invoke");

            if (this.applyStateInvoke == null)
            {
                this.Signature = $"{applyStateType.FullName}.Invoke 未发现";
                this.LastResult = this.Signature;
                return;
            }

            this.Signature = FormatMethod(this.applyStateInvoke);
            this.IsAvailable = true;
            this.LastResult = $"已发现 Glamourer ApplyState wrapper：{this.Signature}";
        }
        catch (Exception ex)
        {
            this.IsAvailable = false;
            this.LastException = ex.Message;
            this.LastResult = $"探测 Glamourer ApplyState 失败：{ex.Message}";
            this.log.Warning(ex, "Failed to probe Glamourer ApplyState wrapper.");
        }
    }

    public bool TryApplyStateBase64(string stateBase64, int objectIndex, object applyFlags, out string reason)
    {
        reason = string.Empty;
        try
        {
            if (!this.IsAvailable || this.applyStateSubscriber == null || this.applyStateInvoke == null)
            {
                reason = this.LastResult;
                return false;
            }

            var parameters = this.applyStateInvoke.GetParameters();
            if (parameters.Length != 4)
            {
                reason = $"ApplyState wrapper 参数数量不是 4：{this.Signature}";
                return false;
            }

            var args = new object?[]
            {
                stateBase64,
                objectIndex,
                Convert.ChangeType(0u, parameters[2].ParameterType),
                applyFlags,
            };

            var result = this.applyStateInvoke.Invoke(this.applyStateSubscriber, args);
            this.LastException = string.Empty;
            this.LastResult = $"Glamourer ApplyState 调用完成：result={result ?? "null"}";
            reason = this.LastResult;
            return true;
        }
        catch (Exception ex)
        {
            this.LastException = ex.InnerException?.Message ?? ex.Message;
            this.LastResult = $"Glamourer ApplyState 调用失败：{this.LastException}";
            this.log.Warning(ex, "Failed to invoke Glamourer ApplyState. ObjectIndex={ObjectIndex}", objectIndex);
            reason = this.LastResult;
            return false;
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
        catch
        {
            return [];
        }
    }

    private static string FormatMethod(MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters}) -> {method.ReturnType.Name}";
    }
}
