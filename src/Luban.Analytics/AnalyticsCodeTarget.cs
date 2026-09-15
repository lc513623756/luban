using Luban.CodeTarget;
using Luban.CodeFormat;
using Luban.CodeFormat.CodeStyles;
using Luban.CSharp.CodeTarget;
using Luban.Defs;
using Luban.Types;
using Scriban.Runtime;

namespace Luban.Analytics;

/// <summary>
/// 将统计定义转换为强类型业务入口和参数类型，项目通过事件接收接口完成接入。
/// </summary>
[CodeTarget("cs-analytics")]
public sealed partial class AnalyticsCodeTarget : CsharpCodeTargetBase
{
    private const string Prefix = AnalyticsSchemaLoader.Prefix;

    /// <summary>
    /// 复用 C# 默认命名规范，仅将统计枚举成员默认转换为 Pascal 名称。
    /// </summary>
    protected override ICodeStyle DefaultCodeStyle => new OverlayCodeStyle(
        base.DefaultCodeStyle, null, null, null, null, null, "pascal");

    /// <summary>
    /// 在写入任何文件前校验完整统计生成模型。
    /// </summary>
    public override void ValidateDefinition(GenerationContext ctx)
    {
        // 输出文件前完成整个模型校验，避免错误配置产生部分生成文件。
        BuildModel(ctx);
    }

    /// <summary>
    /// 根据已校验的模型输出事件入口、参数类型及枚举文件。
    /// </summary>
    public override void Handle(GenerationContext ctx, OutputFileManifest manifest)
    {
        var model = BuildModel(ctx);
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
        Emit("IAnalyticsEventSink", "event_sink", model, manifest, paths);
        Emit(model.Manager, "manager", model, manifest, paths);
    }

    /// <summary>
    /// 使用指定 Scriban 模板输出单个类型，并登记生成文件。
    /// </summary>
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

    /// <summary>
    /// 构造统计生成模型，统一校验类型、方法和上报字段名称冲突。
    /// </summary>
    private GenerationModel BuildModel(GenerationContext ctx)
    {
        var parameterBeans = ctx.ExportBeans.Where(b => Kind(b) is "parameters" or "common").ToList();
        var commonBeans = parameterBeans.Where(b => Kind(b) == "common").ToList();
        if (commonBeans.Count != 1)
        {
            throw new InvalidOperationException("[Analytics] 必须且只能定义一套基础参数");
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
            "IAnalyticsEventSink",
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
            "BindEventSink",
            "BindCommonParametersCollector",
            "Submit"
        };

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
                IsFunnel = bool.Parse(bean.GetTag(Prefix + "isFunnel")),
                StepField = Literal(bean.GetTag(Prefix + "stepField")),
                Comment = Comment(bean.Comment)
            };
            model.ReceiverIds = string.Join(", ", bean.GetTag(Prefix + "receiverIds").Split(',',
                StringSplitOptions.RemoveEmptyEntries).Select(Literal));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in commonBean.Fields)
            {
                if (!keys.Add(field.Name))
                {
                    throw Error(field.Tags, "公共字段冲突: " + field.Name);
                }
            }

            var parameterFields = bean.Fields.Where(f => !f.HasTag(Prefix + "common")).ToList();
            if (parameterFields.Count > 1)
            {
                throw Error(bean.Tags, "每个事件只能生成一个事件参数类型");
            }

            if (parameterFields.Count == 1)
            {
                var parameterBean = ((TBean)parameterFields[0].CType).DefBean;
                var parameters = groups[parameterBean.FullName];
                foreach (var parameter in parameterBean.Fields)
                {
                    if (!keys.Add(parameter.Name))
                    {
                        throw Error(parameter.Tags, $"事件 '{eventName}' 上报字段重复: '{parameter.Name}'");
                    }
                }
                model.ParametersType = parameters.Name;
                model.ParametersArgument = "parameters";
                if (model.IsFunnel)
                {
                    model.StepProperty = CodeStyle.FormatProperty(bean.GetTag(Prefix + "stepField"));
                }
            }
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
            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            var model = new EnumModel { Name = name, IsFunnelStep = value.HasTag(Prefix + "isFunnelStep") };
            foreach (var item in value.Items)
            {
                string member = CodeStyle.FormatEnumItemName(item.Name);
                ValidateIdentifier(member, Origin(item.Tags));
                if (member == name)
                {
                    throw Error(item.Tags, "枚举成员不能与枚举类型同名: " + member);
                }
                if (members.TryGetValue(member, out string previous))
                {
                    throw Error(item.Tags, $"枚举成员生成名称冲突: '{member}'；首次定义: {previous}");
                }
                members.Add(member, Origin(item.Tags));

                model.Items.Add(new EnumItemModel
                {
                    Name = member,
                    Value = item.IntValue,
                    ReportValue = Literal(item.Name),
                    OnceKey = model.IsFunnelStep ? Literal(item.GetTag(Prefix + "onceKey")) : "",
                    Comment = Comment(item.Comment)
                });
            }
            enumModels.Add(model);
        }
        return enumModels;
    }

}
