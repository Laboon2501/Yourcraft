using Dalamud.Plugin.Services;
using LocalQuestReborn.Models;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace LocalQuestReborn.Services;

public sealed class PropModelService
{
    private readonly BrioAssemblyBridgeService brioAssemblyBridge;
    private readonly IPluginLog log;

    public PropModelService(BrioAssemblyBridgeService brioAssemblyBridge, IPluginLog log)
    {
        this.brioAssemblyBridge = brioAssemblyBridge;
        this.log = log;
    }

    public string LastResult { get; private set; } = "尚未进行 Prop 模型实验。";

    public bool ApplyModelPath(RuntimePropInstance prop, out string result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prop.ModelPath))
            {
                result = "modelPath 为空，未应用。";
                prop.LastModelResult = result;
                this.LastResult = result;
                return false;
            }

            var capabilityInfo = this.FindBrioPropCapabilityHints(prop.CharacterObject);
            if (!this.brioAssemblyBridge.EnableUnsafeNativeWrites)
            {
                result = $"已记录 modelPath={prop.ModelPath}。当前未发现可直接接收 .mdl 路径的安全 Brio API；UnsafeMode=false，未写入 native。{capabilityInfo}";
                prop.LastModelResult = result;
                this.LastResult = result;
                return false;
            }

            result = $"UnsafeMode=true，但本轮仍不盲写 DrawObject/ResourceHandle。请先查看 Dump：当前只记录 modelPath={prop.ModelPath}。{capabilityInfo}";
            prop.LastModelResult = result;
            this.LastResult = result;
            return false;
        }
        catch (Exception ex)
        {
            result = $"应用 modelPath 失败：{ex.Message}";
            prop.LastModelResult = result;
            this.LastResult = result;
            this.log.Warning(ex, "Prop model path experiment failed for {RuntimeId}", prop.RuntimeId);
            return false;
        }
    }

    public void DumpDrawObject(RuntimePropInstance prop)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"runtimeId={prop.RuntimeId}");
        builder.AppendLine($"propId={prop.PropId}");
        builder.AppendLine($"objectIndex={prop.ObjectIndex}");
        builder.AppendLine($"address={prop.Address}");
        builder.AppendLine($"drawObjectAddress={prop.DrawObjectAddress}");
        builder.AppendLine($"objectType={prop.ObjectType}");
        builder.AppendLine($"isCharacterClone={prop.IsCharacterClone}");
        builder.AppendLine($"isBrioProp={prop.IsBrioProp}");
        builder.AppendLine($"spawnMethod={prop.SpawnMethod}");
        builder.AppendLine($"propDataFields={prop.PropDataFields}");
        builder.AppendLine($"position={FormatVector(prop.Position)}");
        builder.AppendLine($"rotation={prop.Rotation:F3}");
        builder.AppendLine($"scale={prop.Scale:F3}");
        builder.AppendLine($"modelPath={prop.ModelPath}");

        if (prop.CharacterObject == null)
        {
            builder.AppendLine("characterObject=null");
        }
        else
        {
            var type = prop.CharacterObject.GetType();
            builder.AppendLine($"characterObjectType={type.FullName}");
            AppendInterestingMembers(builder, type, prop.CharacterObject, "Draw", "Model", "Resource", "Path", "Weapon", "Prop", "Address", "ObjectIndex", "Position");
        }

        prop.DrawObjectDump = builder.ToString();
        this.LastResult = "已刷新 DrawObject dump。";
    }

    public void DumpModelResource(RuntimePropInstance prop)
    {
        var builder = new StringBuilder();
        builder.AppendLine("模型/资源信息实验 dump");
        builder.AppendLine($"modelPath={prop.ModelPath}");
        builder.AppendLine($"drawObjectAddress={prop.DrawObjectAddress}");
        builder.AppendLine("结论：当前通过 Dalamud ICharacter public/reflection 层没有拿到可安全写入任意 .mdl 路径的 API。");
        builder.AppendLine("Brio 的 Prop 路线倾向于使用 ActorAppearanceCapability.SetProp(WeaponModelId)，不是直接传 bg/.../xxx.mdl。");

        if (prop.CharacterObject != null)
            AppendInterestingMembers(builder, prop.CharacterObject.GetType(), prop.CharacterObject, "Native", "Draw", "Model", "Resource", "Handle", "Weapon", "Prop");

        prop.ModelResourceDump = builder.ToString();
        this.LastResult = "已刷新模型/资源 dump。";
    }

    private string FindBrioPropCapabilityHints(object? characterObject)
    {
        var brioAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name?.Equals("Brio", StringComparison.OrdinalIgnoreCase) == true);
        if (brioAssembly == null)
            return " 未发现 Brio assembly。";

        var hints = brioAssembly.GetTypes()
            .Where(type => type.Name.Contains("Prop", StringComparison.OrdinalIgnoreCase) ||
                           type.Name.Contains("AppearanceCapability", StringComparison.OrdinalIgnoreCase) ||
                           type.Name.Contains("Model", StringComparison.OrdinalIgnoreCase))
            .Select(type => type.FullName ?? type.Name)
            .Take(20)
            .ToList();

        return hints.Count == 0
            ? " Brio assembly 中未找到明显 Prop/Model 类型。"
            : $" Brio 相关类型候选：{string.Join(", ", hints)}";
    }

    private static void AppendInterestingMembers(StringBuilder builder, Type type, object instance, params string[] keywords)
    {
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => keywords.Any(keyword => property.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))).Take(40))
        {
            try
            {
                builder.AppendLine($"property {property.Name} ({property.PropertyType.Name}) = {property.GetValue(instance) ?? "null"}");
            }
            catch (Exception ex)
            {
                builder.AppendLine($"property {property.Name} 读取失败：{ex.Message}");
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(method => keywords.Any(keyword => method.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))).Take(40))
            builder.AppendLine($"method {method.Name}({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))}) -> {method.ReturnType.Name}");
    }

    private static string FormatVector(Vector3 vector)
        => $"X {vector.X:F2}, Y {vector.Y:F2}, Z {vector.Z:F2}";
}
