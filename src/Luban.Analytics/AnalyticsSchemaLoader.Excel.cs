using System.Globalization;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Luban.DataLoader.Builtin.Excel;
using Luban.Schema;

namespace Luban.Analytics;

/// <summary>
/// 读取统计工作表，并根据实际合并区域恢复事件和参数的纵向层级。
/// </summary>
public sealed partial class AnalyticsSchemaLoader
{
    /// <summary>
    /// 使用 Luban Excel 读取器载入指定工作表，并建立纵向区块。
    /// </summary>
    private static List<Row> ReadRows(string file, string sheet, string[] columns, bool eventSheet)
    {
        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = SheetLoadUtil.CreateSheetReader(Path.GetExtension(file), stream);
        do
        {
            if (reader.Name == sheet)
            {
                return ReadWorksheet(file, sheet, reader, columns, eventSheet);
            }
        } while (reader.NextResult());
        throw new InvalidOperationException($"[Analytics] {file}: 工作表 '{sheet}' 不存在");
    }

    /// <summary>
    /// 读取字段行和数据行，按实际合并区域恢复事件及参数归属。
    /// </summary>
    private static List<Row> ReadWorksheet(
        string file,
        string sheet,
        IExcelDataReader reader,
        string[] columns,
        bool eventSheet)
    {
        var rows = new List<Row>();
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
            cellsByLine[line] = cells;
            string marker = cells.FirstOrDefault()?.Trim() ?? "";
            if (marker == "##var")
            {
                if (header != null)
                {
                    throw new InvalidOperationException($"[Analytics] {file}@{sheet}:{line}: 重复表头");
                }
                header = ParseHeader(file, sheet, line, cells, columns, eventSheet);
                ValidateMergeRegions(file, sheet, line, header, merges, eventSheet);
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
                pair => pair.Key,
                pair => pair.Value < cells.Length ? cells[pair.Value] : "");
            var row = new Row(file, sheet, line, line, line, values);
            rows.Add(RestoreMergedValues(row, header, merges, cellsByLine, eventSheet));
        }

        if (header == null)
        {
            throw new InvalidOperationException($"[Analytics] {file}@{sheet}: 缺少 ##var 表头");
        }
        ValidateMergeContainment(rows, header, merges, eventSheet);
        if (eventSheet)
        {
            foreach (var block in rows.GroupBy(row => (row.File, row.EventOwnerLine)).Select(group => group.ToList()))
            {
                if (block[0].Get("event_name").Length == 0)
                {
                    throw block[0].Error("'event_name' 不能为空；普通空白单元格不会继承上一事件");
                }
                InheritMetadata(block, EventMetadataColumns, "事件");
            }
        }
        return rows;
    }

    /// <summary>
    /// 检查合并区域只覆盖连续数据行，且不跨越事件或参数边界。
    /// </summary>
    private static void ValidateMergeContainment(
        List<Row> rows,
        Dictionary<string, int> header,
        CellRange[] merges,
        bool eventSheet)
    {
        foreach (var merge in merges)
        {
            int firstLine = merge.FromRow + 1;
            int lastLine = merge.ToRow + 1;
            var mergedRows = rows
                .Where(row => row.Line >= firstLine && row.Line <= lastLine)
                .ToList();
            if (mergedRows.Count != lastLine - firstLine + 1)
            {
                Row location = mergedRows.FirstOrDefault();
                string source = location == null
                    ? $"{firstLine}"
                    : $"{location.File}@{location.Sheet}:{firstLine}";
                throw new InvalidOperationException(
                    $"[Analytics] {source}: 合并区域只能覆盖连续的数据行");
            }

            string column = header.Single(pair => pair.Value == merge.FromColumn).Key;
            if (eventSheet && EventMetadataColumns.Contains(column) && column != "event_name" &&
                mergedRows.Select(row => row.EventOwnerLine).Distinct().Count() != 1)
            {
                throw mergedRows[0].Error($"事件信息列 '{column}' 的合并区域不能跨越事件区块");
            }
            if ((eventSheet ? EventParameterColumns : CommonParameterColumns).Contains(column) &&
                column != "parameter" &&
                mergedRows.Select(row => row.ParameterOwnerLine).Distinct().Count() != 1)
            {
                throw mergedRows[0].Error($"参数信息列 '{column}' 的合并区域不能跨越参数区块");
            }
        }
    }

    /// <summary>
    /// 解析字段行，检查重复列、必需列和旧版三表结构。
    /// </summary>
    private static Dictionary<string, int> ParseHeader(
        string file,
        string sheet,
        int line,
        string[] cells,
        string[] requiredColumns,
        bool eventSheet)
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

        if (header.ContainsKey("enum_report_value") || header.ContainsKey("report_value"))
        {
            throw new InvalidOperationException(
                $"[Analytics] {file}@{sheet}:{line}: 请删除独立枚举上报值列，将上报原值填写到 enum_member，default 同样填写该原值");
        }

        string[] legacy = eventSheet
            ? new[] { "parameter_group", "once", "once_key", "enum_name", "member" }
            : new[] { "group", "enum_name", "member" };
        if (legacy.Any(header.ContainsKey))
        {
            throw new InvalidOperationException(
                $"[Analytics] {file}@{sheet}:{line}: 检测到旧三表结构，请迁移到事件内联参数和独立基础参数表");
        }
        foreach (string column in requiredColumns)
        {
            if (!header.ContainsKey(column))
            {
                throw new InvalidOperationException(
                    $"[Analytics] {file}@{sheet}:{line}: 缺少列 '{column}'，请检查当前纵向词典格式");
            }
        }
        return header;
    }

    /// <summary>
    /// 校验实际合并区域的位置和允许合并的列。
    /// </summary>
    private static void ValidateMergeRegions(
        string file,
        string sheet,
        int headerLine,
        Dictionary<string, int> header,
        CellRange[] merges,
        bool eventSheet)
    {
        var supported = new HashSet<string>(
            eventSheet ? EventMetadataColumns.Concat(EventParameterColumns) : CommonParameterColumns,
            StringComparer.Ordinal);
        foreach (var merge in merges)
        {
            string column = header.FirstOrDefault(pair => pair.Value == merge.FromColumn).Key;
            if (merge.FromRow <= headerLine - 1 ||
                merge.FromColumn != merge.ToColumn ||
                column == null ||
                !supported.Contains(column))
            {
                throw new InvalidOperationException(
                    $"[Analytics] {file}@{sheet}:{merge.FromRow + 1}: 仅支持数据区事件或参数信息列的纵向合并");
            }
        }
    }

    /// <summary>
    /// 根据实际合并区域恢复数据及归属，不对普通空白单元格隐式继承。
    /// </summary>
    private static Row RestoreMergedValues(
        Row row,
        Dictionary<string, int> header,
        CellRange[] merges,
        Dictionary<int, string[]> cellsByLine,
        bool eventSheet)
    {
        int eventOwnerLine = row.Line;
        int parameterOwnerLine = row.Line;
        foreach (var merge in merges)
        {
            int rowIndex = row.Line - 1;
            if (rowIndex < merge.FromRow || rowIndex > merge.ToRow)
            {
                continue;
            }
            string column = header.Single(pair => pair.Value == merge.FromColumn).Key;
            string[] anchorCells = cellsByLine[merge.FromRow + 1];
            string anchor = merge.FromColumn < anchorCells.Length ? anchorCells[merge.FromColumn] : "";
            string current = row.Values[column];
            if (rowIndex != merge.FromRow && current.Length != 0 && current.Trim() != anchor.Trim())
            {
                throw row.Error($"合并单元格内部配置与首行冲突: '{column}'");
            }
            row.Values[column] = anchor;
            if (eventSheet && column == "event_name")
            {
                eventOwnerLine = merge.FromRow + 1;
            }
            if (column == "parameter")
            {
                parameterOwnerLine = merge.FromRow + 1;
            }
        }
        return row with
        {
            EventOwnerLine = eventOwnerLine,
            ParameterOwnerLine = parameterOwnerLine
        };
    }

    /// <summary>
    /// 在已确定的区块内继承首行元数据，并拒绝区块内冲突配置。
    /// </summary>
    private static void InheritMetadata(List<Row> block, string[] columns, string label)
    {
        Row owner = block[0];
        foreach (Row row in block.Skip(1))
        {
            foreach (string column in columns)
            {
                string current = row.Get(column, false);
                string anchor = owner.Get(column, false);
                if (current.Length != 0 && current.Trim() != anchor.Trim())
                {
                    throw row.Error($"{label}续行不能配置不同的 '{column}'");
                }
                row.Values[column] = anchor;
            }
        }
    }

    /// <summary>
    /// 保存词典行内容、合并归属及文件位置，供解析和错误定位使用。
    /// </summary>
    private sealed record Row(
        string File,
        string Sheet,
        int Line,
        int EventOwnerLine,
        int ParameterOwnerLine,
        Dictionary<string, string> Values)
    {
        public SchemaSource Source => SchemaSource.Create(File, Sheet);

        /// <summary>
        /// 读取指定列，可按需保留字符串前后的空白。
        /// </summary>
        public string Get(string name, bool trim = true)
        {
            string value = Values.GetValueOrDefault(name, "");
            return trim ? value.Trim() : value;
        }

        /// <summary>
        /// 读取必填列，空值时报告当前配表位置。
        /// </summary>
        public string NonEmpty(string name) =>
            Get(name).Length != 0 ? Get(name) : throw Error($"'{name}' 不能为空");

        /// <summary>
        /// 读取并校验词典标识符，错误定位到当前行。
        /// </summary>
        public string Identifier(string name)
        {
            string value = NonEmpty(name);
            if (!Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                throw Error($"'{name}' 不是合法标识符: '{value}'");
            }
            return value;
        }

        /// <summary>
        /// 解析词典布尔值，不合法时报告当前配表位置。
        /// </summary>
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
            return bool.TryParse(value, out bool result)
                ? result
                : throw Error($"'{name}' 必须为 true/false 或 1/0");
        }

        /// <summary>
        /// 解析以分隔符配置的标签列表。
        /// </summary>
        public string[] List(string name)
        {
            if (Get(name).Length == 0)
            {
                return Array.Empty<string>();
            }
            string[] values = Get(name).Split(',').Select(value => value.Trim()).ToArray();
            if (values.Any(string.IsNullOrEmpty) || values.Distinct().Count() != values.Length)
            {
                throw Error($"'{name}' 列表包含空项或重复项");
            }
            return values;
        }

        /// <summary>
        /// 创建包含统计类别和词典来源位置的 Schema 标签。
        /// </summary>
        public Dictionary<string, string> Tags(string kind) => new()
        {
            [Prefix + "kind"] = kind,
            [Prefix + "origin"] = $"{File}@{Sheet}:{Line}"
        };

        /// <summary>
        /// 构造包含来源位置的统计词典错误。
        /// </summary>
        public Exception Error(string message) =>
            new InvalidOperationException($"[Analytics] {File}@{Sheet}:{Line}: {message}");
    }
}
