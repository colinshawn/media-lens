## ADDED Requirements

### Requirement: 提供统一及指定平台解析路径

系统 SHALL 在 `/api/v1` 下提供 `POST /resolve` 与 `POST /platforms/{platform}/resolve`，两者共用仅含 `text` 的请求体、无 Cookie 解析管线、媒体模型和错误契约。统一入口 MUST 自动选择已注册且启用的适配器；显式入口 MUST 将路径平台作为强制约束，不在请求体重复要求平台字段。

#### Scenario: 统一入口解析抖音
- **WHEN** 调用方向 `/api/v1/resolve` 提交已启用且通过验证的抖音作品分享
- **THEN** 系统选择抖音适配器并返回 `platform: douyin`，调用方不需要预先识别平台

#### Scenario: 指定平台解析抖音
- **WHEN** 调用方向 `/api/v1/platforms/douyin/resolve` 提交同一作品分享
- **THEN** 系统使用与统一入口相同的服务和结果契约，显式平台只增加匹配约束

### Requirement: 通过静态注册表扩展平台

系统 MUST 使用同一内存适配器注册表提供平台分发和能力描述，首期只注册 `douyin`。平台标识 SHALL 为稳定小写字符串，响应 `platform` MUST 与适配器标识一致。注册新平台 SHALL 不要求复制统一解析端点或引入数据库；未来平台 MUST 在独立验证并注册后才出现在发现结果中。

#### Scenario: 注册第二个平台替身
- **WHEN** 协议测试中注册一个具有独立链接规则和结果的平台替身
- **THEN** 原有统一及指定平台路由可以选择该替身，返回其平台标识，并保持抖音行为与原有媒体契约一致

#### Scenario: 尚未注册未来平台
- **WHEN** 项目尚未实现或注册某个计划中的平台
- **THEN** 平台发现结果不列出该平台，也不宣称其解析能力已支持

### Requirement: 平台发现只返回已注册且如实描述的能力

系统 SHALL 提供 `GET /api/v1/platforms`，返回固定根字段 `requestId` 与 `platforms`，前者为本次请求关联字符串，后者为非 null 数组；每项完整包含字符串 `id`、字符串 `displayName`、布尔 `enabled` 及非 null 对象 `capabilities`。四类能力键 `video`、`imageAlbum`、`livePhoto`、`mixedAlbum` MUST 全部出现，状态 MUST 为 `unverified`、`supported` 或 `unsupported`；未来注册平台继续使用同一响应字段。该接口 MUST 仅读取内存注册信息，不请求上游、不依赖持久化存储、不发送或返回 Cookie。

#### Scenario: 初始验证尚未通过
- **WHEN** 只注册抖音但匿名验证尚未完成
- **THEN** 发现结果只包含 `douyin`，未通过的能力标记为 `unverified`，适配器不因注册而自动启用解析

#### Scenario: 发现包含被关闭的注册平台
- **WHEN** 已注册适配器的运行配置为关闭
- **THEN** 发现结果保留该项并返回 `enabled: false`，不把配置启用状态当作上游实时健康状态

### Requirement: 明确区分平台选择错误

系统 MUST 在平台网络访问前检查显式平台是否注册和启用，并校验候选链接归属；短链展开后的最终作品链接同样受平台约束。未知显式平台 SHALL 返回 `404 PLATFORM_NOT_FOUND`，已注册但未启用平台或统一入口无可用适配器 SHALL 返回 `422 PLATFORM_NOT_SUPPORTED`，显式链接归属不符 SHALL 返回 `400 PLATFORM_MISMATCH`；均不得静默切换到其他平台。

#### Scenario: 显式平台未注册
- **WHEN** 解析路径中的平台标识不在注册表
- **THEN** 返回 `404 PLATFORM_NOT_FOUND`，不发起平台请求

#### Scenario: 平台已注册但未启用
- **WHEN** 调用方指定已关闭的抖音适配器
- **THEN** 返回 `422 PLATFORM_NOT_SUPPORTED`，不因分享公开而绕过启用策略

#### Scenario: 输入或展开结果属于其他平台
- **WHEN** 指定抖音入口的文本没有抖音链接但存在其他平台 URL，或展开后作品归属不同
- **THEN** 返回 `400 PLATFORM_MISMATCH`，不调用其他适配器或将该作品 ID 交给抖音元数据入口

### Requirement: 保持路径版本与媒体类型一致性

系统 SHALL 将兼容版本放在 `/api/v1` 前缀；单纯新增平台或兼容可选字段 MUST 保留现有路径与请求语义。视频、图片和实况 SHALL 使用共用解析路径及结果类型表达，不因媒体类型重复注册解析入口。破坏必需字段或既有类型语义的变更 MUST 单独规划新版本。

#### Scenario: 增加平台保持统一入口
- **WHEN** 一个新平台完成验证并注册
- **THEN** 现有调用方继续使用 `/api/v1/resolve`，指定平台调用方使用相同 `/platforms/{platform}/resolve` 模式，既有抖音路径仍有效

#### Scenario: 同一平台出现不同媒体类型
- **WHEN** 抖音分享分别解析为视频、图片图集和实况图集
- **THEN** 使用同一解析路径，以返回的 `workType` 与媒体项 `type` 区分，调用方无需改用按媒体类型拆分的路径
