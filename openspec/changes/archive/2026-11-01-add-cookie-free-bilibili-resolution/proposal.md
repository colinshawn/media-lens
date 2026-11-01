## Why

MediaLens 的抖音严格无 Cookie 解析已合并到 master，需要将已有多平台接口和注册表扩展到 Bilibili，让调用方继续通过分享文本取得可自行下载的视频资源。Bilibili 的分 P、WBI 签名和音视频分离与抖音不同，必须先确认匿名可行性和完整文件边界，避免把无声视频轨道、试看或部分分段误报为完整下载结果。

## What Changes

- 新增 `bilibili` 普通公开视频解析：从分享文本识别 BV/AV 完整链接及 `b23.tv` 短链，解析规范 BV 标识、选定分 P 与对应 CID；没有 `p` 时选择第 1 P，有明确 `p` 时只解析该 P，不批量解析整稿。
- 沿用 `POST /api/v1/resolve`、`POST /api/v1/platforms/{platform}/resolve` 和平台发现接口；指定平台路径为 `/api/v1/platforms/bilibili/resolve`，请求体仍只有 `text`。复用既有 URL 提取、ResolutionService 选择及 IPlatformAdapter，不复制路由；分 P 只从分享 URL 的 p 选择，不新增 JSON 字段或路由段。
- 复用既有成功/失败 DTO。成功只返回选定分 P 的一个 video 项；每个 `videoSources` 候选必须是可独立下载的完整 MP4，并保留原有音频、完整 URL、独立下载头和可确认的技术信息。
- 先验证公开页面或第一方详情/播放接口的严格匿名行为；若需要 WBI，只在本地计算签名并从匿名 nav 响应读取公开参数，不生成或复用 Cookie、游客标识、设备指纹及浏览器会话。
- 将共享 HTTP 组件中的抖音请求头和域名规则改为按平台隔离的不可变配置，保留逐跳目标校验、完整 IPv4/IPv6 公网规则、固定连接 IP、响应读取限制和总时限，防止并发请求串用平台头部或越界跳转。
- 仅接受可确认完整的单段 progressive MP4；DASH 音视频分离、连续多段、FLV/HLS、试看及本次未实现的内容类型明确失败，不增加音轨、合并计划或任务接口来改变现有 v1 语义。
- Bilibili 的真实匿名验证通过后才生产注册和启用，届时 `video` 为 `supported`，`imageAlbum`、`livePhoto`、`mixedAlbum` 为 `unsupported`；提案阶段不改变当前仅抖音的运行行为。
- 补充合成协议用例、双平台真实 HTTP 回归、仓库外真实解析/下载验证和中英文使用说明；真实分享文本、作品 ID、CDN 地址及原始响应继续禁止进入代码库。

非目标：服务端视频下载/代理、音视频合并、转码、数据库或 Redis、Cookie/登录支持、最高画质承诺、全部分 P 批量解析、番剧影视、课程、直播、互动视频、动态/专栏图文及相册集成。

## Capabilities

### New Capabilities

- `cookie-free-bilibili-resolution`：定义 Bilibili 匿名元数据与 WBI、分 P/CID 选择、完整 MP4 判定、CDN 目标与请求头隔离、错误行为和真实验证门槛。

### Modified Capabilities

- `share-text-resolution`：把仅抖音的链接与作品规则扩展为按平台注册规则，保留原有行为并增加 BV/AV、短链和分 P 输入约束。
- `platform-routing`：在 Bilibili 验证通过后加入生产注册表，明确双平台路由、发现结果、启用门槛及逐类型能力状态。
- `media-resolution-api`：重申仅 text 的共用静态请求与既有错误契约，明确视频候选是覆盖所选逻辑视频的独立完整文件，禁止将分离音轨或连续分段当作互相备用的来源，保留既有字段和 v1 路径。

## Impact

- 新增 `src/MediaLens.Core/Platforms/Bilibili` 适配器、链接/元数据解析和必要的本地 WBI 组件；调整共享 HTTP 配置、API 依赖注入与验证工具的平台分发。
- 沿用 .NET 10 Minimal API、静态 DTO、源生成 JSON 和现有容器；不引入第三方解析服务、浏览器、FFmpeg、媒体卷或账号配置。
- 抖音已发布的四类媒体行为、默认请求配置和现有 670 项回归必须保持；新增平台不删除或重命名响应字段、不改变视频项数量语义。
- 本地参考实现和公开源代码只提供线索；实施已通过六类真实严格匿名解析、完整音视频下载、正式 API 与 Linux 容器验收。选定入口、实际匿名画质、CDN 范围及网络限制见[验证记录](../../../../docs/validation/cookie-free-bilibili.md)，该记录不承诺全部公开视频或未来持续可用。
