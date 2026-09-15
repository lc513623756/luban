namespace Luban.Analytics;

/// <summary>
/// 统计枚举的生成模型及其配置的上报值。
/// </summary>
public sealed class EnumModel
{
    /// <summary>
    /// 此枚举是否用于漏斗步骤，需要生成一次性键映射。
    /// </summary>
    public bool IsFunnelStep { get; init; }

    public string Name { get; init; }
    public List<EnumItemModel> Items { get; } = new();
}
