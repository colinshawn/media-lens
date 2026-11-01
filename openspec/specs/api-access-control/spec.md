# API 访问控制规范

## Purpose

定义 MediaLens API 的共享 Bearer token 访问控制、凭证格式与安全比较、统一未授权响应，以及凭证与严格无 Cookie 上游链路及输出之间的隔离边界。

## Requirements

### Requirement: 路由组统一要求有效访问凭证

系统 MUST 为 `/api/v1` 路由组中的已匹配端点统一校验共享 Bearer token，覆盖 `POST /api/v1/resolve`、`POST /api/v1/platforms/{platform}/resolve` 和 `GET /api/v1/platforms`。通过既有 Cookie 和请求大小等前置检查后，校验 SHALL 在 JSON 绑定、平台发现服务和平台解析之前执行；鉴权失败 MUST 不执行这些业务服务或任何上游请求。同一路由组后续新增的端点 MUST 继承鉴权要求。

#### Scenario: 两个解析入口拒绝无凭证调用
- **WHEN** 调用方通过前置 HTTP 检查但未提供凭证，并向任一解析入口提交分享文本
- **THEN** 系统返回 `401 / UNAUTHORIZED`，不绑定请求正文、不提取分享 URL、不访问上游

#### Scenario: 平台发现同样需要鉴权
- **WHEN** 调用方未提供有效凭证请求平台发现端点，且通过既有前置检查
- **THEN** 系统返回 `401 / UNAUTHORIZED`，不执行平台发现服务，不建立公开发现旁路

#### Scenario: 无凭证和非法 JSON 同时出现
- **WHEN** 解析请求通过前置检查但缺少凭证，正文为非法 JSON
- **THEN** 系统先返回统一未授权响应，不先返回 JSON 绑定错误

#### Scenario: 有凭证后继续执行既有业务
- **WHEN** 三个端点收到有效共享凭证且通过前置检查
- **THEN** 继续执行既有绑定、平台发现或解析逻辑，保留原来的成功与业务失败语义

#### Scenario: 路由不存在或 HTTP 方法不匹配
- **WHEN** 请求通过前置检查，但路径不存在或方法未匹配任何受保护端点
- **THEN** 系统保留既有 `404` 或 `405` 及统一错误结构，不把鉴权作为新端点注册或方法兜底

### Requirement: 只从单个合法 Bearer 请求头读取凭证

系统 SHALL 只接受单个符合 Bearer 语法的 `Authorization` 请求头，认证方案名不区分大小写，token 内容 MUST 区分大小写并按原值比较。缺失、空值、错误方案、重复头部、逗号合并的多个凭证、空白或控制字符导致的非法语法 MUST 被拒绝。系统 MUST 不从 URL 查询参数、JSON 正文、Cookie 或备用自定义头中提取访问凭证。

#### Scenario: 认证方案大小写不同
- **WHEN** 调用方使用不同大小写的 Bearer 方案名，并提供内容完全一致的合法 token
- **THEN** 系统接受凭证，继续处理请求

#### Scenario: token 内容大小写不同
- **WHEN** 调用方提供的 token 仅大小写与配置不同
- **THEN** 系统返回统一未授权响应，不将 token 规范化为相同值

#### Scenario: 重复或多重凭证
- **WHEN** 请求包含两个 Authorization 头，或单个头包含多个凭证
- **THEN** 系统拒绝请求，不任取首个或最后一个值通过校验

#### Scenario: 使用其他位置提供 token
- **WHEN** 调用方仅通过查询参数、JSON 字段或自定义头提交密钥，未提供合法 Authorization 头
- **THEN** 通过前置检查的受保护请求返回 `401 / UNAUTHORIZED`，不使用这些值认证

### Requirement: token 长度与字符在摘要计算前校验

系统 MUST 将实际传输的 token 本体限制为 `32～256` 个 ASCII 字符，包含两个边界，并继续满足 Bearer 字符语法。长度 SHALL 不包含 `Bearer ` 前缀，MUST 不按解码后字节数计算，不通过大小写转换、裁剪或截断使输入合规。通过 HTTP 服务器与既有前置检查并到达鉴权层的长度或字符非法请求 MUST 在编码、摘要计算、正文绑定及业务调用前返回统一 `401 / UNAUTHORIZED`。长度合法 SHALL 仅表示通过格式检查，内容仍 MUST 与配置完全匹配。

#### Scenario: 请求 token 位于两个合法边界
- **WHEN** 分别使用语法合法的 32 和 256 字符 ASCII 密钥启动服务，并以对应完全相同的 token 请求平台发现接口
- **THEN** 两种请求均通过鉴权并返回 200；计数仅包含 token 本体，不包含 Bearer 前缀

#### Scenario: 请求 token 位于边界之外
- **WHEN** 已正常启动的服务收到通过前置检查的请求，token 本体分别为语法合法的 31 和 257 个 ASCII 字符
- **THEN** 两种请求均在计算摘要前返回统一 401、Bearer challenge 和既有错误结构，不调用业务服务或上游，不将前缀计入长度，也不截断长 token

#### Scenario: 字符数合规但包含非 ASCII
- **WHEN** 到达鉴权层的 token 字符数在范围内，但包含非 ASCII 字符
- **THEN** 系统在计算摘要前返回统一未授权响应，不以 Unicode 字符数合规或编码转换后的结果放行

#### Scenario: 不裁剪或截断后接受凭证
- **WHEN** 到达鉴权层的 token 本体包含非法空白，或超过最大长度但其前缀与配置相同
- **THEN** 系统返回统一未授权响应，不对 token 本体执行裁剪或截断后比较，不在错误或日志中输出原值

#### Scenario: 边界长度合法但内容不匹配
- **WHEN** token 本体为 32 或 256 个语法合法的 ASCII 字符，但与当前配置内容不一致
- **THEN** 系统仍返回统一未授权响应，不因满足长度边界而免除内容比较

### Requirement: 鉴权失败使用统一错误响应

鉴权失败 MUST 返回 HTTP `401` 和 `WWW-Authenticate: Bearer`，正文 SHALL 为既有 `requestId + error` 结构。`error` MUST 完整包含 `code: UNAUTHORIZED`、不包含输入凭证的通用 `message`、`retryable: false` 和 `retryAfterSeconds: null`，不得包含作品项、密钥、摘要或内部异常。请求日志与错误正文 MUST 使用同一个服务端生成的请求标识。

#### Scenario: 密钥不匹配
- **WHEN** 收到格式合法但内容不匹配的 Bearer token
- **THEN** 系统返回上述状态、challenge 和固定错误字段，不将未授权映射为 `INVALID_INPUT` 或可自动重试的错误

#### Scenario: 多种鉴权失败原因
- **WHEN** 不同请求分别出现缺少凭证、方案错误或 token 内容不匹配
- **THEN** 系统使用相同的未授权错误码和通用消息，不披露配置值或比较细节

### Requirement: 使用固定时间方式比较静态凭证

系统 MUST 在启动时取得本次进程使用的配置快照，使用一致字节编码与固定时间比较完成 token 校验，不依赖普通字符串相等比较来处理凭证。更换部署密钥 SHALL 在重启或重建服务后生效，本期不提供热更新或新旧密钥同时有效的行为。

#### Scenario: 固定输入校验
- **WHEN** 使用固定合成密钥分别校验完全一致、不同大小写和不同内容的输入
- **THEN** 只有完全一致的输入通过；字节编码与摘要比较结果可重复验证

#### Scenario: 更换部署密钥
- **WHEN** 部署方更新密钥并重启服务
- **THEN** 只接受新配置的密钥，旧密钥被拒绝，不依赖进程自动生成或保存新密钥

### Requirement: 服务凭证与严格无 Cookie 上游隔离

本服务 token SHALL 仅用于 API 入站访问控制，不向共享解析服务、平台适配器或上游 HTTP 传输传递。所有平台元数据、短链与媒体跳转的每一跳 MUST 保持无 Cookie、无账号或游客身份构造，且不得携带本服务的 Authorization 或 token。入站 Cookie 与请求体的 cookie、session、identity 等上游身份字段 MUST 继续被既有输入规则拒绝；上游要求身份时仍明确失败。

#### Scenario: 授权后的请求发生重定向
- **WHEN** 有效调用方凭证通过校验，受控上游返回一跳或多跳重定向并尝试设置 Cookie
- **THEN** 每一跳出站请求均不包含本服务凭证或 Cookie，后续调用也不复用上游 Set-Cookie

#### Scenario: 有效 token 不豁免 Cookie 输入
- **WHEN** 调用方携带有效 token，同时提交 Cookie 请求头或请求体身份字段
- **THEN** 系统按现有规则返回 `400 / INVALID_INPUT`，不把有效 token 当作放行上游身份材料的依据

#### Scenario: 现有 Cookie 前置检查
- **WHEN** 请求缺少有效 token，同时包含 Cookie 请求头
- **THEN** 保留现有 Cookie 前置拒绝优先级，返回 `400 / INVALID_INPUT`，不访问上游

### Requirement: 凭证不进入输出与持久化

系统 MUST 不将访问 token、Authorization 内容或其摘要写入启动日志、请求日志、错误正文、媒体响应、下载候选头、文件或镜像。日志 SHALL 仅包含安全的配置项名称、事件和请求关联信息。授权成功后的媒体输出 MUST 保留既有最终 CDN URL、必要的独立下载头、图集顺序、混合图集和实况配对规则。

#### Scenario: 捕获成功与失败输出
- **WHEN** 使用合成密钥运行成功请求、错误 token 请求及启动配置失败场景，并捕获响应和日志
- **THEN** 捕获结果均不包含密钥、原始 Authorization 或其摘要；产生 HTTP 错误响应时仍包含请求标识，启动失败不开放 HTTP 接口

#### Scenario: 返回实况或混合图集
- **WHEN** 授权后的解析返回普通图片和实况混合的媒体资源
- **THEN** 项目顺序、连续 index、静态图与短片配对及最终媒体 URL 保持原有契约，候选 headers 仅含允许的下载头且不含鉴权凭证
