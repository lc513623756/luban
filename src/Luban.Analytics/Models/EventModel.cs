namespace Luban.Analytics;

/// <summary>
/// 事件定义与强类型业务方法的生成信息。
/// </summary>
public sealed class EventModel
{
    /// <summary>
    /// 漏斗步骤在生成参数类中的属性名。
    /// </summary>
    public string StepProperty { get; set; }

    public string Name { get; init; }

    public string Definition { get; init; }
    public string EventName { get; init; }
    public string Comment { get; init; }
    public bool IsFunnel { get; init; }
    public string StepField { get; init; }
    public string ReceiverIds { get; set; }
    public string ParametersType { get; set; }
    public string ParametersArgument { get; set; }
    public bool HasParameters => !string.IsNullOrEmpty(ParametersType);
}
