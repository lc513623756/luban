namespace Luban.Analytics;

/// <summary>
/// 单个参数类型的生成模型，包含字段和必填参数优先的构造参数列表。
/// </summary>
public sealed class ParameterModel
{
    public bool IsCommon { get; init; }
    public string InputType { get; set; }
    public string CollectedType { get; set; }
    public string Name { get; init; }
    public string Comment { get; init; }
    public List<FieldModel> ConstructorFields { get; set; }
    public List<FieldModel> Fields { get; } = new();
}
