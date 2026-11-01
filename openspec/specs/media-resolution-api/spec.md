# 媒体解析 API 规范

## Purpose

定义 MediaLens Minimal API 的共用静态响应契约，统一作品、媒体项和独立下载候选的信息，保留图集顺序及实况配对，并明确请求关联、可空值、过期信息与错误语义。

## Requirements

### Requirement: 采用 Minimal API 提供共用静态响应契约

系统 MUST 使用 ASP.NET Core 10 Minimal API，通过路由分组及路由扩展方法提供统一与指定平台解析端点，不注册 MVC Controller。两者 SHALL 接收仅含非空 `text` 的请求体，共用无 Cookie 解析服务、静态响应 DTO、类型化结果及源生成 JSON 序列化；HTTP 状态表达成功或失败，不新增通用 data 包装层或重复成功标记。

#### Scenario: 两个入口解析同一作品
- **WHEN** 合法抖音分享分别提交到 `/api/v1/resolve` 和 `/api/v1/platforms/douyin/resolve`
- **THEN** 两者返回相同字段与作品/媒体语义，不要求请求体重复声明平台，不创建下载任务或返回媒体正文

#### Scenario: 实际 HTTP 宿主验证
- **WHEN** 使用受控上游组件对启动后的 Minimal API 宿主发起请求
- **THEN** 验证实际路由、JSON 绑定、取消传播及成功/失败契约，不以仅直接调用服务方法代替端点验证

#### Scenario: 分 P 仍使用分享文本输入
- **WHEN** 调用方向既有两个解析入口提交带 p 的 Bilibili 分享 URL，且请求体只有 text
- **THEN** 通过既有静态 DTO 解析所选 P，不新增必需请求字段、平台专用路由或响应包装

#### Scenario: 不扩展请求体的业务字段
- **WHEN** 调用方在 JSON 中额外提交 p、cid、qn、quality 或 platform 字段
- **THEN** 沿用静态绑定拒绝规则返回 INVALID_INPUT，不把这些字段作为 Bilibili 特例接收

### Requirement: 解析成功包含固定的作品字段

成功响应 SHALL 为 `200`，根字段 MUST 完整包含 `requestId`、`platform`、`workId`、`workType`、`webUrl`、`description`、`items`、`resolvedAt`、`expiresAt`。平台与所选适配器一致，`workId` 为字符串，`webUrl` 为该作品的规范公开页面链接；描述仅来自当前取得的元数据，不存在时 MUST 显式为 `null`。`items` MUST 非空，不返回原始上游响应或持久化记录 ID。

#### Scenario: 上游缺少描述
- **WHEN** 已取得完整媒体，但上游没有作品描述
- **THEN** 成功结果保留 `description: null`，不为补齐展示字段额外抓取信息，其他固定字段仍全部出现

#### Scenario: 平台内作品标识
- **WHEN** 不同平台出现相同的作品 ID 字面值
- **THEN** 响应分别保留各自的 `platform` 和字符串 `workId`，以两者组合标识作品

### Requirement: 媒体项类型与来源列表保持一致

每个媒体项 SHALL 固定包含从 1 开始且与数组顺序连续一致的整数 `index`、字符串 `type`、数组 `imageSources`、数组 `videoSources`。`type` MUST 为 `video`、`image` 或 `livePhoto`：视频只含非空视频候选，图片只含非空图片候选，实况两者均非空；不适用的列表 MUST 为 `[]`，不得为 `null`。`workType: video` MUST 仅有一个 video 项；album MUST 由一个或多个 image/livePhoto 项组成。

#### Scenario: 普通视频
- **WHEN** 作品为视频
- **THEN** `workType` 为 `video`，仅有 `index: 1` 的 video 项，`imageSources` 为空、`videoSources` 非空，不把封面或音乐代替视频

#### Scenario: 单张图片或普通图集
- **WHEN** 作品包含一张或多张普通图片
- **THEN** `workType` 为 `album`，按原顺序返回 image 项，每项图片候选非空且视频候选为空

### Requirement: 保持混合图集顺序及实况配对完整性

系统 MUST 保持图集原始项顺序。每张实况的静态图和动态片段 SHALL 位于同一项的 `imageSources` 与 `videoSources`，不得独立重新编号。声明为实况但缺少任一部分或其他必需媒体项时 MUST 返回 `MEDIA_INCOMPLETE`，不能删除缺失项或降级后返回完整成功。

#### Scenario: 普通图片与实况混排
- **WHEN** 源顺序为普通图片、实况图片、普通图片
- **THEN** 返回连续 index 1、2、3，第二项同时含对应图片和短片来源，不跨项合并重复 URL

#### Scenario: 实况片段缺失
- **WHEN** 上游声明实况但静态图或动态短片无法取得
- **THEN** API 返回 `MEDIA_INCOMPLETE`，不返回部分作品 items 或成功状态

### Requirement: 每个候选包含完整独立的下载信息

`imageSources` 与 `videoSources` SHALL 使用同一静态候选契约，每项 MUST 包含 `url`、`headers`、`mimeType`、`fileName`、`width`、`height`、`durationMs`、`sizeBytes`、`expiresAt`。`url` 为完整最终 CDN 地址；`headers` 为该候选所需的完整字符串字典，无继承或覆盖层级，不需要头部时 MUST 为 `{}`。其余字段按契约允许 `null`。视频候选 MUST 是覆盖请求所选逻辑视频、可独立下载的完整文件并保留其原有音频；videoSources 中的多项表示备用来源，不能表示需要共同下载和合并的轨道、连续分段或不同分 P。Bilibili 首期仅接受经确认的完整单段 MP4，其他结构明确失败，字段、媒体项数量语义与 v1 路径不变。

#### Scenario: 同一视频具有不同候选
- **WHEN** 同一逻辑视频有不同格式、尺寸或码率的可用来源
- **THEN** 按优先级返回候选，每个候选携带自身元数据，调用方择一下载而非下载全部候选

#### Scenario: 图片与短片需要不同请求头
- **WHEN** 实况图片与短片，或两个备用候选所需常规头部不同
- **THEN** 每个候选分别返回自己的完整 headers，不将其他候选的头部、格式或大小套用到该候选

#### Scenario: 需要组合的音视频轨道
- **WHEN** Bilibili 上游仅提供 DASH video 与 audio 两个独立轨道
- **THEN** 当前 v1 返回 UNSUPPORTED_RESOURCE，不把两者放入 videoSources 让调用方择一，也不新增未约定的合并字段

#### Scenario: 连续分段或不同分 P
- **WHEN** 上游返回多个连续 durl，或一个作品包含多个分 P
- **THEN** 连续分段不作为完整候选成功；多 P 仅解析输入选定的 P，其它 P 不充当该视频的备用来源

#### Scenario: 完整 MP4 下载与响应兼容
- **WHEN** Bilibili 返回覆盖所选 P 的完整 MP4 及独立备用来源
- **THEN** 使用既有一个 video 项及完整候选字段，imageSources 为 []，未知技术信息为 null，每个来源保留自己的完整下载头

### Requirement: 候选头部和技术元数据如实且安全

候选头部 MUST 从确认过的常规媒体请求头允许列表产生，不包含 Cookie、Set-Cookie、Authorization、Proxy-Authorization 或账号凭据。未知 MIME、建议文件名、像素尺寸、时长和大小 SHALL 为 `null`，不得用空串或零值伪装已知。尺寸、视频时长及大小已知时 MUST 为正整数；时长单位为毫秒、大小单位为字节，图片 `durationMs` MUST 为 `null`。建议文件名 MUST 不含目录且后缀有可靠依据；不为补齐可空元数据下载媒体正文。

#### Scenario: 技术信息未知
- **WHEN** 取得了必需的最终媒体地址，但无法确认尺寸、大小、格式或时长
- **THEN** 对应键显式为 null，仍可成功；不会根据 URL 后缀盲猜 MIME 或读取整个媒体文件补齐字段

#### Scenario: 候选携带敏感上游头部
- **WHEN** 上游信息包含 Cookie、认证信息或整组响应头
- **THEN** 返回候选只保留已确认的常规下载头，不回显这些敏感字段

### Requirement: 如实表达逐候选与汇总过期时间

系统 SHALL 返回 UTC ISO 8601 的 `resolvedAt` 与逐候选 `expiresAt`；未知候选过期时间 MUST 为 `null`。顶层 `expiresAt` 只有在所有返回候选均有可确认的过期依据时才取最早值，否则 MUST 为 `null`。不得猜测固定 TTL 或更改 URL 查询参数；已明确过期候选不得返回，必需资源候选全部不可用时 MUST 返回 `MEDIA_INCOMPLETE`。

#### Scenario: 部分候选有效期未知
- **WHEN** 一个候选有效期已知，另一个候选有效期未知
- **THEN** 分别返回各自 expiresAt，顶层 expiresAt 为 null，不将已知值套用到未知候选

#### Scenario: 所有候选过期时间已知
- **WHEN** 所有返回候选均有可靠且尚未过期的时间依据
- **THEN** 顶层 expiresAt 为最早候选过期时间，完整 URL 及查询参数保留不变

### Requirement: 序列化与请求关联字段一致

三个接口的成功与应用产生的错误响应 MUST 包含服务端为本次请求生成的非空 `requestId`，不依赖存储记录或原样回显任意入站值。固定字段 SHALL 全部输出，字段名 camelCase；可空值显式 null、数组不为 null、字典不为 null，枚举值使用文档规定的大小写敏感字符串。UTC 时间 MUST 使用带 Z 的 ISO 8601 格式。

#### Scenario: 空列表和未知字段值
- **WHEN** 视频项没有图片，某候选不需要请求头且大小未知
- **THEN** 分别返回 imageSources 空数组、headers 空对象和 sizeBytes null，不省略这些键

#### Scenario: 同一请求发生错误
- **WHEN** 请求在绑定、平台解析、中间件或异常处理阶段失败
- **THEN** JSON 错误响应与该请求日志使用相同 requestId，不转为其他框架默认错误形状

### Requirement: 失败响应包含固定可判断字段

错误根对象 MUST 包含 `requestId` 与 `error`；error SHALL 完整包含字符串 `code`、安全字符串 `message`、布尔 `retryable`、可空整数 `retryAfterSeconds`，并保留与错误语义一致的 HTTP 状态。等待秒数已知时 MUST 大于等于 0，未知或不可重试时为 null。错误 SHALL 不包含部分作品 items、敏感上游信息或内部异常；程序判断依赖 code 而非 message。

#### Scenario: 公开分享无法匿名解析
- **WHEN** 当前匿名路线无法取得作品的完整媒体
- **THEN** 返回 422、COOKIE_FREE_UNAVAILABLE、retryable false 和 retryAfterSeconds null，包含请求关联标识，不建立身份回退

#### Scenario: 上游暂时限流
- **WHEN** 上游限流被判定为暂时故障且等待秒数有可靠依据
- **THEN** 返回 503、UPSTREAM_UNAVAILABLE、retryable true 和对应等待秒数；等待未知时为 null，不推断为作品删除
