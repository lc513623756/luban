namespace Luban.Analytics;

/// <summary>
/// 完整的统计生成模型，提供模板需要的类型定义和项目接入代码。
/// </summary>
public sealed class GenerationModel
{
    public string Namespace { get; init; }
    public string Manager { get; init; }
    public string CommonInputType { get; init; }
    public string CommonCollectedType { get; init; }
    public string CommonType { get; init; }
    public List<ParameterModel> Parameters { get; init; }
    public List<EnumModel> Enums { get; init; }
    public List<EventModel> Events { get; init; }
}
