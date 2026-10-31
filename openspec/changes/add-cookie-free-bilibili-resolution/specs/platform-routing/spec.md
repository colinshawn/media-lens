## MODIFIED Requirements

### Requirement: 通过静态注册表扩展平台

系统 MUST 使用同一内存适配器注册表提供平台分发和能力描述，保留 `douyin`，并在 Bilibili 严格匿名与完整下载验收通过后生产注册 `bilibili`。平台标识 SHALL 为稳定小写字符串，响应 `platform` MUST 与适配器标识一致。注册新平台 SHALL 不要求复制统一解析端点或引入数据库；未来平台 MUST 在独立验证并注册后才出现在发现结果中。

#### Scenario: 注册第二个平台替身
- **WHEN** 协议测试中注册一个具有独立链接规则和结果的平台替身
- **THEN** 原有统一及指定平台路由可以选择该替身，返回其平台标识，并保持抖音行为与原有媒体契约一致

#### Scenario: 尚未注册未来平台
- **WHEN** 项目尚未实现或注册某个计划中的平台
- **THEN** 平台发现结果不列出该平台，也不宣称其解析能力已支持

#### Scenario: Bilibili 验证通过后生产接入
- **WHEN** Bilibili 已完成真实匿名及完整文件验证并注册启用
- **THEN** 统一入口和 `/api/v1/platforms/bilibili/resolve` 共用原解析服务与 DTO，抖音路径及原有四类能力不变

#### Scenario: Bilibili 尚未通过真实验收
- **WHEN** 新适配器只有实现或替身测试，尚无完整匿名媒体下载证据
- **THEN** 生产注册表保持已有已验证平台，不预注册启用 Bilibili；受控测试中的关闭替身不算生产支持

#### Scenario: 沿用平台错误的现有优先级
- **WHEN** 合法请求已提取到 URL，显式 bilibili 未注册、已注册关闭或已启用但没有所属链接
- **THEN** 分别返回 PLATFORM_NOT_FOUND、PLATFORM_NOT_SUPPORTED 或 PLATFORM_MISMATCH，不进入 Bilibili 元数据请求，也不新增平台专用错误结构

### Requirement: 平台发现只返回已注册且如实描述的能力

系统 SHALL 提供 `GET /api/v1/platforms`，返回固定根字段 `requestId` 与 `platforms`，前者为本次请求关联字符串，后者为非 null 数组；每项完整包含字符串 `id`、字符串 `displayName`、布尔 `enabled` 及非 null 对象 `capabilities`。四类能力键 `video`、`imageAlbum`、`livePhoto`、`mixedAlbum` MUST 全部出现，状态 MUST 为 `unverified`、`supported` 或 `unsupported`；未来注册平台继续使用同一响应字段。该接口 MUST 仅读取内存注册信息，不请求上游、不依赖持久化存储、不发送或返回 Cookie。 本次 Bilibili 验收通过后，发现结果 SHALL 保留抖音并增加 bilibili，后者只将 video 标记 supported，imageAlbum、livePhoto、mixedAlbum 标记 unsupported；提案与未验证实现不能改变生产发现结果。

#### Scenario: 初始验证尚未通过
- **WHEN** 只注册抖音但匿名验证尚未完成
- **THEN** 发现结果只包含 `douyin`，未通过的能力标记为 `unverified`，适配器不因注册而自动启用解析

#### Scenario: 发现包含被关闭的注册平台
- **WHEN** 已注册适配器的运行配置为关闭
- **THEN** 发现结果保留该项并返回 `enabled: false`，不把配置启用状态当作上游实时健康状态

#### Scenario: 双平台发现如实描述媒体范围
- **WHEN** 抖音和经过真实验收的 Bilibili 均已生产注册启用
- **THEN** 返回两项及各自完整四个能力键；抖音仍为四项 supported，Bilibili 仅 video 为 supported，接口不发起上游健康检查
