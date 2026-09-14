namespace Luban.Analytics;

/// <summary>
/// 单个枚举成员及其上报值和描述。
/// </summary>
public sealed class EnumItemModel
{
    public string Name { get; init; }
    public int Value { get; init; }
    public string ReportValue { get; init; }
    public string Comment { get; init; }
}
