# Luban.Analytics

基于 Luban 5.1 的工具扩展程序集，通过 `[RegisterBehaviour]` 注册。
主 Luban 工程引用本程序集，构建时包含插件及默认 Scriban 模板。

- `AnalyticsSchemaLoader`：注册 schema 类型 `analytics`，复用内置 Excel reader。
  读取三张统计表，构造 `RawBean`、`RawEnum`，通过 `analytics.*` Tags 传递元数据。
  不注册 `RawTable`，不需要运行时配置数据，也不使用静态可变状态。
- `AnalyticsCodeTarget`：注册 `cs-analytics`，复用 C# CodeStyle、模板搜索和输出机制。
  校验统计规则，将已编译定义转换为明确的模板模型，再输出代码。
- `Templates/cs-analytics/`：使用 bean、enum、manager 和事件/接口模板，
  按类型输出独立文件；枚举转换随对应枚举文件生成，可通过 `customTemplateDir` 分别覆盖。

生成代码携带路由和 OnceKey 元数据，不识别漏斗；send.sbn 与 bind_common.sbn 可独立覆盖发送入口和公共参数绑定，业务事件方法返回 void。
配置中的统计工作簿合并处理，OnceKey 校验覆盖所有配置文件。
支持参数组、枚举名称纵向合并；事件名称合并区块内每行配置一个 parameter_group，保留原始配表行定位，不使用任意空白向下填充。
游戏不引用本工具程序集。

仓库级用法、示例配表和集成检查见根目录 `AnalyticsTables/README.md`。

## 静态入口与框架接入

生成静态事件入口、事件定义、参数类型和 IAnalyticsCommonParametersCollector，不生成发送结果、sink 或运行时快照。公共参数类型提供 ToParameters 转换。send.sbn 与 bind_common.sbn 分别定义转发和收集器绑定，内置片段未配置时抛出配置错误。运行时元数据与公共参数采集由目标框架管理。

基础组通过 injected 标记区分传入与采集字段，并生成完整参数、Input、CollectedParameters 和 Compose。收集器只返回 CollectedParameters。injected 默认 false，只允许基础组必填且没有默认值的字段；导表器不按名称推断取值。具体框架映射在项目 bind_common 模板中通过具名构造参数定义。
