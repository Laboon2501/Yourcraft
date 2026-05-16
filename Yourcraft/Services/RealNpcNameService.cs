using Dalamud.Plugin.Services;
using System.Reflection;

namespace Yourcraft.Services;

public sealed class RealNpcNameService
{
    private readonly IPluginLog log;

    public RealNpcNameService(IPluginLog log)
    {
        this.log = log;
    }

    public string LastNameError { get; private set; } = string.Empty;

    public bool TrySetNativeName(object character, string name)
    {
        this.LastNameError = string.Empty;
        var errors = new List<string>();

        try
        {
            if (this.TrySetNameMember(character, name, errors))
                return true;

            var native = character.GetType()
                .GetMethod("Native", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(character, null);

            if (native != null && this.TrySetNameMember(native, name, errors))
                return true;

            var gameObject = native?.GetType()
                .GetProperty("GameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(native);

            if (gameObject != null && this.TrySetNameMember(gameObject, name, errors))
                return true;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            this.log.Warning(ex, "Failed to set real NPC native name");
        }

        this.LastNameError = errors.Count == 0
            ? "原生 NamePlate 名称未能设置，需后续研究 native name 字段。"
            : $"原生 NamePlate 名称未能设置，需后续研究 native name 字段。{string.Join("；", errors)}";
        return false;
    }

    public string TryReadNativeName(object character)
    {
        try
        {
            var direct = this.ReadNameMember(character);
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            var native = character.GetType()
                .GetMethod("Native", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(character, null);

            if (native != null)
            {
                var nativeName = this.ReadNameMember(native);
                if (!string.IsNullOrWhiteSpace(nativeName))
                    return nativeName;

                var gameObject = native.GetType()
                    .GetProperty("GameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(native);

                if (gameObject != null)
                {
                    var gameObjectName = this.ReadNameMember(gameObject);
                    if (!string.IsNullOrWhiteSpace(gameObjectName))
                        return gameObjectName;
                }
            }
        }
        catch (Exception ex)
        {
            this.LastNameError = $"读取原生名称失败：{ex.Message}";
            this.log.Warning(ex, "Failed to read real NPC native name");
        }

        return "不可用";
    }

    private bool TrySetNameMember(object source, string name, List<string> errors)
    {
        var type = source.GetType();
        foreach (var memberName in new[] { "Name", "NamePlate", "ObjectName" })
        {
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property is { CanWrite: true })
            {
                if (property.PropertyType == typeof(string))
                {
                    property.SetValue(source, name);
                    return true;
                }

                errors.Add($"{type.Name}.{memberName} 类型为 {property.PropertyType.Name}，暂不支持写入。");
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && !field.IsInitOnly)
            {
                if (field.FieldType == typeof(string))
                {
                    field.SetValue(source, name);
                    return true;
                }

                errors.Add($"{type.Name}.{memberName} 字段类型为 {field.FieldType.Name}，暂不支持写入。");
            }
        }

        errors.Add($"{type.Name} 上未找到可写 Name/NamePlate/ObjectName。");
        return false;
    }

    private string ReadNameMember(object source)
    {
        var type = source.GetType();
        foreach (var memberName in new[] { "Name", "NamePlate", "ObjectName" })
        {
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var propertyValue = property?.GetValue(source)?.ToString();
            if (!string.IsNullOrWhiteSpace(propertyValue))
                return propertyValue;

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fieldValue = field?.GetValue(source)?.ToString();
            if (!string.IsNullOrWhiteSpace(fieldValue))
                return fieldValue;
        }

        return string.Empty;
    }
}
