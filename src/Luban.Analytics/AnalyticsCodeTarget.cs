using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Luban.CodeTarget;
using Luban.CSharp.CodeTarget;
using Luban.Defs;
using Luban.Types;
using Scriban.Runtime;

namespace Luban.Analytics;

/// <summary>
/// 将统计定义转换为强类型业务入口和参数类型，项目接入由发送、绑定模板完成。
/// </summary>
[CodeTarget("cs-analytics")]
public sealed class AnalyticsCodeTarget : CsharpCodeTargetBase
{
    private const string Prefix = AnalyticsSchemaLoader.Prefix;
    public override void ValidateDefinition(GenerationContext ctx)
    {
        // 输出文件前完成整个模型校验，避免错误配置产生部分生成文件。
        BuildModel(ctx);
    }

    public override void Handle(GenerationContext ctx, OutputFileManifest manifest)
    {
        var model = BuildModel(ctx);
        model.SendBody = RenderBody("send", model);
        model.BindCommonBody = RenderBody("bind_common", model);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in model.Parameters)
        {
            Emit(group.Name, "bean", model, manifest, paths, "group", group);
        }

        foreach (var value in model.Enums)
        {
            Emit(value.Name, "enum", model, manifest, paths, "enum", value);
        }

        Emit("AnalyticsEventDefinition", "event_definition", model, manifest, paths);
        Emit("IAnalyticsCommonParametersCollector", "common_collector", model, manifest, paths);
        Emit(model.Manager, "manager", model, manifest, paths);
    }

    private string RenderBody(string name, GenerationModel model)
    {
        var template = GetTemplate(name);
        var context = CreateTemplateContext(template);
        var globals = new ScriptObject();
        globals.Add("model", model);
        context.PushGlobal(globals);
        return template.Render(context).TrimEnd();
    }

    private void Emit(string name, string templateName, GenerationModel model,
        OutputFileManifest manifest, HashSet<string> paths, string variable = null, object value = null)
    {
        if (!paths.Add(name + ".cs"))
        {
            throw new InvalidOperationException("[Analytics] 生成文件名称冲突: " + name + ".cs");
        }

        var template = GetTemplate(templateName);
        var context = CreateTemplateContext(template);
        var globals = new ScriptObject();
        globals.Add("model", model);
        if (variable != null)
        {
            globals.Add(variable, value);
        }

        context.PushGlobal(globals);
        manifest.AddFile(CreateOutputFile(name + ".cs", FileHeader.Trim() + "\n\n" + template.Render(context).Trim() + "\n"));
    }

    private GenerationModel BuildModel(GenerationContext ctx)
    {
        var parameterBeans = ctx.ExportBeans.Where(b => Kind(b) is "parameters" or "common").ToList();
        var commonBeans = parameterBeans.Where(b => Kind(b) == "common").ToList();
        if (commonBeans.Count != 1)
        {
            throw new InvalidOperationException("[Analytics] 必须且只能定义一个基础参数组");
        }

        string manager = CodeStyle.FormatType(ctx.Target.Manager);
        ValidateIdentifier(manager, "target.manager");
        string ns = CodeStyle.FormatNamespace(ctx.Target.TopModule);
        if (string.IsNullOrWhiteSpace(ns))
        {
            throw new InvalidOperationException("[Analytics] target.topModule 不能为空");
        }

        foreach (string part in ns.Split('.'))
        {
            ValidateIdentifier(part, "target.topModule");
        }

        var typeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "AnalyticsEventDefinition",
            "AnalyticsEnumValues",
            "IAnalyticsCommonParametersCollector",
            "Func",
            "Guid",
            "Array",
            "Dictionary",
            "ReadOnlyDictionary",
            "StringComparer",
            "DateTimeOffset",
            "ArgumentNullException",
            "ArgumentOutOfRangeException",
            "InvalidOperationException",
            "IReadOnlyList",
            "IReadOnlyDictionary",
            "System"
        };
        AddType(manager, new()
        {
            [Prefix + "origin"] = "target.manager"
        }, typeNames);
        var enumModels = BuildEnums(ctx, typeNames);

        var groups = BuildParameters(parameterBeans, ns, typeNames);

        var commonBean = commonBeans.Single();
        var commonModel = groups[commonBean.FullName];
        AddCommonParameterTypes(commonBean, commonModel, groups, typeNames);

        var events = new List<EventModel>();
        var entryMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            manager,
            "BindCommonParametersCollector",
            "Submit"
        };
        ValidateOnceKeys(ctx.Assembly.TypeList.OfType<DefBean>());

        foreach (var bean in ctx.ExportBeans.Where(b => Kind(b) == "event"))
        {
            string eventName = bean.GetTag(Prefix + "eventName");
            string method = CodeStyle.FormatMethod(eventName);
            ValidateIdentifier(method, Origin(bean.Tags));
            string definition = method + "Definition";
            if (!entryMembers.Add(method) || !entryMembers.Add(definition))
            {
                throw Error(bean.Tags, "事件方法或定义生成名称冲突: " + method);
            }

            var model = new EventModel
            {
                Name = method,
                Definition = definition,
                EventName = Literal(eventName),
                OnceKey = Literal(bean.GetTag(Prefix + "onceKey")),
                Comment = Comment(bean.Comment)
            };
            model.PlatformTags = string.Join(", ", bean.GetTag(Prefix + "platformTags").Split(',',
                StringSplitOptions.RemoveEmptyEntries).Select(Literal));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in commonBean.Fields)
            {
                if (!keys.Add(field.Name))
                {
                    throw Error(field.Tags, "公共字段冲突: " + field.Name);
                }
            }

            foreach (var field in bean.Fields.Where(f => !f.HasTag(Prefix + "common")))
            {
                var groupBean = ((TBean)field.CType).DefBean;
                var group = groups[groupBean.FullName];
                foreach (var parameter in groupBean.Fields)
                {
                    if (!keys.Add(parameter.Name))
                    {
                        throw Error(parameter.Tags, $"事件 '{eventName}' 上报字段重复: '{parameter.Name}'");
                    }
                }

                model.Groups.Add(new EventGroupModel
                {
                    Type = group.Name,
                    Argument = EscapeIdentifier(CodeStyle.FormatField(groupBean.Name))
                });
            }
            // 参数组名称不能遮蔽模板中的局部变量。
            if (model.Groups.Any(g => g.Argument == "values"))
            {
                throw Error(bean.Tags, "参数组生成名称与内部变量冲突");
            }

            if (model.Groups.Select(g => g.Argument).Distinct().Count() != model.Groups.Count)
            {
                throw Error(bean.Tags, "参数组生成名称冲突");
            }

            model.Signature = string.Join(", ", model.Groups.Select(g => g.Type + " " + g.Argument));
            events.Add(model);
        }
        if (events.Count == 0)
        {
            throw new InvalidOperationException("[Analytics] 当前 target 没有统计事件");
        }

        return new GenerationModel
        {
            Namespace = ns,
            Manager = manager,
            CommonType = commonModel.Name,
            CommonInputType = commonModel.InputType,
            CommonCollectedType = commonModel.CollectedType,
            Parameters = groups.Values.ToList(),
            Enums = enumModels,
            Events = events
        };
    }

    /// <summary>
    /// 构造枚举及其上报值映射，统一检查生成名称冲突。
    /// </summary>
    private List<EnumModel> BuildEnums(GenerationContext ctx, HashSet<string> typeNames)
    {
        var enumModels = new List<EnumModel>();
        foreach (var value in ctx.ExportEnums.Where(e => Kind(e) == "enum"))
        {
            string name = CodeStyle.FormatType(value.Name);
            AddType(name, value.Tags, typeNames);
            var members = new HashSet<string>(StringComparer.Ordinal);
            var model = new EnumModel { Name = name };
            foreach (var item in value.Items)
            {
                string member = CodeStyle.FormatEnumItemName(item.Name);
                ValidateIdentifier(member, Origin(item.Tags));
                if (member == name || !members.Add(member))
                {
                    throw Error(item.Tags, "枚举成员生成名称冲突: " + member);
                }

                model.Items.Add(new EnumItemModel
                {
                    Name = member,
                    Value = item.IntValue,
                    ReportValue = Literal(item.GetTag(Prefix + "reportValue")),
                    Comment = Comment(item.Comment)
                });
            }
            enumModels.Add(model);
        }
        return enumModels;
    }

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
            string name = CodeStyle.FormatType(bean.Name) + "Parameters";
            AddType(name, bean.Tags, typeNames);
            var model = new ParameterModel
            {
                Name = name,
                IsCommon = Kind(bean) == "common",
                Comment = Comment("统计参数组：" + bean.Name)
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
    /// 一次性身份在整套配置中唯一，不受当前 target 的导出范围限制。
    /// </summary>
    private static void ValidateOnceKeys(IEnumerable<DefBean> beans)
    {
        var onceKeys = new Dictionary<string, DefBean>(StringComparer.Ordinal);
        foreach (var value in beans.Where(b => Kind(b) == "event"))
        {
            string key = value.GetTag(Prefix + "onceKey");
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (onceKeys.TryGetValue(key, out var previous))
            {
                throw Error(value.Tags, "once_key 重复: '" + key + "'；首次定义: " + Origin(previous.Tags));
            }

            onceKeys.Add(key, value);
        }
    }

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

    private static string Kind(DefTypeBase value) => value.GetTag(Prefix + "kind");

    private static string Origin(Dictionary<string, string> tags) => tags.GetValueOrDefault(Prefix + "origin", "");

    private static Exception Error(Dictionary<string, string> tags, string message) =>
        new InvalidOperationException($"[Analytics] {Origin(tags)}: {message}");

    private static string Literal(string value) => JsonSerializer.Serialize(value ?? "");

    private static string Comment(string value) => (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

    private string EscapeIdentifier(string name) => PreservedKeyWords.Contains(name) ? "@" + name : name;

    private void AddType(string name, Dictionary<string, string> tags, HashSet<string> names)
    {
        ValidateIdentifier(name, Origin(tags));
        if (!names.Add(name))
        {
            throw Error(tags, "生成类型名称冲突: " + name);
        }
    }

    private void ValidateIdentifier(string name, string origin, bool allowKeyword = false)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") || (!allowKeyword && PreservedKeyWords.Contains(name)))
        {
            throw new InvalidOperationException($"[Analytics] {origin}: 非法 C# 生成标识符 '{name}'");
        }
    }
}
