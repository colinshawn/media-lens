## MODIFIED Requirements

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
