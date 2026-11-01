## MODIFIED Requirements

### Requirement: 采用 Minimal API 提供共用静态响应契约

系统 MUST 使用 ASP.NET Core 10 Minimal API，通过路由分组及路由扩展方法提供统一与指定平台解析端点，不注册 MVC Controller。既有两个解析端点与平台发现端点 SHALL 统一要求有效的本服务 Bearer token；通过既有前置 HTTP 检查的请求 MUST 在正文绑定和业务服务执行前完成鉴权。两个解析端点 SHALL 接收仅含非空 `text` 的请求体，共用无 Cookie 解析服务、静态响应 DTO、类型化结果及源生成 JSON 序列化；HTTP 状态表达成功或失败，不新增通用 data 包装层或重复成功标记。

#### Scenario: 两个入口解析同一作品
- **WHEN** 调用方携带有效访问凭证，将合法抖音分享分别提交到 `/api/v1/resolve` 和 `/api/v1/platforms/douyin/resolve`
- **THEN** 两者返回相同字段与作品/媒体语义，不要求请求体重复声明平台，不创建下载任务或返回媒体正文

#### Scenario: 实际 HTTP 宿主验证
- **WHEN** 使用受控上游组件对启动后的 Minimal API 宿主发起有凭证和无凭证请求
- **THEN** 验证实际路由、鉴权、JSON 绑定、取消传播及成功/失败契约，不以仅直接调用服务方法代替端点验证

#### Scenario: 分 P 仍使用分享文本输入
- **WHEN** 调用方携带有效访问凭证，向既有两个解析入口提交带 p 的 Bilibili 分享 URL，且请求体只有 text
- **THEN** 通过既有静态 DTO 解析所选 P，不新增必需请求字段、平台专用路由或响应包装

#### Scenario: 不扩展请求体的业务字段
- **WHEN** 通过鉴权的调用方在 JSON 中额外提交 p、cid、qn、quality 或 platform 字段
- **THEN** 沿用静态绑定拒绝规则返回 INVALID_INPUT，不把这些字段作为 Bilibili 特例接收

#### Scenario: 授权后保持全部媒体结果约束
- **WHEN** 授权后的请求解析视频、普通图集、实况图集或混合图集
- **THEN** 保留连续 index、原始顺序、实况配对、完整最终 CDN URL 与逐候选必要下载头，不将本服务凭证加入任何结果字段

### Requirement: 失败响应包含固定可判断字段

错误根对象 MUST 包含 `requestId` 与 `error`；error SHALL 完整包含字符串 `code`、安全字符串 `message`、布尔 `retryable`、可空整数 `retryAfterSeconds`，并保留与错误语义一致的 HTTP 状态。等待秒数已知时 MUST 大于等于 0，未知或不可重试时为 null。错误 SHALL 不包含部分作品 items、敏感上游信息、本服务访问凭证或内部异常；程序判断依赖 code 而非 message。因凭证缺失、token 本体不满足 `32～256` 个 ASCII 字符限制、Bearer 格式错误或内容不匹配而产生的鉴权失败 MUST 使用 `401 / UNAUTHORIZED`、`WWW-Authenticate: Bearer`、`retryable: false` 与 `retryAfterSeconds: null`，复用相同请求标识及错误输出入口。

#### Scenario: 公开分享无法匿名解析
- **WHEN** 授权后的请求通过当前匿名路线无法取得作品的完整媒体
- **THEN** 返回 422、COOKIE_FREE_UNAVAILABLE、retryable false 和 retryAfterSeconds null，包含请求关联标识，不建立身份回退

#### Scenario: 上游暂时限流
- **WHEN** 授权后的请求遇到上游限流，被判定为暂时故障且等待秒数有可靠依据
- **THEN** 返回 503、UPSTREAM_UNAVAILABLE、retryable true 和对应等待秒数；等待未知时为 null，不推断为作品删除

#### Scenario: 访问凭证缺失或无效
- **WHEN** 对已匹配受保护端点的请求通过前置 HTTP 检查，但缺少凭证或凭证无效
- **THEN** 返回统一 401 未授权错误及 Bearer challenge，服务端生成的 requestId 与日志一致，不泄露输入或配置密钥

#### Scenario: token 长度越界使用既有错误结构
- **WHEN** 对已匹配受保护端点的请求到达鉴权层，token 本体分别为 31 或 257 个语法合法的 ASCII 字符
- **THEN** 两种请求均在摘要计算和正文绑定前返回统一 401、UNAUTHORIZED、Bearer challenge、不可重试标记和空等待时间，不回显凭证

#### Scenario: 有效凭证后的请求绑定错误
- **WHEN** 调用方提供有效凭证，但 JSON 结构、字段或必需 text 不合法
- **THEN** 保留原有 400 / INVALID_INPUT 和统一错误结构，不将业务输入错误变成未授权或服务内部错误
