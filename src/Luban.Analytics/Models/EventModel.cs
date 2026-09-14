namespace Luban.Analytics;

/// <summary>
/// 事件定义与强类型业务方法的生成信息。
/// </summary>
public sealed class EventModel
{
    public string Name { get; init; }
    public string Definition { get; init; }
    public string EventName { get; init; }
    public string Comment { get; init; }
    public string OnceKey { get; init; }
    public string PlatformTags { get; set; }
    public string Signature { get; set; }
    public List<EventGroupModel> Groups { get; } = new();
}
