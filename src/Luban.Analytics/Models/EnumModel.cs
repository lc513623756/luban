namespace Luban.Analytics;

/// <summary>
/// 统计枚举的生成模型及其配置的上报值。
/// </summary>
public sealed class EnumModel
{
    public string Name { get; init; }
    public List<EnumItemModel> Items { get; } = new();
}
