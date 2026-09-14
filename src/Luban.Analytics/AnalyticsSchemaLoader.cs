using System.Globalization;
using ExcelDataReader;
using System.Text.RegularExpressions;
using Luban.DataLoader.Builtin.Excel;
using Luban.RawDefs;
using Luban.Schema;

namespace Luban.Analytics;

/// <summary>
/// 读取统计工作簿并构建 Luban 定义，校验事件、参数及枚举的跨表关系。
/// </summary>
[SchemaLoader("analytics", "xlsx", "xls", "xlsm")]
public sealed class AnalyticsSchemaLoader : SchemaLoaderBase
{
    internal const string Prefix = "analytics.";
    private static readonly HashSet<string> PrimitiveTypes = new()
    {
        "int",
        "long",
        "float",
        "double",
        "bool",
        "string"
    };

    public override void Load(string fileName)
    {
        string eventsSheet = Option("eventsSheet");
        string parametersSheet = Option("parametersSheet");
        string enumsSheet = Option("enumsSheet");
        string commonGroup = Option("commonGroup");
        if (new[]
        {
            eventsSheet,
            parametersSheet,
            enumsSheet
        }.Distinct().Count() != 3)
        {
            throw new InvalidOperationException("[Analytics] 三个工作表映射必须不同");
        }

        // 一次汇总配置的统计工作簿，跨文件校验复用定义和唯一性。
        // 数据保持在当前生成流程内，不使用静态状态。
        var imports = GenerationContext.GlobalConf.Imports.Where(i => i.Type == "analytics")
            .Select(i => Path.GetFullPath(Path.Combine(GenerationContext.GlobalConf.ConfigDir, i.FileName))).ToList();
        if (imports.Count == 0)
        {
            imports.Add(Path.GetFullPath(fileName));
        }

        if (Path.GetFullPath(fileName) != imports[0])
        {
            return;
        }

        var enums = imports.SelectMany(file => ReadRows(file, enumsSheet,
            "enum_name", "member", "report_value", "is_default", "description"))
            .GroupBy(row => row.Identifier("enum_name"))
            .ToDictionary(group => group.Key, group => group.ToList());
        var parameters = imports.SelectMany(file => ReadRows(file, parametersSheet,
            "group", "parameter", "type", "required", "default", "description"))
            .GroupBy(r => r.Identifier("group")).ToDictionary(g => g.Key, g => g.ToList());
        var events = imports.SelectMany(file => ReadRows(file, eventsSheet,
            "event_name", "description", "parameter_group", "platform_tags", "once", "once_key")).ToList();
        if (!parameters.ContainsKey(commonGroup))
        {
            throw new InvalidOperationException($"[Analytics] {fileName}@{parametersSheet}: 基础参数组 '{commonGroup}' 不存在");
        }

        if (events.Count == 0)
        {
            throw new InvalidOperationException($"[Analytics] {fileName}@{eventsSheet}: 没有事件定义");
        }

        // 完成所有校验后再注册定义，避免失败时留下不完整状态。
        var rawEnums = new List<RawEnum>();
        var rawBeans = new List<RawBean>();
        foreach (var (name, rows) in enums)
        {
            Unique(rows, "member");
            Unique(rows, "report_value");
            if (rows.Count(r => r.Bool("is_default")) > 1)
            {
                throw rows.First().Error($"枚举 '{name}' 有多个默认成员");
            }

            rawEnums.Add(new RawEnum
            {
                Name = name,
                Namespace = "",
                Source = rows[0].Source,
                Comment = name,
                Tags = rows[0].Tags("enum"),
                Groups = new(),
                IsUniqueItemId = true,
                Items = rows.Select((r, i) => new EnumItem
                {
                    Name = r.Identifier("member"),
                    Value = i.ToString(CultureInfo.InvariantCulture),
                    Alias = "",
                    Comment = r.Get("description"),
                    Tags = new(r.Tags("member"))
                    {
                        [Prefix + "reportValue"] = r.NonEmpty("report_value"),
                        [Prefix + "isDefault"] = r.Bool("is_default").ToString()
                    }
                }).ToList()
            });
        }
        foreach (var (name, rows) in parameters)
        {
            Unique(rows, "parameter");
            if (enums.ContainsKey(name) || PrimitiveTypes.Contains(name))
            {
                throw rows[0].Error($"参数组 '{name}' 与枚举或基础类型重名");
            }

            var bean = Bean(name, rows[0], name == commonGroup ? "common" : "parameters");
            foreach (var row in rows)
            {
                string type = row.NonEmpty("type");
                if (!PrimitiveTypes.Contains(type) && !enums.ContainsKey(type))
                {
                    throw row.Error($"未知参数类型 '{type}'；支持基础类型或本词典中的枚举");
                }

                bool required = row.Bool("required");
                bool injected = row.Bool("injected");
                if (injected && name != commonGroup)
                {
                    throw row.Error("injected 只允许用于基础参数组");
                }
                if (injected && !required)
                {
                    throw row.Error("传入参数必须必填");
                }
                string defaultValue = row.Get("default", trim: false);
                if (required && defaultValue.Length != 0)
                {
                    throw row.Error("必填参数不能配置默认值");
                }

                bean.Fields.Add(Field(row.Identifier("parameter"), type, row, new()
                {
                    [Prefix + "required"] = required.ToString(),
                    [Prefix + "injected"] = injected.ToString(),
                    [Prefix + "default"] = defaultValue
                }));
            }
            rawBeans.Add(bean);
        }

        // 每个纵向合并的事件名称区块定义一个事件，每行引用一个参数组。
        var eventBlocks = events.GroupBy(r => (r.File, r.OwnerLine)).Select(g => g.ToList()).ToList();
        Unique(eventBlocks.Select(block => block[0]).ToList(), "event_name");
        foreach (var block in eventBlocks)
        {
            var row = block[0];
            string eventName = row.Identifier("event_name");
            var bean = Bean("__analytics_event_" + eventName, row, "event");
            if (parameters.ContainsKey(bean.Name) || enums.ContainsKey(bean.Name))
            {
                throw row.Error("名称与统计内部事件定义冲突");
            }

            var groupRows = block.Where(r => r.Get("parameter_group").Length != 0).ToList();
            Unique(groupRows, "parameter_group");
            string[] groups = groupRows.Select(r => r.Identifier("parameter_group")).ToArray();
            for (int index = 0; index < groups.Length; index++)
            {
                string group = groups[index];
                if (!parameters.ContainsKey(group))
                {
                    throw groupRows[index].Error($"引用的参数组 '{group}' 不存在");
                }

                if (group == commonGroup)
                {
                    throw groupRows[index].Error("基础参数组自动包含，不应在事件中重复引用");
                }
            }
            bool once = row.Bool("once");
            string onceKey = row.Get("once_key");
            if (!once && onceKey.Length != 0)
            {
                throw row.Error("非一次性事件不能配置 once_key");
            }

            bean.Tags[Prefix + "eventName"] = eventName;
            bean.Tags[Prefix + "platformTags"] = string.Join(",", row.List("platform_tags"));
            bean.Tags[Prefix + "onceKey"] = once ? (onceKey.Length == 0 ? eventName : onceKey) : "";
            bean.Fields.Add(Field("__common", commonGroup, row, new()
            {
                [Prefix + "common"] = "True"
            }));
            for (int i = 0; i < groups.Length; i++)
            {
                bean.Fields.Add(Field("group" + i, groups[i], groupRows[i], new()));
            }

            rawBeans.Add(bean);
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

    private static string Option(string name) => EnvManager.Current.GetOption("analytics", name, false);

    private static RawBean Bean(string name, Row row, string kind) => new()
    {
        Name = name,
        Namespace = "",
        Parent = "",
        Alias = "",
        Sep = "",
        Source = row.Source,
        Comment = row.Get("description"),
        Tags = row.Tags(kind),
        Groups = new(),
        Fields = new()
    };

    private static RawField Field(string name, string type, Row row, Dictionary<string, string> tags)
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
            Comment = row.Get("description"),
            Tags = tags,
            Groups = new(),
            Variants = new()
        };
    }

    private static void Unique(List<Row> rows, string column)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!seen.Add(row.NonEmpty(column)))
            {
                throw row.Error($"重复的 {column}: '{row.Get(column)}'");
            }
        }
    }

    private static List<Row> ReadRows(string file, string sheet, params string[] columns)
    {
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = SheetLoadUtil.CreateSheetReader(Path.GetExtension(file), stream);

        do
        {
            if (reader.Name == sheet)
            {
                return ReadWorksheet(file, sheet, reader, columns);
            }
        } while (reader.NextResult());

        throw new InvalidOperationException($"[Analytics] {file}: 工作表 '{sheet}' 不存在");
    }

    private static List<Row> ReadWorksheet(
        string file,
        string sheet,
        IExcelDataReader reader,
        string[] columns)
    {
        var result = new List<Row>();
        var cellsByLine = new Dictionary<int, string[]>();
        var merges = reader.MergeCells ?? Array.Empty<CellRange>();
        Dictionary<string, int> header = null;
        int line = 0;

        while (reader.Read())
        {
            ++line;
            string[] cells = Enumerable.Range(0, reader.FieldCount)
                .Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "")
                .ToArray();
            cellsByLine.Add(line, cells);
            string marker = cells.FirstOrDefault()?.Trim() ?? "";

            if (marker == "##var")
            {
                if (header != null)
                {
                    throw new InvalidOperationException($"[Analytics] {file}@{sheet}:{line}: 重复表头");
                }

                header = ParseHeader(file, sheet, line, cells, columns);
                ValidateMergeRegions(file, sheet, line, header, columns[0], merges);
                continue;
            }

            if (marker.StartsWith("##", StringComparison.Ordinal) || cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (header == null)
            {
                throw new InvalidOperationException($"[Analytics] {file}@{sheet}:{line}: 数据前需要 ##var 表头");
            }

            if (marker.Length != 0)
            {
                throw new InvalidOperationException($"[Analytics] {file}@{sheet}:{line}: 数据行首列应为空");
            }

            var values = header.ToDictionary(
                column => column.Key,
                column => column.Value < cells.Length ? cells[column.Value] : "");
            var row = new Row(file, sheet, line, line, values);
            row = RestoreMergedColumns(row, header, merges, cellsByLine, columns[0]);

            if (columns[0] == "event_name")
            {
                InheritEventMetadata(row, header, merges, result);
            }

            result.Add(row);
        }

        if (header == null)
        {
            throw new InvalidOperationException($"[Analytics] {file}@{sheet}: 缺少 ##var 表头");
        }

        return result;
    }

    private static Dictionary<string, int> ParseHeader(
        string file,
        string sheet,
        int line,
        string[] cells,
        string[] requiredColumns)
    {
        var header = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int index = 1; index < cells.Length; index++)
        {
            string column = cells[index].Trim();

            if (column.Length != 0 && !header.TryAdd(column, index))
            {
                throw new InvalidOperationException($"[Analytics] {file}@{sheet}:{line}: 重复列 '{column}'");
            }
        }

        foreach (string column in requiredColumns)
        {
            if (!header.ContainsKey(column))
            {
                throw new InvalidOperationException($"[Analytics] {file}@{sheet}:{line}: 缺少列 '{column}'");
            }
        }

        return header;
    }

    private static void ValidateMergeRegions(
        string file,
        string sheet,
        int headerLine,
        Dictionary<string, int> header,
        string groupingColumn,
        CellRange[] merges)
    {
        var mergeableColumns = groupingColumn == "event_name"
            ? new[] {
                "event_name",
                "description",
                "platform_tags",
                "once",
                "once_key"
            }
            : new[] { groupingColumn };

        foreach (var merge in merges)
        {
            bool inDataRows = merge.FromRow > headerLine - 1;
            bool singleColumn = merge.FromColumn == merge.ToColumn;
            bool supportedColumn = header.Any(column =>
                column.Value == merge.FromColumn && mergeableColumns.Contains(column.Key));

            if (!inDataRows || !singleColumn || !supportedColumn)
            {
                throw new InvalidOperationException(
                    $"[Analytics] {file}@{sheet}:{merge.FromRow + 1}: 仅支持数据区的分组标识和事件元数据列纵向合并");
            }
        }
    }

    /// <summary>
    /// 只恢复实际合并区域的首格，不把普通空白单元格向下填充。
    /// </summary>
    private static Row RestoreMergedColumns(
        Row row,
        Dictionary<string, int> header,
        CellRange[] merges,
        Dictionary<int, string[]> cellsByLine,
        string groupingColumn)
    {
        int ownerLine = row.Line;

        foreach (var merge in merges)
        {
            if (row.Line - 1 < merge.FromRow || row.Line - 1 > merge.ToRow)
            {
                continue;
            }

            string column = header.Single(entry => entry.Value == merge.FromColumn).Key;
            var anchor = cellsByLine[merge.FromRow + 1];
            string value = merge.FromColumn < anchor.Length ? anchor[merge.FromColumn] : "";

            if (row.Line - 1 != merge.FromRow && row.Values[column].Length != 0 && row.Values[column] != value)
            {
                throw row.Error($"合并单元格内部配置与首行冲突: '{column}'");
            }

            row.Values[column] = value;

            if (column == groupingColumn)
            {
                ownerLine = merge.FromRow + 1;
            }
        }

        // 文件行号用于错误定位，OwnerLine 仅用于识别同一个事件区块。
        return row with
        {
            OwnerLine = ownerLine
        };
    }

    private static void InheritEventMetadata(
        Row row,
        Dictionary<string, int> header,
        CellRange[] merges,
        List<Row> previousRows)
    {
        var eventMerge = merges.SingleOrDefault(merge =>
            merge.FromColumn == header["event_name"] &&
            row.Line - 1 >= merge.FromRow && row.Line - 1 <= merge.ToRow);

        foreach (var merge in merges)
        {
            if (merge.FromColumn == header["event_name"] ||
                row.Line - 1 < merge.FromRow || row.Line - 1 > merge.ToRow)
            {
                continue;
            }

            if (eventMerge == null || merge.FromRow < eventMerge.FromRow || merge.ToRow > eventMerge.ToRow)
            {
                throw row.Error("事件元数据合并不能跨越事件区块");
            }
        }

        if (row.OwnerLine == row.Line)
        {
            return;
        }

        var owner = previousRows.SingleOrDefault(previous => previous.Line == row.OwnerLine);

        if (owner == null)
        {
            throw row.Error("事件合并区域缺少定义首行");
        }

        // 首行声明事件信息，续行只声明参数组；重复信息必须与首行一致。
        foreach (string column in new[] {
            "description",
            "platform_tags",
            "once",
            "once_key"
        })
        {
            if (row.Values[column].Length != 0 && row.Values[column].Trim() != owner.Get(column))
            {
                throw row.Error($"事件续行不能配置不同的 '{column}'");
            }

            row.Values[column] = owner.Get(column, trim: false);
        }
    }

    private sealed record Row(string File, string Sheet, int Line, int OwnerLine, Dictionary<string, string> Values)
    {
        public SchemaSource Source => SchemaSource.Create(File, Sheet);
        public string Get(string name, bool trim = true)
        {
            string value = Values.GetValueOrDefault(name, "");
            return trim ? value.Trim() : value;
        }
        public string NonEmpty(string name) => Get(name).Length != 0 ? Get(name) : throw Error($"'{name}' 不能为空");
        public string Identifier(string name)
        {
            string value = NonEmpty(name);
            if (!Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                throw Error($"'{name}' 不是合法标识符: '{value}'");
            }

            return value;
        }
        public bool Bool(string name)
        {
            string value = Get(name);
            if (value.Length == 0 || value == "0")
            {
                return false;
            }

            if (value == "1")
            {
                return true;
            }

            return bool.TryParse(value, out bool result) ? result : throw Error($"'{name}' 必须为 true/false 或 1/0");
        }
        public string[] List(string name)
        {
            if (Get(name).Length == 0)
            {
                return Array.Empty<string>();
            }

            string[] values = Get(name).Split(',').Select(s => s.Trim()).ToArray();
            if (values.Any(string.IsNullOrEmpty) || values.Distinct().Count() != values.Length)
            {
                throw Error($"'{name}' 列表包含空项或重复项");
            }

            return values;
        }
        public Dictionary<string, string> Tags(string kind) => new()
        {
            [Prefix + "kind"] = kind,
            [Prefix + "origin"] = $"{File}@{Sheet}:{Line}"
        };
        public Exception Error(string message) => new InvalidOperationException($"[Analytics] {File}@{Sheet}:{Line}: {message}");
    }
}
