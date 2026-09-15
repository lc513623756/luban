# Luban.Analytics

`Luban.Analytics` 是基于 Luban 5.1 的统计代码生成扩展。主程序引用该程序集并加载默认 Scriban 模板，游戏运行时不引用生成工具。

- `AnalyticsSchemaLoader` 注册 `analytics` schema，负责词典语义和 `RawBean` / `RawEnum` 构造；`AnalyticsSchemaLoader.Excel.cs` 负责表头、数据行和实际合并区域读取。两者复用内置 Excel reader、`##var` / `##` 约定和 `SchemaSource`，不生成 `RawTable` 或运行时数据。
- `AnalyticsCodeTarget` 注册 `cs-analytics`，负责事件模型和文件输出；`AnalyticsCodeTarget.Parameters.cs` 负责参数类型、默认值和 C# 名称校验。每种生成类型输出独立文件。
- `Templates/cs-analytics` 提供参数、枚举、事件定义、基础参数采集器、事件接收接口和静态入口。

事件参数直接定义在事件区块中。每个有参数的事件生成自己的 `XxxParameters`；枚举按事件名和参数名自动命名。整张基础参数表按 `analytics.commonTypePrefix` 生成完整类型、框架传入类型、游戏采集类型和枚举，`injected` 只区分框架传入字段与游戏采集字段。

事件方法返回 `bool`，只表示目标框架是否接收。项目在 Game 层实现并绑定 `IAnalyticsEventSink`，其中可以接入 Core 管理器或其它统计框架。生成工具不负责接收器 SDK、缓存、重试或一次性持久化。

`kind=funnel` 时，`step_field` 引用当前事件内必填且无默认值的枚举参数。生成入口用事件名和枚举上报值计算稳定的一次性键，不解释业务步骤顺序。

扩展读取 `analytics.eventsSheet`、`analytics.commonParametersSheet` 和 `analytics.commonTypePrefix`。旧 `parametersSheet`、`enumsSheet`、`commonGroup` 和旧三表表头会给出迁移错误。多个 analytics `schemaFiles` 作为同一套词典校验，错误保留文件、工作表和行位置。

完整配表格式、调用方式和验证命令见 `AnalyticsTables/README.md`。
