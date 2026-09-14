namespace Luban.Analytics;

/// <summary>
/// 参数字段的生成信息，区分属性名、构造参数名和实际的上报键。
/// </summary>
public sealed class FieldModel
{
    public string Name { get; init; }
    public string Argument { get; init; }
    public string ParameterName { get; init; }
    public string Type { get; init; }
    public bool Injected { get; init; }
    public bool Required { get; init; }
    public string DefaultValue { get; init; }
    public string Key { get; init; }
    public string Comment { get; init; }
    public string ReportExpression { get; init; }
}
