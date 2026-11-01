## Context

抖音变更已同步主 specs 并合并到 master。变更开始时 Minimal API、平台注册表和响应 DTO 已具备扩展入口，而 HTTP 请求头、目标范围及客户端注册仍面向抖音。本次已引入不可变平台 HTTP 配置和独立客户端装配，保留抖音默认绑定及现有契约。

本变更继承[平台路由规范](../../specs/platform-routing/spec.md)与[媒体结果契约](../../specs/media-resolution-api/spec.md)：输入仅含 text，服务返回最终 CDN URL 和每个候选的完整 headers；调用方负责实际下载和保存。当前 video 项只能表达同一逻辑视频的完整备用文件，不能表达需要组合的音视频轨道或连续分段。

2026-10-31 的依据和证据范围：

| 依据 | 可用于设计的事实 | 不能据此得出的结论 |
| --- | --- | --- |
| 当前 MediaLens 的 HTTP、平台注册表和结果验证代码 | 安全传输可复用；需要按平台配置请求头与目标范围；video 必须只有一个媒体项 | 不能认为新增域名后所有平台跳转均可放行 |
| 工作区参考项目 `Douyin_TikTok_Download_API-5.1.8` 的 `src/dtk/platforms/bilibili`、`src/dtk/signing/native/wbi.py` 和 `wbi_keys.py` | 有详情→选定 CID→播放地址流程、公开 WBI 参数与本地计算、单段 MP4 判定；参考参数包含 fnval=1、platform=html5 | 参考链路存在游客/身份基础设施，其成功不是 MediaLens 严格无身份解析的证据 |
| [yt-dlp 的 Bilibili 实现](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/bilibili.py) | 分别处理 DASH 轨道和 durl 分段，包含 WBI 及指纹参数处理 | 不把其指纹生成、登录或格式合并路径带入本项目，也不据此宣称匿名完整 MP4 一定可得 |

实施结论：正式适配器已完成六类真实匿名解析与独立完整 MP4 下载，画面/音轨及所选 P 均通过系统解码核对。选定入口、网络条件和最终 API/容器状态见[验证记录](../../../docs/validation/cookie-free-bilibili.md)；参考材料不替代这些实测。

### 既有接口与扩展点兼容性核对

| 现有接口或组件 | 本次接入方式 | 兼容约束 |
| --- | --- | --- |
| POST /api/v1/resolve | 由现有 ResolutionService.Select 选择已注册的 BilibiliAdapter | 保持 text 输入及按链接顺序选择，不增加平台分支端点 |
| POST /api/v1/platforms/{platform}/resolve | 既有模板直接接受小写 bilibili 标识 | 只增加适配器注册，不另建 /bilibili、/videos 或分 P 路由 |
| GET /api/v1/platforms | 从同一 PlatformRegistry 返回新平台及四个能力键 | 只读内存，不触发元数据、WBI 或健康检查 |
| IPlatformAdapter | 保留 Id、DisplayName、Capabilities、CanHandle(Uri)、ResolveAsync(Uri, CancellationToken) | 不为 Bilibili 扩展公开接口方法或把 URL 选择重复放入适配器 |
| ResolveRequest / ResolveResponse | 仍使用现有静态 DTO 与源生成 JSON | 不增加必需字段，不改变 null、数组、requestId/error 或 workType=video 单项语义 |
| ShareUrlExtractor | 复用通用 URL 提取，BV/AV/p 交给 Bilibili 链接规则 | 不为新平台复制提取器，抖音标点、顺序和查询优先级保持 |
| Program 中的上游客户端注册 | 按平台装配并由容器持有客户端 | 保留抖音默认绑定，Bilibili 不能复用抖音固定 Referer/目标策略 |

核对依据为当前[路由注册](../../../src/MediaLens.Api/MediaResolutionEndpoints.cs)、[解析分发](../../../src/MediaLens.Core/Resolution/ResolutionService.cs)、[适配器接口](../../../src/MediaLens.Core/Platforms/IPlatformAdapter.cs)、[契约](../../../src/MediaLens.Core/Contracts/MediaContracts.cs)和[已有第二平台回归](../../../tests/MediaLens.Tests/ApiTests.cs)。新增平台标识属于既有开放字符串的扩展，继续使用 v1；本次不要求抖音调用方改变请求或响应读取方式。

## Goals / Non-Goals

**Goals:**

- 支持普通公开 UGC 视频的 BV、AV 完整链接、常见分享文本及 b23.tv 短链。
- 将一个请求限定为一个选定分 P，准确匹配 BV/AID、page 与 CID，保留来源归属。
- 在不接收/发送 Cookie、不构造账号、游客或设备身份的情况下，使用必要的常规头和可验证的本地 WBI。
- 返回能够独立下载且不丢失源作品音频的完整 MP4 候选，保持既有 v1 契约。
- 抽取平台请求配置时保持抖音四类媒体、请求边界、错误响应和并发隔离；提供真实解析与独立客户端下载证据。

**Non-Goals:**

- Cookie、SESSDATA、bili_jct、buvid3/buvid4、设备指纹、浏览器会话及第三方 Token 服务。
- DASH 音视频合并、FLV 拼接、HLS 下载、媒体代理、转码、FFmpeg、媒体落盘或数据库。
- 最高画质、登录/会员/付费/充电专属权益、试看下载及地区限制解除。
- 整稿全部分 P、作者/合集批量采集、互动视频、番剧影视、课程、直播、动态/专栏图文或实况解析。

## Decisions

### 1. 以普通 UGC 的单个分 P 为解析单位

平台选择继续使用现有 ResolutionService.Select；CanHandle(Uri) 只做无副作用的平台归属识别，覆盖 Bilibili 已识别主机与 b23.tv，不以视频路径、BV/AV 或 p 是否有效作为平台归属条件。归属集合与网络允许集合分开：识别属于 Bilibili 的主页、直播等链接只用于本地返回 UNSUPPORTED_RESOURCE，不代表允许请求这些主机。CanHandle 不访问网络、不抢占其他平台链接，也不在当前链接失败后选择后续链接作为回退。

在归属确定后，接受经允许主机上的 `/video/BV…`、`/video/av…` 及能展开到这些路径的 b23.tv 短链。BV 标识保留大小写，不对其主体做大小写转换；AV/AID 和 CID 在协议模型中保持字符串，不通过浮点数中转。AV 输入优先由详情响应取得规范 BV，不额外移植不必要的 BV/AV 转换算法。

`p` 是从 1 开始的正整数；缺省为第 1 P。零、负数、溢出、小数、空值及重复 p 参数返回 INVALID_INPUT。取得详情的 pages 后验证页码范围，越界同样失败，不悄悄回到第 1 P。若选中页不存在有效 CID，返回协议变化错误。只使用 pages 中与页码对应的 CID，不采用输入查询参数提供的 cid。

p 只来自 text 中分享 URL 的查询参数，不作为路由段或 ResolveRequest 的额外 JSON 字段。响应 workId 是整稿规范 BV；webUrl 明确包含 `?p=<所选页码>`，用于标明本次解析的分 P。同稿不同 P 的 workId 可以相同，所选资源由 webUrl 中的 p 区分，不能只以 workId 区分不同 P 的本地文件。items 仍只有 index=1 的 video 项，不能把其他分 P 放入 videoSources。无 p 选择第 1 P 是既定单资源语义，不代表已经取得整稿全部分 P；该限制在 README 中明确说明。

主页、合集、番剧、直播、动态及互动内容返回 UNSUPPORTED_RESOURCE；即便其页面包含 BV、推荐视频或重定向，也不展开批量或跳到不同资源类型。

替代方案：把所有分 P 都塞入 items 会改变 workType=video 仅一个媒体项的约束；新增批量路径和响应类型应另立提案，本次不采用。

### 2. 先验证最小匿名入口，再固定请求顺序

实测选定第一方 `/x/web-interface/view` 与 `/x/player/playurl` 的最小未签名链路；必要时有界回退到对应 WBI 入口。当前普通样本不需要 Cookie、指纹或页面脚本，不运行接口全排列。短链的公开页面只用于取得最终作品链接，不以推荐数据替代详情身份校验。

播放请求以 BV、已匹配 CID 和固定格式参数为输入。实测采用 fnval=1、fnver=0、platform=html5、fourk=0、high_quality=1、qn=80；样本响应实际 quality=64，不保证请求画质等于实际返回画质，不在本次 API 增加画质字段。既不主动请求试看，也不生成 dm_img 等指纹字段。

元数据必须与请求的 BV/AID 一致；播放数据必须来自所选 CID 的受控请求。页面中的推荐内容、其他稿件或其他分 P 不能充当失败回退。若数据表明内容是付费、充电专属、互动/番剧跳转或预览，拒绝对应范围，不输出截短内容。

替代方案：直接照搬参考项目的游客初始化、指纹或第三方解析会改变严格无身份边界；公开页面可打开也不能替代匿名媒体验证。

### 3. WBI 是可验证的本地计算，不是账号身份

若选中的匿名入口需要 WBI，通过同一受控无 Cookie 客户端请求 `api.bilibili.com/x/web-interface/nav`，仅读取 data.wbi_img 中两个公开资源名的键材料。不下载这些图片、不请求 finger/spi、不复用 nav 返回的 Set-Cookie。nav 的业务 code=-101 不单独等同于键读取失败；必须验证结构及键长度，登录状态不参与签名。

实现纯 .NET 的本地签名组件，明确版本及 mixin 排列常量；以 TimeProvider 提供 Unix 秒。签名输入限定为业务参数与 wts，按固定规则排序、删除协议规定的字符、UTF-8 编码并计算 w_rid。编码后的 Query 既用于计算也原样用于实际请求，空间用 %20，斜杠及保留字符按协议转义；不能再交给另一套表单编码器重新序列化。

固定合成键、时间及 Query 建立独立参考向量，覆盖空格、中文、斜杠、标点、参数顺序、空值和大整数；分别验证规范化字符串、w_rid 与实际抓到的请求目标字节。参考实现的编码细节不是天然正确的保证，向量必须与所选协议版本交叉核对。

公开键只作进程内短期缓存，首版复用窗口取 30 分钟，作为本地性能策略而非上游有效期承诺；并发刷新合并且等待可取消。仅在有明确签名失效依据时最多重新取键并重试一次，所有步骤计入同一个解析时限。没有可用键或刷新失败时明确失败，不引入永久存储、不缓存用户身份、不进行无限重试。

替代方案：依赖远程签名服务会引入黑盒；将公开键当作永久常量会掩盖轮换；为通过风控而生成游客/指纹参数均不采用。

### 4. 成功候选必须是覆盖所选分 P 的完整文件

首期接受上游明确报告 MP4，且 durl 恰好只有一个元素的 progressive 文件；主 URL 与 backup_url 是同一完整资源的备用候选。继续确认最终 CDN 与媒体响应头，只返回经过确认的可用候选，首版每个必需列表最多 2 个。

仅有 DASH video/audio、多个连续 durl、FLV、HLS 或其他必须组合的结构时返回 UNSUPPORTED_RESOURCE。不能把分离的 audio 放入 videoSources，也不能把多个 durl 当成互为备用，或只取第一段当作完整视频。即便上游同时提供 DASH，只要另外存在经过确认的完整 MP4，仍可选择该完整来源。

检查预览/权益标记和播放时长与所选分 P 时长的相容性；明显截断、试看或无法排除已标记预览的结果不得返回成功。若原作品有音频，成功候选必须保留它；原始静音作品不要求凭空产生音轨。服务端依据明确格式及协议证据筛选，独立验证客户端负责完整下载后核对音轨、画面和时长，服务端不读取媒体正文。

元数据归属于实际候选：durationMs 使用该分 P / 播放结果，不取整稿总时长；不能把详情中的原始尺寸套给被降画质的 MP4。候选尺寸未知则 null；大小以可核对的候选元数据或媒体响应头为依据。文件名包含 BV 和 p，避免不同分 P 下载覆盖。只在协议已确认某过期参数的单位和语义后解析有效期，否则仍为 null；完整查询参数始终保留。

替代方案：新增 tracks/downloadPlan 或引入服务端合并会扩大契约与运行依赖。本次先交付符合既有下载方式的范围；若真实匿名路线只提供分离流，则保持未启用并另行设计，不把失败当作本提案验收完成。

### 5. HTTP 请求配置和目标范围按平台隔离

在现有 CookieFreeHttpClient / PublicHttpTargetPolicy 中引入最小的不可变平台配置，容纳请求头、精确平台/API 主机、媒体根及最终 CDN 判定。API 在 Program 的组合入口通过显式工厂或带平台标识的依赖注入绑定，为适配器装配各自受控客户端。客户端及其 HttpMessageHandler 由容器统一持有和释放；不能把两次非限定 IUpstreamClient 注册当作平台分发，也不能在适配器构造中创建未被容器管理的长生命周期客户端。保留抖音的默认 IUpstreamClient 绑定及现有构造/调用入口，Bilibili 使用独立配置，不要求改变 IPlatformAdapter 或响应 DTO。禁止在共享 DefaultRequestHeaders 上按请求切换 Referer/Origin，也不把两个平台所有域名合成任何请求都可访问的大允许列表。

Bilibili 元数据候选主机为 www.bilibili.com、m.bilibili.com、api.bilibili.com，短链主机为 b23.tv。媒体根先调查 bilivideo.com / bilivideo.cn；实施只加入经真实响应确认的必要范围，不接受任意 akamaized.net、任意云 CDN 根、IP 字面量、非默认端口或接口传回的新域名。未知 CDN 可以排除该候选，必需资源均无可用来源则明确失败；不得通过改写 upos 主机或重造 URL 获得未返回的候选。

所有元数据、nav、短链、播放和 CDN 跳转都不发送 Cookie、不构造身份。沿用 2 MiB 解压后元数据上限、最多 5 次重定向、媒体仅响应头、MaxResponseDrainSize=0、总超时/取消及完整公网 IP 分类。整组 DNS 结果都必须通过，连接时使用刚验证的地址并保留 TLS 主机检查；TUN Fake-IP 不因新增平台获得例外。

短链重定向先在本地分类 Location：若属于另一已注册平台，在发起跨平台请求前转换为既有 400 PLATFORM_MISMATCH；若明确属于本平台未支持资源，则返回 422 UNSUPPORTED_RESOURCE。内部必须保留可区分的失败原因，由所选适配器或解析层映射；不能只抛普通 UpstreamRequestException，因为现有 ResolutionService 会把它映射为 502 UPSTREAM_CHANGED。未知/越界目标仍按安全策略拒绝，不借另一个平台注册而放行。

跳转归属使用与适配器同源的纯 URI 规则，规则集合在组合入口装配，不让 HTTP 客户端反向依赖 PlatformRegistry 产生“注册表→适配器→客户端→注册表”构造循环。该集合仅识别归属，不授予跨平台访问权限，实际连接始终受当前平台的目标策略约束。

每个返回候选包含其实际下载所需的完整 User-Agent、Referer、Accept 和经确认必要的 Origin，不包含 Cookie、Authorization 等。下载验证使用这些头；浏览器地址栏裸 URL 的 403 不作为带完整参数下载失败的证据。

### 6. 沿用 API 字段与平台发现，按证据启用

两个解析入口保持请求体 `{ "text": "公开分享文本" }`，新增平台不新增 HTTP 路由代码。p、cid、qn、quality、platform 等均不新增为 JSON 请求字段；额外字段继续由既有静态绑定规则拒绝为 400 INVALID_INPUT。指定平台只来自既有路由参数，其有效值在本次注册 bilibili，不是可拼接任意上游地址的输入，也不为它增加大小写别名。

沿用当前输入校验与平台选择顺序：请求已成功绑定并提取到候选 URL 后，显式平台未注册为 404 PLATFORM_NOT_FOUND，已注册关闭为 422 PLATFORM_NOT_SUPPORTED，再校验首个所属链接并在不匹配时返回 400 PLATFORM_MISMATCH；不能让 Bilibili 的路径/参数校验提前覆盖这些现有错误。统一入口继续按文本候选顺序选择已启用的归属适配器，不由注册顺序或新平台的路径有效性改变选择。

成功映射如下，其他字段、UTC/null 与错误形状继续遵循主规格：

| 字段 | Bilibili 语义 |
| --- | --- |
| platform | bilibili |
| workId | 规范 BV，AV 输入经可信详情响应规范化 |
| workType / items | video / 仅一个 index=1、type=video 的项 |
| webUrl | 规范公开视频页，明确保留所选 p |
| description | 当前可信详情中的 desc，缺少则 null |
| imageSources | []，封面不作为图集资源 |
| videoSources | 所选 P 的独立完整 MP4 备用来源，不是分 P 列表或轨道列表 |

生产注册和启用必须晚于匿名元数据、完整媒体、独立客户端画面/音频/时长及 API 验证通过。实现期间在受控测试中分别验证关闭状态、具有合成支持能力的适配器及双平台成功路由；这些测试不构成真实支持声明，没有可行性证据时生产仍仅注册抖音。验证通过后 Bilibili 的 video 标记 supported，imageAlbum、livePhoto、mixedAlbum 标记 unsupported，保留四个键；抖音四项保持 supported。

错误继续使用既有稳定代码：

| 情况 | HTTP / code |
| --- | --- |
| 无效 BV/AV 形式、无效或越界 p | 400 / INVALID_INPUT |
| 显式平台不匹配，包括短链跳向另一已注册平台 | 400 / PLATFORM_MISMATCH |
| 本次未实现的内容、分离流、多段或非 MP4 | 422 / UNSUPPORTED_RESOURCE |
| 明确需要会话/身份，或匿名路线无可用公开数据 | 422 / COOKIE_FREE_UNAVAILABLE |
| 已识别完整 MP4，但必需最终来源全部不可用 | 422 / MEDIA_INCOMPLETE；明确暂时故障则 503 |
| 有明确作品删除/不可访问证据 | 404 / CONTENT_UNAVAILABLE |
| 不可识别结构、BV/AID 不匹配或页缺少 CID | 502 / UPSTREAM_CHANGED |
| 网络、限流或有明确暂时拒绝依据 | 503 / UPSTREAM_UNAVAILABLE |
| 任意步骤耗尽同一解析总时限 | 504 / RESOLUTION_TIMEOUT |

普通 HTTP 403/412、未知业务码或 CDN 403 不自动说明作品删除、必须登录或签名已失效；根据入口和明确证据分类。无依据的未知结构归协议变化，暂时风控不启动会话回退；不增加客户端必须认识的新错误枚举。

### 7. 分开验证传输、协议、平台可行性和调用方下载

离线使用合成链接/标识、固定 WBI 向量和 TCP 上游验证签名、每跳 Cookie 缺席、Set-Cookie 不复用、分 P/CID、流结构拒绝、备用来源及脱敏。真实 Kestrel 验证统一/指定入口、双平台选择、平台发现、错误形状和并发请求头隔离；现有抖音 670 项回归继续通过。

真实样本覆盖 BV、AV、b23.tv、未指定 p 和指定非首 P。由仓库外的独立客户端用正式 API 返回的 url + headers 完整下载，检查字节长度、可解码视频、源有音频时的音轨，以及所选 P 的时长。只验证 response status=200、video/mp4 或存在 URL 不算通过。

验证工具增加显式 `--platform bilibili` 的 adapter 模式，默认 douyin 和原输入格式保持。直接验证适配器可在生产启用前完成；完整媒体证据齐备后再装配生产注册表，并对正式 API 与最终 Linux 容器验证。新平台检查不得把探测模式的可访问性当成已部署支持。

真实输入、BV/AID/CID、完整重定向/CDN 地址、签名查询和原始响应仅存在仓库外。仓库记录日期、入口、主机、状态、Cookie 缺席、所选 P 统计及验证结果；合成夹具不使用公开真实视频链接。不重复使用抖音样本来宣称 Bilibili 已支持。

## Risks / Trade-offs

- [匿名只返回 DASH 或受限预览] → 以完整 MP4 与音视频实测为启用门槛；明确失败，保留契约，必要时另立流组合提案。
- [签名、公开键或风控变化] → 纯函数向量、可取消的短期公开键缓存及有依据的一次刷新；不扩展到身份或指纹。
- [分 P 错配或详情总时长误用] → BV/AID、pages 与 CID 联合校验；规范 URL 带 p，按所选 P 提供元数据。
- [平台头部串用或允许列表扩大] → 不可变平台配置及双平台并发测试，每跳使用当前平台的目标规则。
- [CDN 返回未知域名或调用方网络拒绝] → 有界排除并返回明确失败；保留完整 URL 与下载头，不重写主机或承诺永久有效。
- [抽取公共组件影响既有抖音] → 默认配置兼容，保留四类回归及已有公网 IP 边界用例；独立检查响应字段与平台发现。

## Migration Plan

1. 固定候选入口、WBI 向量与真实匿名/完整 MP4 的证据要求；先完成受控验证，不修改生产支持声明。
2. 加入平台 HTTP 配置、Bilibili 解析/签名组件和验证工具入口，执行离线与双平台 API 验证。
3. 真实适配器及独立下载通过后，在同一生产注册表加入并启用 Bilibili，只发布 video 支持；完成正式 API 和 Linux 容器验证。
4. 同步中英文 README 与脱敏证据；不需要数据迁移或新增服务。若后续退化，关闭 Bilibili 解析或撤回该部署，保留抖音；不自动启用 Cookie 回退。
5. 所有验收通过后再同步主 specs 并归档本变更；未通过完整匿名路线时保留任务未完成。

## 后续观察与开放问题

- 当前选定的第一方未签名详情与播放入口已通过六类匿名和完整文件验证；后续需观察协议、风控或地域变化，退化时按既有明确错误失败，不加入身份回退。
- 已确认的 CDN 范围为 bilivideo.com；候选尺寸和过期语义仍可能缺失，未知值保持 null，新域名不会自动放行。
- 普通音频视频、约 9 分钟长视频和两个不同 P 已分别验收，证据见[验证记录](../../../docs/validation/cookie-free-bilibili.md)；样本通过不代表覆盖全部视频。
- 若将来需要 DASH、全部分 P 或图文，应另立提案表达组合下载及完整性，不改变当前备用列表语义。
