using System.Globalization;
using System.Text.RegularExpressions;
using Luban.RawDefs;
using Luban.Schema;

namespace Luban.Analytics;

/// <summary>
/// 解析纵向统计词典，并构造供代码目标使用的 Luban 定义。
/// </summary>
[SchemaLoader("analytics", "xlsx", "xls", "xlsm")]
public sealed partial class AnalyticsSchemaLoader : SchemaLoaderBase
{
    internal const string Prefix = "analytics.";

    private static readonly HashSet<string> PrimitiveTypes = new()
    {
        "int", "long", "float", "double", "bool", "string"
    };

    private static readonly string[] EventColumns =
    {
        "event_name", "event_description", "receiver_ids", "kind", "step_field",
        "parameter", "type", "required", "default", "parameter_description",
        "enum_member", "is_default", "enum_description"
    };

    private static readonly string[] CommonColumns =
    {
        "parameter", "type", "required", "default", "description", "injected",
        "enum_member", "is_default", "enum_description"
    };

    private static readonly string[] EventMetadataColumns =
    {
        "event_name", "event_description", "receiver_ids", "kind", "step_field"
    };

    private static readonly string[] EventParameterColumns =
    {
        "parameter", "type", "required", "default", "parameter_description"
    };

    private static readonly string[] CommonParameterColumns =
    {
        "parameter", "type", "required", "default", "description", "injected"
    };

    private static readonly string[] EnumColumns =
    {
        "enum_member", "is_default", "enum_description"
    };

    /// <summary>
    /// 加载纵向统计词典，构建事件、参数和枚举 Schema，并执行结构校验。
    /// </summary>
    public override void Load(string fileName)
    {
        RejectLegacyOptions();
        string eventsSheet = Option("eventsSheet");
        string commonParametersSheet = Option("commonParametersSheet");
        string commonTypePrefix = Option("commonTypePrefix").Trim();
        if (eventsSheet == commonParametersSheet)
        {
            throw new InvalidOperationException("[Analytics] 事件表和基础参数表映射必须不同");
        }
        if (!Regex.IsMatch(commonTypePrefix, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new InvalidOperationException(
                $"[Analytics] analytics.commonTypePrefix 不是合法 C# 类型前缀: '{commonTypePrefix}'");
        }

        var imports = GenerationContext.GlobalConf.Imports
            .Where(import => import.Type == "analytics")
            .Select(import => Path.GetFullPath(Path.Combine(
                GenerationContext.GlobalConf.ConfigDir, import.FileName)))
            .ToList();
        if (imports.Count == 0)
        {
            imports.Add(Path.GetFullPath(fileName));
        }
        if (Path.GetFullPath(fileName) != imports[0])
        {
            return;
        }

        var eventRows = imports
            .SelectMany(file => ReadRows(file, eventsSheet, EventColumns, true))
            .ToList();
        var commonRows = imports
            .SelectMany(file => ReadRows(file, commonParametersSheet, CommonColumns, false))
            .ToList();
        if (eventRows.Count == 0)
        {
            throw new InvalidOperationException($"[Analytics] {fileName}@{eventsSheet}: 没有事件定义");
        }

        var rawEnums = new List<RawEnum>();
        var rawBeans = new List<RawBean>();
        var commonBlocks = CreateParameterBlocks(commonRows, null, CommonParameterColumns);
        if (commonBlocks.Count == 0)
        {
            throw new InvalidOperationException(
                $"[Analytics] {fileName}@{commonParametersSheet}: 没有基础参数定义");
        }

        var commonBean = CreateParameterBean(
            commonTypePrefix, commonTypePrefix + "Parameters", "基础参数", commonBlocks,
            true, "description", commonTypePrefix, rawEnums);
        rawBeans.Add(commonBean);

        var eventBlocks = eventRows
            .GroupBy(row => (row.File, row.EventOwnerLine))
            .Select(group => group.OrderBy(row => row.Line).ToList())
            .ToList();
        Unique(eventBlocks.Select(block => block[0]).ToList(), "event_name");
        var onceKeys = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var block in eventBlocks)
        {
            BuildEvent(block, commonBean, rawEnums, rawBeans, onceKeys);
        }

        foreach (var value in rawEnums)
        {
            Collector.Add(value);
        }
        foreach (var value in rawBeans)
        {
            Collector.Add(value);
        }
    }

    /// <summary>
    /// 将纵向事件区块转换为 Schema，关联独立参数类型和基础参数。
    /// </summary>
    private static void BuildEvent(
        List<Row> rows,
        RawBean commonBean,
        List<RawEnum> rawEnums,
        List<RawBean> rawBeans,
        Dictionary<string, Row> onceKeys)
    {
        Row eventRow = rows[0];
        string eventName = eventRow.Identifier("event_name");
        string eventKind = eventRow.Get("kind");
        if (eventKind.Length == 0)
        {
            eventKind = "event";
        }
        if (eventKind is not ("event" or "funnel"))
        {
            throw eventRow.Error("kind 仅支持 event 或 funnel");
        }

        var parameterRows = rows.Where(HasParameterData).ToList();
        if (parameterRows.Count != rows.Count && (rows.Count != 1 || parameterRows.Count != 0))
        {
            throw rows.First(row => !HasParameterData(row))
                .Error("事件区块中存在缺少 parameter 的数据行");
        }

        var parameterBlocks = CreateParameterBlocks(parameterRows, rows, EventParameterColumns);
        if (parameterBlocks.Count > 0)
        {
            Unique(parameterBlocks.Select(block => block[0]).ToList(), "parameter");
        }

        string stepField = ValidateStepField(eventRow, eventKind, parameterBlocks);
        if (eventKind == "funnel")
        {
            var steps = parameterBlocks.Single(block => block[0].Get("parameter") == stepField);
            ValidateOnceKeys(eventName, steps, onceKeys);
        }

        RawBean parameterBean = null;
        if (parameterBlocks.Count > 0)
        {
            parameterBean = CreateParameterBean(
                "__analytics_parameters_" + eventName,
                ToPascalIdentifier(eventName) + "Parameters",
                "事件参数：" + eventName,
                parameterBlocks,
                false,
                "parameter_description",
                eventName,
                rawEnums);
            rawBeans.Add(parameterBean);
        }

        if (eventKind == "funnel")
        {
            var steps = parameterBlocks.Single(block => block[0].Get("parameter") == stepField);
            var stepEnum = rawEnums.Single(value => value.Name == eventName + "_" + stepField);
            stepEnum.Tags[Prefix + "isFunnelStep"] = "True";
            for (int index = 0; index < steps.Count; index++)
            {
                stepEnum.Items[index].Tags[Prefix + "onceKey"] =
                    eventName + ":" + steps[index].NonEmpty("enum_member");
            }
        }

        var eventBean = Bean(
            "__analytics_event_" + eventName,
            eventRow,
            "event",
            eventRow.Get("event_description"));
        eventBean.Tags[Prefix + "eventName"] = eventName;
        eventBean.Tags[Prefix + "receiverIds"] = string.Join(",", eventRow.List("receiver_ids"));
        eventBean.Tags[Prefix + "isFunnel"] = (eventKind == "funnel").ToString();
        eventBean.Tags[Prefix + "stepField"] = stepField;
        eventBean.Fields.Add(Field("__common", commonBean.Name, eventRow, new()
        {
            [Prefix + "common"] = "True"
        }, eventRow.Get("event_description")));
        if (parameterBean != null)
        {
            eventBean.Fields.Add(Field("parameters", parameterBean.Name, eventRow, new(), "事件参数"));
        }
        rawBeans.Add(eventBean);
    }

    /// <summary>
    /// 枚举每个步骤的最终持久化身份，在整套词典范围内检查并定位重复键。
    /// </summary>
    private static void ValidateOnceKeys(string eventName, List<Row> steps, Dictionary<string, Row> onceKeys)
    {
        foreach (Row step in steps)
        {
            string onceKey = eventName + ":" + step.NonEmpty("enum_member");
            if (onceKeys.TryGetValue(onceKey, out Row previous))
            {
                throw step.Error(
                    $"一次性键重复: '{onceKey}'；首次定义: {previous.File}@{previous.Sheet}:{previous.Line}");
            }
            onceKeys.Add(onceKey, step);
        }
    }

    /// <summary>
    /// 根据纵向参数区块创建 Schema 参数类型，包含默认值及注入声明。
    /// </summary>
    private static RawBean CreateParameterBean(
        string rawName,
        string generatedName,
        string comment,
        List<List<Row>> blocks,
        bool isCommon,
        string descriptionColumn,
        string enumPrefix,
        List<RawEnum> rawEnums)
    {
        Row first = blocks[0][0];
        var bean = Bean(rawName, first, isCommon ? "common" : "parameters", comment);
        bean.Tags[Prefix + "generatedName"] = generatedName;

        foreach (var block in blocks)
        {
            Row row = block[0];
            string parameter = row.Identifier("parameter");
            string configuredType = row.NonEmpty("type");
            bool required = row.Bool("required");
            bool injected = isCommon && row.Bool("injected");
            string defaultValue = row.Get("default", false);

            if (injected && !required)
            {
                throw row.Error("框架传入参数必须必填");
            }
            if (required && defaultValue.Length != 0)
            {
                throw row.Error("必填参数不能配置默认值");
            }

            string fieldType;
            if (configuredType == "enum")
            {
                string enumName = enumPrefix + "_" + parameter;
                rawEnums.Add(CreateEnum(enumName, row.Get(descriptionColumn), block));
                fieldType = enumName;
            }
            else
            {
                if (!PrimitiveTypes.Contains(configuredType))
                {
                    throw row.Error($"未知参数类型 '{configuredType}'；支持基础类型或 enum");
                }
                EnsureNoEnumValues(block);
                if (block.Count != 1)
                {
                    throw row.Error("基础类型参数只能占一行，不能使用参数合并区域");
                }
                fieldType = configuredType;
            }

            bean.Fields.Add(Field(parameter, fieldType, row, new()
            {
                [Prefix + "required"] = required.ToString(),
                [Prefix + "injected"] = injected.ToString(),
                [Prefix + "default"] = defaultValue
            }, row.Get(descriptionColumn)));
        }
        return bean;
    }

    /// <summary>
    /// 创建内联枚举，成员原值同时作为上报值，校验原值唯一性和默认成员。
    /// </summary>
    private static RawEnum CreateEnum(string name, string comment, List<Row> rows)
    {
        if (rows.Any(row => row.Get("enum_member").Length == 0))
        {
            throw rows[0].Error("enum 参数必须至少配置一个完整的枚举成员");
        }
        Unique(rows, "enum_member");
        if (rows.Count(row => row.Bool("is_default")) > 1)
        {
            throw rows.First(row => row.Bool("is_default")).Error("枚举有多个默认成员");
        }

        return new RawEnum
        {
            Name = name,
            Namespace = "",
            Source = rows[0].Source,
            Comment = comment,
            Tags = rows[0].Tags("enum"),
            Groups = new(),
            IsUniqueItemId = true,
            Items = rows.Select((row, index) => new EnumItem
            {
                Name = row.Identifier("enum_member"),
                Value = index.ToString(CultureInfo.InvariantCulture),
                Alias = "",
                Comment = row.Get("enum_description"),
                Tags = new(row.Tags("member"))
                {
                    [Prefix + "isDefault"] = row.Bool("is_default").ToString()
                }
            }).ToList()
        };
    }

    /// <summary>
    /// 校验漏斗步骤引用当前事件内必填且无默认值的枚举参数。
    /// </summary>
    private static string ValidateStepField(Row row, string kind, List<List<Row>> parameters)
    {
        string stepField = row.Get("step_field");
        if (kind == "event")
        {
            if (stepField.Length != 0)
            {
                throw row.Error("普通事件不能配置 step_field");
            }
            return "";
        }
        if (!Regex.IsMatch(stepField, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw row.Error("漏斗 step_field 必须引用当前事件的参数名");
        }

        var block = parameters.SingleOrDefault(value => value[0].Get("parameter") == stepField);
        if (block == null)
        {
            throw row.Error("漏斗 step_field 参数不存在: " + stepField);
        }
        Row field = block[0];
        if (field.Get("type") != "enum")
        {
            throw row.Error("漏斗步骤必须是枚举参数: " + stepField);
        }
        if (!field.Bool("required") || field.Get("default").Length != 0)
        {
            throw row.Error("漏斗步骤必须必填且不配置默认值: " + stepField);
        }
        return stepField;
    }

    /// <summary>
    /// 按实际纵向合并区域建立参数区块，并检查区块边界。
    /// </summary>
    private static List<List<Row>> CreateParameterBlocks(
        List<Row> rows,
        List<Row> eventRows,
        string[] metadataColumns)
    {
        var blocks = rows
            .GroupBy(row => (row.File, row.ParameterOwnerLine))
            .Select(group => group.OrderBy(row => row.Line).ToList())
            .ToList();
        foreach (var block in blocks)
        {
            Row owner = block[0];
            if (owner.Get("parameter").Length == 0)
            {
                throw owner.Error("'parameter' 不能为空；普通空白单元格不会继承上一参数");
            }
            if (eventRows != null && block.Any(row => row.EventOwnerLine != owner.EventOwnerLine))
            {
                throw owner.Error("参数合并区域不能跨越事件区块");
            }
            InheritMetadata(block, metadataColumns, "参数");
        }
        return blocks;
    }

    /// <summary>
    /// 判断当前行是否包含参数或内联枚举配置。
    /// </summary>
    private static bool HasParameterData(Row row) =>
        row.Get("parameter").Length != 0 || EnumColumns.Any(column => row.Get(column).Length != 0);

    /// <summary>
    /// 禁止基础类型参数填写枚举成员配置。
    /// </summary>
    private static void EnsureNoEnumValues(IEnumerable<Row> rows)
    {
        foreach (Row row in rows)
        {
            string column = EnumColumns.FirstOrDefault(name => row.Get(name).Length != 0);
            if (column != null)
            {
                throw row.Error($"基础类型参数不能配置 '{column}'");
            }
        }
    }

    /// <summary>
    /// 拒绝旧三表选项，并提示迁移到当前纵向词典。
    /// </summary>
    private static void RejectLegacyOptions()
    {
        foreach (string option in new[] { "parametersSheet", "enumsSheet", "commonGroup" })
        {
            if (EnvManager.Current.TryGetOption("analytics", option, false, out _))
            {
                throw new InvalidOperationException(
                    $"[Analytics] analytics.{option} 已废弃，请迁移到 analytics.commonParametersSheet");
            }
        }
    }

    /// <summary>
    /// 读取当前统计扩展的必需配置选项。
    /// </summary>
    private static string Option(string name) => EnvManager.Current.GetOption("analytics", name, false);

    /// <summary>
    /// 创建携带统计类别及来源位置标签的原始 Schema Bean。
    /// </summary>
    private static RawBean Bean(string name, Row row, string kind, string comment) => new()
    {
        Name = name,
        Namespace = "",
        Parent = "",
        Alias = "",
        Sep = "",
        Source = row.Source,
        Comment = comment,
        Tags = row.Tags(kind),
        Groups = new(),
        Fields = new()
    };

    /// <summary>
    /// 创建原始 Schema 字段，并保留词典来源及统计扩展标签。
    /// </summary>
    private static RawField Field(
        string name,
        string type,
        Row row,
        Dictionary<string, string> tags,
        string comment)
    {
        foreach (var pair in row.Tags("field"))
        {
            tags[pair.Key] = pair.Value;
        }
        return new RawField
        {
            Name = name,
            Type = type,
            Alias = "",
            Comment = comment,
            Tags = tags,
            Groups = new(),
            Variants = new()
        };
    }

    /// <summary>
    /// 检查指定列在当前定义范围内唯一，错误定位到重复行。
    /// </summary>
    private static void Unique(List<Row> rows, string column)
    {
        var seen = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (Row row in rows)
        {
            string value = row.NonEmpty(column);
            if (seen.TryGetValue(value, out Row previous))
            {
                throw row.Error(
                    $"重复的 {column}: '{value}'；首次定义: {previous.File}@{previous.Sheet}:{previous.Line}");
            }
            seen.Add(value, row);
        }
    }

    /// <summary>
    /// 将词典名称转换为生成类型使用的 Pascal 标识符。
    /// </summary>
    private static string ToPascalIdentifier(string value) =>
        string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
