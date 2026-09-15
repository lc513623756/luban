using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Luban.Defs;
using Luban.Types;

namespace Luban.Analytics;

/// <summary>
/// 构造参数生成模型，并校验 C# 类型、成员名称与默认值。
/// </summary>
public sealed partial class AnalyticsCodeTarget
{
    /// <summary>
    /// 构造参数类型；模板仅负责排版，类型转换和构造参数排序由模型提供。
    /// </summary>
    private Dictionary<string, ParameterModel> BuildParameters(
        IEnumerable<DefBean> parameterBeans,
        string ns,
        HashSet<string> typeNames)
    {
        var groups = new Dictionary<string, ParameterModel>();
        foreach (var bean in parameterBeans)
        {
            string name = bean.GetTag(Prefix + "generatedName");
            AddType(name, bean.Tags, typeNames);
            var model = new ParameterModel
            {
                Name = name,
                IsCommon = Kind(bean) == "common",
                Comment = Comment(bean.Comment)
            };
            var properties = new HashSet<string>(StringComparer.Ordinal)
            {
                name,
                "AppendTo",
                "ToParameters",
                "Compose"
            };
            var arguments = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in bean.Fields)
            {
                string property = CodeStyle.FormatProperty(field.Name);
                ValidateIdentifier(property, Origin(field.Tags));
                if (!properties.Add(property))
                {
                    throw Error(field.Tags, "参数属性生成名称冲突: " + property);
                }

                string argument = CodeStyle.FormatField(field.Name);
                ValidateIdentifier(argument, Origin(field.Tags), allowKeyword: true);
                if (!arguments.Add(argument))
                {
                    throw Error(field.Tags, "构造参数生成名称冲突: " + argument);
                }

                bool required = bool.Parse(field.GetTag(Prefix + "required"));
                var enumType = (field.CType as TEnum)?.DefEnum;
                string type = enumType == null ? field.Type : "global::" + ns + "." + CodeStyle.FormatType(enumType.Name);
                string defaultValue = required ? "" : DefaultLiteral(field, enumType, ns);
                model.Fields.Add(new FieldModel
                {
                    Name = property,
                    Argument = EscapeIdentifier(argument),
                    ParameterName = argument,
                    Type = type,
                    Required = required,
                    Injected = bool.Parse(field.GetTag(Prefix + "injected")),
                    DefaultValue = defaultValue,
                    Key = Literal(field.Name),
                    Comment = Comment(field.Comment),
                    ReportExpression = enumType == null ? property : "global::" + ns + ".AnalyticsEnumValues.ToReportValue(" + property + ")"
                });
            }
            model.ConstructorFields = model.Fields.OrderByDescending(f => f.Required).ToList();
            groups.Add(bean.FullName, model);
        }
        return groups;
    }

    /// <summary>
    /// 为基础参数生成稳定的传入类型和采集类型，空字段类型同样保留。
    /// </summary>
    private void AddCommonParameterTypes(
        DefBean commonBean,
        ParameterModel commonModel,
        Dictionary<string, ParameterModel> groups,
        HashSet<string> typeNames)
    {
        string commonName = CodeStyle.FormatType(commonBean.Name);
        commonModel.InputType = commonName + "Input";
        commonModel.CollectedType = commonName + "CollectedParameters";
        AddCommonParameterSubset(commonBean, commonModel, groups, typeNames, commonModel.InputType, injected: true);
        AddCommonParameterSubset(commonBean, commonModel, groups, typeNames, commonModel.CollectedType, injected: false);
    }

    /// <summary>
    /// 为注入字段或游戏采集字段构造独立参数类型，并校验名称冲突。
    /// </summary>
    private void AddCommonParameterSubset(
        DefBean commonBean,
        ParameterModel commonModel,
        Dictionary<string, ParameterModel> groups,
        HashSet<string> typeNames,
        string name,
        bool injected)
    {
        AddType(name, commonBean.Tags, typeNames);
        var subset = new ParameterModel
        {
            Name = name,
            Comment = injected ? "框架传入的基础参数" : "游戏采集的基础参数"
        };
        subset.Fields.AddRange(commonModel.Fields.Where(field => field.Injected == injected));
        if (subset.Fields.Any(field => field.Name == name))
        {
            throw Error(commonBean.Tags, "参数属性生成名称冲突: " + name);
        }

        subset.ConstructorFields = subset.Fields.OrderByDescending(field => field.Required).ToList();
        groups.Add(name, subset);
    }

    /// <summary>
    /// 将配置默认值转换为合法 C# 字面量，可选枚举必须有明确默认成员。
    /// </summary>
    private string DefaultLiteral(DefField field, DefEnum enumType, string ns)
    {
        string value = field.GetTag(Prefix + "default") ?? "";
        if (enumType != null)
        {
            var item = value.Length == 0 ? enumType.Items.SingleOrDefault(i =>
                bool.Parse(i.GetTag(Prefix + "isDefault"))) : enumType.Items.SingleOrDefault(i => i.Name == value.Trim());
            if (item == null)
            {
                throw Error(field.Tags, "可选枚举参数需要有效的默认成员");
            }

            return "global::" + ns + "." + CodeStyle.FormatType(enumType.Name) + "." + CodeStyle.FormatEnumItemName(item.Name);
        }
        if (field.Type == "string")
        {
            return Literal(value);
        }

        value = value.Length == 0 ? (field.Type == "bool" ? "false" : "0") : value.Trim();
        switch (field.Type)
        {
            case "int" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer):
                return integer.ToString(CultureInfo.InvariantCulture);
            case "long" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue):
                return longValue.ToString(CultureInfo.InvariantCulture) + "L";
            case "float" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue) && float.IsFinite(floatValue):
                return floatValue.ToString("R", CultureInfo.InvariantCulture) + "f";
            case "double" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue) && double.IsFinite(doubleValue):
                return doubleValue.ToString("R", CultureInfo.InvariantCulture) + "d";
            case "bool" when value == "0":
                return "false";
            case "bool" when value == "1":
                return "true";
            case "bool" when bool.TryParse(value, out bool boolean):
                return boolean ? "true" : "false";
            default:
                throw Error(field.Tags, $"默认值 '{value}' 不符合类型 '{field.Type}'");
        }
    }

    /// <summary>
    /// 读取 Schema 元素的统计类别标签。
    /// </summary>
    private static string Kind(DefTypeBase value) => value.GetTag(Prefix + "kind");

    /// <summary>
    /// 读取 Schema 元素保存的词典来源位置。
    /// </summary>
    private static string Origin(Dictionary<string, string> tags) => tags.GetValueOrDefault(Prefix + "origin", "");

    /// <summary>
    /// 根据 Schema 标签中的词典位置构造生成错误。
    /// </summary>
    private static Exception Error(Dictionary<string, string> tags, string message) =>
        new InvalidOperationException($"[Analytics] {Origin(tags)}: {message}");

    /// <summary>
    /// 将字符串转换为可直接写入 C# 源码的字面量。
    /// </summary>
    private static string Literal(string value) => JsonSerializer.Serialize(value ?? "");

    /// <summary>
    /// 统一词典说明的换行格式，供模板生成注释。
    /// </summary>
    private static string Comment(string value) => (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// 为允许使用的 C# 关键字标识符添加转义前缀。
    /// </summary>
    private string EscapeIdentifier(string name) => PreservedKeyWords.Contains(name) ? "@" + name : name;

    /// <summary>
    /// 登记生成类型，并校验标识符合法性及全配置名称唯一性。
    /// </summary>
    private void AddType(string name, Dictionary<string, string> tags, HashSet<string> names)
    {
        ValidateIdentifier(name, Origin(tags));
        if (!names.Add(name))
        {
            throw Error(tags, "生成类型名称冲突: " + name);
        }
    }

    /// <summary>
    /// 校验生成名称符合 C# 标识符规则，并按用途限制保留关键字。
    /// </summary>
    private void ValidateIdentifier(string name, string origin, bool allowKeyword = false)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") || (!allowKeyword && PreservedKeyWords.Contains(name)))
        {
            throw new InvalidOperationException($"[Analytics] {origin}: 非法 C# 生成标识符 '{name}'");
        }
    }
}
