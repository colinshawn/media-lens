## Context

MediaLens 当前只有 README、OpenSpec 配置、共享 .NET 10 构建配置和空解决方案，没有应用项目及既有 API。用户需要输入公开分享文本，取得作品 ID、视频/图片/实况类型与最终 CDN 地址，由调用方自行下载并保存。

已有依据：参考项目展示了分享文本提取、短链识别、作品详情字段映射及图片/短片配对方法。第一个参考项目通过身份池和浏览器游客 Cookie 获取详情，第二个参考项目会补充 `msToken` Cookie；它们不能作为严格无 Cookie 成功的证据。

验证结论（2026-10-31）：第一方匿名详情入口 `api.amemv.com` 在仅含作品 ID 和固定应用版本参数时返回四类样本的完整数据；未发送 Cookie、签名、游客或设备标识。四类样本经真实 Minimal API、逐项顺序/配对比对、独立客户端下载和系统解码通过，详见[验证记录](../../../../docs/validation/cookie-free-douyin.md)。

## Goals / Non-Goals

**Goals:**

- 交付可独立验证的 .NET 10 HTTP API 与解析组件；仅在真实匿名媒体证据通过后启用生产平台。
- 将分享文本解析为稳定的作品与媒体契约，保留完整 URL、原图集顺序及实况配对关系。
- 对初始请求、短链跳转、元数据请求和播放跳转执行相同的无 Cookie 约束。
- 返回明确的未支持、不可用、协议变化或超时结果，允许调用方重新解析临时地址。
- 提供可复现证据，验证解析端与调用方网络中的实际下载链路。

**Non-Goals:**

- 账号登录、游客身份生成、身份池、浏览器自动化及会话回退。
- 服务端媒体下载/代理、文件持久化、转码、转录、批量采集、作者列表及其他平台适配。
- 相册权限、原生实况文件封装和系统相册导入；返回图片与短片不代表已完成这些端侧能力。

## Decisions

### 1. 以独立可行性验证作为生产启用门槛

先使用最小 .NET 验证入口与受控 HTTP 替身，证明每一跳都不发送 Cookie，再进行真实公开样本验证。验证入口不扩展为运行时的浏览器或身份服务。

| 样本类别 | 元数据匿名取得 | 完整资源及配对 | 最终 CDN | 调用方下载 | 当前状态 |
| --- | --- | --- | --- | --- | --- |
| 单个公开视频 | 通过 | 通过，1 段视频 | 通过 | 通过并解码 | supported |
| 普通图集 | 通过 | 通过，12 张静态图 | 通过 | 通过并解码 | supported |
| 实况图集 | 通过 | 通过，1 对图片/短片 | 通过 | 通过并解码 | supported |
| 普通图片与实况混合图集 | 通过 | 通过，3 静态图及 1 实况 | 通过 | 通过并解码 | supported |

证据记录验证时间、入口类别、样本类别、请求跳数、Cookie 缺席断言、响应分类、资源数量与调用方网络结果。用户提供的真实分享文本、分享链接、作品 ID、重定向完整地址和媒体 URL 仅可作为仓库外的临时测试输入，不写入代码、测试夹具、文档或提交记录。仓库内只保留合成样本及无样本标识的结构统计；动态参数和临时媒体 URL 不作为长期稳定性证明。

某类型只有完整通过后才记录为支持。未通过或明确返回失败不能勾选该类型的成功验收。无可用匿名数据入口时，可继续完成并独立验证共享解析组件、静态契约、Minimal API 和容器交付，但生产注册表必须保持该平台关闭、能力为 `unverified`。生产启用与媒体成功验收仍受真实匿名证据约束，不通过调整 Cookie 策略或把替身当作平台证明来宣称完成。

此前未验证阶段只交付运行结构并保持平台关闭。当前四类真实证据已通过，抖音注册表已启用且四项能力为 `supported`；早期失败结果作为历史证据保留。

### 1.1 已选定的匿名取数路线

- 已展开分享页若包含目标作品完整元数据则直接复用；完整作品链接本地提取 ID 后直接请求匿名详情，避免读取桌面空壳。
- 主入口为 `https://api.amemv.com/aweme/v1/aweme/detail/`；Query 仅有当前作品 `aweme_id` 和固定 `aid=6383`、`device_platform=web`、`version_code=190500`、`version_name=19.5.0`。这些是应用协议常量，不构造设备/账号/游客身份。
- 详情无可用数据时读取公开分享页及 `jingxuan.douyin.com/m/video/{workId}`。精选解析精确校验 `gid`，读取 `_SSR_DATA` 中 JSON 字符串 `video_model`，仅选有音轨的已知独立 MP4；不执行 JavaScript、拼接 DASH 或调用签名服务。
- 元数据入口只在精确主机 `api.amemv.com` 放行，不扩大到整个 `amemv.com`；它和播放入口均不能作为最终 CDN。所有访问仍执行公网 DNS、端口、凭据及跳转检查。
- 当前不需要本地签名；不移植未验证的 A-Bogus，不引入 UIFID、ttwid、msToken、设备标识或会话预热。
- 媒体项最多 4 项并发确认响应头，每个必需列表最多返回 2 个可用候选。`Task.WhenAll` 保留输入顺序，任一必需资源失败仍拒绝整次解析。

### 2. 一个运行项目，按职责组织最小解析管线

增加唯一运行宿主 `src/MediaLens.Api/MediaLens.Api.csproj`，采用 ASP.NET Core 10 Minimal API；共享的 `src/MediaLens.Core/MediaLens.Core.csproj` 承载契约、解析和 HTTP 边界，使宿主与 `tools/MediaLens.Probe` 不重复实现 Cookie 策略。全部项目加入 `MediaLens.slnx`，沿用 `Directory.Build.props` 和 `global.json`。协议与契约测试位于 `tests/MediaLens.Tests/`，验证工具回归位于 `tests/MediaLens.Probe.Tests/`，TCP 替身由 `tests/Shared/` 共用。

HTTP 端点统一采用 Minimal API，通过路由扩展方法集中注册。在 `MapGroup("/api/v1")` 下注册统一解析、`/platforms/{platform}/resolve` 指定解析与 `/platforms` 发现。两个 POST 端点通过同一解析服务执行，指定平台仅作为额外约束；GET 端点读取内存注册表。端点处理函数负责 DTO 绑定、调用依赖注入的解析服务、传播取消令牌及返回类型化结果；平台请求与媒体映射保持在解析管线中。宿主不注册 MVC Controller，也不通过 Controller 承载本次 API。

HTTP 集成验证使用可替换上游组件启动真实 Minimal API 宿主，检查路由、JSON 请求绑定、状态码、响应契约和取消传播；不以直接调用解析服务代替端点集成验证。

```text
HTTP API
  -> 分享链接识别与逐跳展开
  -> 抖音适配器：作品信息与媒体字段映射
  -> 最终媒体地址解析
  -> 统一结果
        |
        +-- 所有上游访问经过同一个无 Cookie HTTP 组件
```

采用 `MediaLens.Resolution`、`MediaLens.Platforms.Douyin`、`MediaLens.Http`、`MediaLens.Contracts` 等少量职责目录。接口与实现保持同一命名空间；使用文件范围命名空间，异步入口接收取消令牌。仅在平台适配与可替换网络边界引入接口，不增加仓储、身份池或动态插件框架。平台适配器及其描述信息通过依赖注入静态注册，由同一内存注册表支持解析分发与能力发现。首期只有 `douyin`；未来增加适配器时，HTTP 端点、共享请求体与结果契约不需要复制或按平台分叉。

JSON 契约使用静态类型与源生成序列化上下文，为 native AOT 保留兼容空间。本次提供常规发布的 Linux 容器；不将 native AOT 发布设为首轮验收条件。

不引入数据访问层、后台 Worker 或 Redis。唯一 API 宿主处理当前请求，共享库仅用于复用验证工具和服务的同一安全边界，不维护业务存储。

### 3. 无 Cookie 是请求行为约束

- API 请求体只接受 `text`。拒绝入站 `Cookie` 请求头及 `cookie`、`session`、`identity` 等身份输入字段，返回 `400`。
- 上游 HTTP 使用关闭 Cookie 功能和自动重定向的处理器，例如 `SocketsHttpHandler` 的 `UseCookies = false`、`AllowAutoRedirect = false`。不创建 Cookie 容器，不设置显式 Cookie 请求头。
- 逐跳处理 `Location`；每次请求重新按允许列表构建常规请求头，不复用上游 `Set-Cookie`。返回调用方的响应同样不包含 `Set-Cookie`。
- 共享连接可以服务无会话请求，但不在连接、请求头、缓存或日志中保存用户/游客身份材料。
- 常规请求头不等于身份构造。签名只有在输入来自当前业务请求、明确常量和受控时间/随机源，且不依赖 Cookie、游客指纹档案或浏览器会话时才可加入。
- 若上游要求会话，返回 `COOKIE_FREE_UNAVAILABLE`；不调用 Browser RPC、身份生成器或不透明 Token 服务回退。

验证必须覆盖上游首跳设置 Cookie 后的下一跳，以及不同调用间的隔离。仅检查配置项或请求体不足以证明没有隐式 Cookie。

### 3.1 公网地址规则的来源和范围

公网目标分类使用 2026-10-31 获取的 IANA IPv4 / IPv6 特殊用途注册表快照（26 个 IPv4 前缀、25 个 IPv6 前缀），按最长前缀处理父子网例外。只有目的地址有效、可转发且明确 Globally Reachable 的活动条目允许；N/A、空值、False 和废弃条目拒绝。IPv4 多播另外排除，未命中特殊用途表的 IPv6 仅接受全球单播 2000::/3。带区域编号的 IPv6 拒绝。

IPv4-mapped 地址按内嵌 IPv4 分类；标准 NAT64 前缀 64:ff9b::/96 还必须通过内嵌 IPv4 检查，遵循 RFC 6052 §3.1。自定义网络转换前缀和实际路由可达性不由静态地址表推断。共享 HTTP 策略的域名、端口、整组 DNS 拒绝及连接 IP 固定行为保持一致。规则维护、原始字段快照和边界测试见[公网地址分类记录](../../../../docs/validation/public-ip-classification.md)。

### 4. 链接识别先于平台元数据请求

统一入口从分享文案按出现顺序选择首个已注册且启用适配器可识别的链接；首期仅抖音。处理中文标点和可识别的无协议前缀链接。没有任何 URL 时返回 `INVALID_INPUT`；存在 URL 但没有可用平台适配器时返回 `PLATFORM_NOT_SUPPORTED`。多个链接时不并发解析、不隐式批量返回。

指定平台入口先检查平台键是否已注册并启用，再按文本顺序选择首个属于该适配器的链接。仅有其他平台 URL 时返回 `PLATFORM_MISMATCH`，不调用其他适配器。初始链接与短链展开后的作品归属都受相同平台约束，不将其他平台的作品 ID 传给抖音适配器。

识别完整作品路径和短链展开后的作品链接；查询参数 `modal_id`、`vid` 优先于主页路径。`/video/` 为视频候选，`/note/`、`/slides/` 为图集候选，最终类型以作品数据为准。作品 ID 全程使用字符串。主页、合集、音乐和直播不是本接口的单个作品输入。

每次网络访问只允许 HTTP(S) 且通过平台入口/CDN 规则校验，拒绝 URL 凭据、非公开地址和越界跳转。限制输入长度、跳转次数、元数据读取量及总超时；具体数值集中为有语义的配置项或常量。相对 `Location` 基于当前 URL 展开，循环与超限明确失败。

### 5. 元数据适配与本地签名分别验证

只解析实际匿名响应中可观察到的字段，不预设某个分享页脚本或作品接口必然匿名可用。网页响应和 JSON 响应仅在证据支持时增加对应解析入口。

本地签名不能单独证明上游会返回内容。若需要移植算法，固定 Query/Body 原始字节、User-Agent、时间与随机输入，对比可复现输出与中间摘要；保持参数顺序、编码、整数位移、字节序和自定义编码表的一致性。签名完成后不得再次改变被签名的参数。两个参考项目的算法版本分别验证，不混用常量或把变体密码过程替换为同名标准实现。

可空字段与缺失必需字段分开处理。内容明确不可用时返回内容错误；无会话响应不含媒体时返回匿名不可用；响应结构无法识别时返回上游协议错误，不推测为空图集或成功。

### 6. 平台无关的响应字段契约

两个解析 POST 接口使用同一成功响应。GET 平台发现与错误响应同样包含 `requestId`。HTTP 状态表达成功或失败，响应直接返回相应静态类型 DTO；不另设通用 `data` 包装层或重复的成功状态字段。

当前尚无已发布 API。本次将先前草案的字符串地址数组和跨层下载头覆盖收敛为 `imageSources` / `videoSources`：每个下载候选直接携带自己的地址、完整必要请求头和技术元数据。草案中的 `imageUrls`、`videoUrls`、顶层 `requestHeaders` 及项级头部覆盖字段不进入最终 v1 契约。首次发布后再改变这些既有字段需要按版本兼容规则处理。

#### 6.1 解析成功的顶层字段

下列键全部出现；允许为空的值显式使用 JSON `null`。字段名采用 camelCase，作品 ID 为字符串；时间统一使用带 `Z` 的 UTC ISO 8601 格式。

| 字段 | JSON 类型 | 可空 | 含义 |
| --- | --- | --- | --- |
| `requestId` | string | 否 | 服务端为本次 HTTP 请求生成的非空关联标识，不是作品或存储记录 ID。 |
| `platform` | string | 否 | 稳定平台标识，首期为 `douyin`，与实际选择的适配器一致。 |
| `workId` | string | 否 | 平台内作品 ID；作品身份由 `platform` 与 `workId` 共同确定。 |
| `workType` | string | 否 | `video` 或 `album`；单张图片同样属于 album。 |
| `webUrl` | string | 否 | 该作品的规范公开页面地址，区别于原始分享文案、短链和媒体 CDN。 |
| `description` | string | 是 | 当前元数据中取得的作品描述；未提供时为 `null`，不另发请求补齐。 |
| `items` | array | 否 | 非空、有序的媒体项；数组顺序是作品顺序。 |
| `resolvedAt` | string | 否 | 本次完整解析完成的 UTC 时间。 |
| `expiresAt` | string | 是 | 所有返回候选地址的最早可确认过期时间；任一候选未知时为 `null`。 |

不返回数据库 ID、下载任务 ID、原始上游响应、作者统计或额外抓取的展示信息。请求关联标识随成功、绑定错误、平台错误及未处理异常统一返回，并用于标准输出日志，不依赖数据库。

#### 6.2 媒体项字段

| 字段 | JSON 类型 | 可空 | 含义 |
| --- | --- | --- | --- |
| `index` | integer | 否 | 从 1 开始，严格等于其数组位置加 1；不是跨请求的持久化标识。 |
| `type` | string | 否 | `video`、`image` 或 `livePhoto`。 |
| `imageSources` | array | 否 | 同一张静态图的有序下载候选；不适用时为 `[]`。 |
| `videoSources` | array | 否 | 同一段视频的有序下载候选；不适用时为 `[]`。 |

| `type` | `imageSources` | `videoSources` |
| --- | --- | --- |
| `video` | 必须为空 | 至少一个候选 |
| `image` | 至少一个候选 | 必须为空 |
| `livePhoto` | 至少一个候选 | 至少一个候选，且属于同一项的动态片段 |

`workType: video` 必须且只包含一个 `type: video` 的媒体项；`workType: album` 包含一个或多个 `image` / `livePhoto` 项。普通图片和实况可以混排，保持源顺序。缺失实况图片/短片或其他必需项时返回 `MEDIA_INCOMPLETE`，不降级或删除缺失项后宣称完整成功。

同一候选列表中的多项是同一逻辑媒体的备用来源，调用方优先下载首项，失败后按序选择其他候选，不需要全部下载。候选可以具有不同格式、尺寸或码率，因此技术元数据必须随候选返回。只在同一资源内按完整 URL 与匹配请求头去重，不跨媒体项合并或重新编号。

#### 6.3 每个下载候选的字段

`imageSources` 和 `videoSources` 使用同一静态 `MediaSource` 契约。以下键全部出现，技术信息未知时为 `null`；不使用空串、零值或推测结果表示未知。

| 字段 | JSON 类型 | 可空 | 含义 |
| --- | --- | --- | --- |
| `url` | string | 否 | 完整的最终 CDN URL，保留全部路径及查询参数。 |
| `headers` | object | 否 | 此候选所需的完整常规 HTTP 请求头，键值均为字符串；不需要时为 `{}`，无跨层继承。 |
| `mimeType` | string | 是 | 经确认的媒体 MIME 类型，不根据 URL 后缀盲猜。 |
| `fileName` | string | 是 | 可选的安全保存文件名，仅含文件名、不含路径；只有后缀有可靠依据时才提供，调用方可自行命名。 |
| `width` | integer | 是 | 该候选已知的像素宽度，已知值必须大于 0。 |
| `height` | integer | 是 | 该候选已知的像素高度，已知值必须大于 0。 |
| `durationMs` | integer | 是 | 视频时长，单位毫秒，已知值必须大于 0；图片固定为 `null`。 |
| `sizeBytes` | integer | 是 | 该候选已知文件大小，单位字节，已知值必须大于 0。 |
| `expiresAt` | string | 是 | 该候选可确认的 UTC 过期时间，未知为 `null`。 |

头部从确认过的常规媒体头允许列表中产生，例如 User-Agent、Referer、Accept、Origin；候选之间独立取值，不存在顶层默认头或项内覆盖规则。任何层级不得返回 Cookie、Set-Cookie、Authorization、Proxy-Authorization 或上游账号凭据，也不回显原始上游整组头部。

尺寸、时长、大小和 MIME 信息只能来自当前匿名元数据或有限的媒体响应头读取；不为补齐这些可空字段下载媒体正文。技术元数据缺失不阻止成功，必需的媒体类型、配对或最终下载地址缺失仍必须失败。已明确过期的候选不返回；某必需资源没有剩余候选时返回 `MEDIA_INCOMPLETE`。

#### 6.4 成功响应示例

以下域名、头部、时间与元数据仅为契约示例，不代表已有匿名成功证据或实际可下载链接。

```json
{
  "requestId": "example-request-mixed-album",
  "platform": "douyin",
  "workId": "123456",
  "workType": "album",
  "webUrl": "https://www.douyin.com/note/123456",
  "description": "普通图片与实况混合图集示例",
  "items": [
    {
      "index": 1,
      "type": "image",
      "imageSources": [
        {
          "url": "https://cdn.example/01_image.jpg",
          "headers": {
            "User-Agent": "<download user agent>",
            "Referer": "https://www.douyin.com/"
          },
          "mimeType": "image/jpeg",
          "fileName": "douyin_123456_01_image.jpg",
          "width": 1080,
          "height": 1440,
          "durationMs": null,
          "sizeBytes": null,
          "expiresAt": null
        }
      ],
      "videoSources": []
    },
    {
      "index": 2,
      "type": "livePhoto",
      "imageSources": [
        {
          "url": "https://cdn.example/02_image.jpg",
          "headers": {
            "User-Agent": "<download user agent>",
            "Referer": "https://www.douyin.com/"
          },
          "mimeType": "image/jpeg",
          "fileName": "douyin_123456_02_image.jpg",
          "width": 1080,
          "height": 1440,
          "durationMs": null,
          "sizeBytes": null,
          "expiresAt": null
        }
      ],
      "videoSources": [
        {
          "url": "https://cdn.example/02_video.mp4",
          "headers": {
            "User-Agent": "<download user agent>",
            "Referer": "https://www.douyin.com/"
          },
          "mimeType": "video/mp4",
          "fileName": "douyin_123456_02_video.mp4",
          "width": 720,
          "height": 960,
          "durationMs": 5000,
          "sizeBytes": null,
          "expiresAt": null
        }
      ]
    }
  ],
  "resolvedAt": "2026-10-30T08:00:00Z",
  "expiresAt": null
}
```

### 7. 返回最终媒体地址，不把播放接口作为 CDN 成功结果

直接媒体地址经允许列表与响应证据确认后保留完整 URL；若地址指向播放接口，使用同一无 Cookie HTTP 组件继续解析跳转。可以通过流式 GET 取得响应头后关闭响应，避免下载媒体正文；不把 HEAD 可用性设为所有入口的前提。

只返回可作为媒体下载目标的最终 URL 与匹配的安全请求头。不得删除、重排或重新生成 URL 的签名/查询参数；每个候选携带自己的请求头及元数据，不能把一个候选的头部、格式或大小套用到其他候选。失败的备用地址可排除；必需资源没有可用候选时整次解析失败。

`resolvedAt` 使用 UTC。每个候选具有独立 `expiresAt`；顶层值只有在所有返回候选的过期依据均可确认时才取最早值，任一未知则为 `null`。该值是地址有效期信息的保守汇总，不承诺此前始终可下载，不采用固定猜测 TTL。调用方遇到地址失效时重新解析原分享内容。

服务端取得 URL 不证明调用方网络能够下载。验收需由独立客户端用返回 URL 和头部实际取得资源；协议替身或解析端连通测试不能代替这一项。

### 8. 面向多平台的 Minimal API 路径与错误契约

统一使用 `/api/v1` 作为客户端契约版本前缀。路径中的平台标识为稳定小写字符串，首期为 `douyin`。响应的 `platform` 与注册表中的平台标识一致；客户端不应把当前只存在抖音理解为平台枚举永久封闭。

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| POST | `/api/v1/resolve` | 从分享文本识别已注册、启用的平台，调用相应适配器。 |
| POST | `/api/v1/platforms/{platform}/resolve` | 固定平台约束后解析；首期有效平台路径为 `/api/v1/platforms/douyin/resolve`。 |
| GET | `/api/v1/platforms` | 返回注册平台的标识、名称、启用状态与逐类型能力。 |

两个 POST 接口都只接收 `{ "text": "公开分享文本" }`，共用输入约束、无 Cookie HTTP 组件、媒体结果和错误响应。成功返回 `200`；不创建后台下载任务或返回媒体正文。指定平台不在请求体重复声明。统一入口选择首个可用适配器支持的链接；指定入口选择文本中首个属于指定适配器的链接，仅有其他平台链接时明确拒绝。

平台分发与能力描述来自同一个静态依赖注入注册表。指定平台不是上游 URL 模板，不能用路径参数拼接任意平台主机。平台未注册时显式入口返回 `404 PLATFORM_NOT_FOUND`；平台已注册但未启用时返回 `422 PLATFORM_NOT_SUPPORTED`；输入归属与指定平台不符时返回 `400 PLATFORM_MISMATCH`。统一入口有 URL 但无可用适配器时返回 `422 PLATFORM_NOT_SUPPORTED`。

平台发现只读取内存配置，不请求上游、不保存业务数据。它包含已注册但被关闭的适配器，不能列出尚未注册的未来平台。`enabled` 表示运行配置是否启用该适配器；能力值为 `unverified`、`supported` 或 `unsupported`，反映本实现的验证覆盖，不代表平台实时健康状态。启用解析前至少一个目标类型必须有通过证据；类型是否支持独立于平台注册或启用状态。

平台发现响应包含两个必需字段：`requestId`（本次请求关联字符串）与 `platforms`（非空或空数组，不为 `null`）。每个平台项字段固定如下：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `id` | string | 稳定的平台标识，与解析响应 `platform` 一致。 |
| `displayName` | string | 平台展示名称。 |
| `enabled` | boolean | 当前运行配置是否启用，区别于平台实时健康状态。 |
| `capabilities` | object | 包含 `video`、`imageAlbum`、`livePhoto`、`mixedAlbum` 四个能力键，值为 `unverified`、`supported`、`unsupported`；四个键全部出现。 |

当前发现接口的状态示例（requestId 为合成值）：

```json
{
  "requestId": "example-request-platforms",
  "platforms": [
    {
      "id": "douyin",
      "displayName": "抖音",
      "enabled": true,
      "capabilities": {
        "video": "supported",
        "imageAlbum": "supported",
        "livePhoto": "supported",
        "mixedAlbum": "supported"
      }
    }
  ]
}
```

未来注册新平台后即可沿用统一入口，并使用 `/api/v1/platforms/<平台标识>/resolve`。仅新增平台、增加兼容的可选字段时保持 v1；修改必需字段、既有类型语义或破坏既有调用方式时规划 v2。上游平台自身的接口版本不进入 MediaLens 客户端路径。不按视频、图片或实况重复设置解析路由，类型由共用响应表达。

替代方案：仅暴露 `/api/v1/douyin/resolve` 等平台路径。该方式使每个调用方都需要预先识别平台，因此保留统一入口，并把显式平台路径用于已有平台上下文的调用。无数据库及持久化作品资源的当前范围不引入 `/works/{id}` 查询或下载任务路径。

| HTTP 状态 | 错误码 | 语义 |
| --- | --- | --- |
| 400 | `INVALID_INPUT` | 文本无效、没有 URL，或携带被禁止的身份输入 |
| 400 | `PLATFORM_MISMATCH` | 指定平台与输入或展开后的作品链接不一致 |
| 404 | `PLATFORM_NOT_FOUND` | 显式路径中的平台标识未注册 |
| 422 | `PLATFORM_NOT_SUPPORTED` | 平台已注册但未启用，或统一入口没有可用适配器 |
| 422 | `UNSUPPORTED_RESOURCE` | 已识别平台的链接不是本次支持的单个作品资源 |
| 422 | `COOKIE_FREE_UNAVAILABLE` | 当前匿名链路未取得媒体或被要求建立会话 |
| 422 | `MEDIA_INCOMPLETE` | 必需媒体或实况配对不完整，含无法取得最终 CDN 地址 |
| 404 | `CONTENT_UNAVAILABLE` | 上游有明确证据表明作品已删除或不可访问 |
| 502 | `UPSTREAM_CHANGED` | 响应结构或媒体协议无法识别 |
| 503 | `UPSTREAM_UNAVAILABLE` | 网络失败或上游限流等暂时故障 |
| 504 | `RESOLUTION_TIMEOUT` | 超出解析总时限 |

错误响应根字段为 `requestId` 与 `error`；不返回部分作品 `items`。`error` 的四个键全部出现：

| 字段 | 类型 | 可空 | 含义 |
| --- | --- | --- | --- |
| `code` | string | 否 | 稳定的机器可读错误码，调用方据此分支处理。 |
| `message` | string | 否 | 安全的可读说明，不能作为程序判断依据。 |
| `retryable` | boolean | 否 | 原请求是否属于可在稍后重试的暂时故障，不表示立即或无限重试。 |
| `retryAfterSeconds` | integer | 是 | 已确认的建议重试等待秒数，必须大于等于 0；未知或 `retryable: false` 时为 `null`。 |

示例对应 `422`：

```json
{
  "requestId": "example-request-unavailable",
  "error": {
    "code": "COOKIE_FREE_UNAVAILABLE",
    "message": "当前匿名解析路线无法取得该作品的完整媒体。",
    "retryable": false,
    "retryAfterSeconds": null
  }
}
```

仅对暂时故障提示可重试；平台不存在、未支持和不匹配均不可通过原样重试解决。若同时返回 Retry-After 响应头，它与 `retryAfterSeconds` 的等待语义一致。不暴露原始上游响应、敏感头部或内部异常，不把任意 403/空响应推断为作品删除。

Minimal API 的 JSON 绑定错误、中间件错误及应用内异常也采用该形状和请求关联标识。补充通用 HTTP 错误映射：`404 ENDPOINT_NOT_FOUND`、`405 METHOD_NOT_ALLOWED`、`415 UNSUPPORTED_MEDIA_TYPE`、`500 INTERNAL_ERROR`；框架绑定失败使用 `400 INVALID_INPUT`。非暂时故障的上述错误返回 `retryable: false`、`retryAfterSeconds: null`。

序列化约定对三个端点一致：固定字段显式输出，可空值用 `null`，列表不用 `null`、字典不用 `null`，字段名 camelCase，枚举用文档中的大小写敏感字符串。客户端应忽略未识别的可选字段，并对未知错误码采用通用失败处理；新增平台标识保持开放字符串，既有媒体类型语义的改变仍按破坏性版本处理。使用 `ResolveResponse`、`MediaItem`、`MediaSource`、`PlatformListResponse`、`ApiErrorResponse` 等静态 DTO 与源生成序列化，不以动态对象或匿名类型作为公开响应契约。

### 9. 以常规 Linux 容器交付最小 API

在 API 项目存在后添加多阶段 Dockerfile、环境配置说明及必要的容器忽略项。解析服务只读取有限元数据和媒体响应头，不依赖媒体目录、数据库、Redis、浏览器或平台账号配置。

## Risks / Trade-offs

- [匿名入口不可用] → 以四类样本矩阵为前置门槛，记录负面证据，保持该类型未通过状态；不改动无 Cookie 边界。
- [平台字段或签名变化] → 将适配隔离在抖音模块，保留脱敏响应样本与固定输入验证，线上探测不替代离线契约验证。
- [URL 过期或调用方网络不可下载] → 保留完整 URL/必要头部，未知有效期返回 `null`，由调用方验证下载并支持重新解析。
- [混合图集丢项或实况错配] → 按原始图片项建立结果，必需配对缺失时失败，不对图片和动态片段分别编号。
- [严格约束降低支持率] → 如实公布已通过类型；失败错误的正确性与媒体解析成功率分别记录。
- [共享连接无意携带状态] → 关闭 Cookie 处理并对显式头部、重定向和跨调用建立可观察断言。

## Migration Plan

1. 完成受控网络验证和真实匿名样本矩阵，提交证据及可行性结论。
2. 实现共享组件、统一契约、Minimal API 与离线协议验证；无可用匿名媒体证据时保持平台关闭，未通过类型不能宣称完成。
3. 完成调用方下载验证后，加入容器运行材料并更新中英文 README 的真实支持范围。
4. 新仓库没有存量 API 或数据迁移。若发布后协议退化，可撤回部署或将受影响类型标记为不可用；不启用 Cookie/浏览器回退。

## Open Questions

- 已验证匿名详情覆盖当前四类样本；公开阅读页另有视频成功证据。不同网络、其他公开作品及平台后续协议变化仍需持续验证。
- 四类目标样本的数量、顺序和实况配对已通过；系统相册权限与原生实况封装仍由调用方负责。
- 当前调用方网络仅凭返回 URL 和常规头部即可下载；尚未对其他地区或网络作覆盖承诺。
- 当前主入口和精选视频入口均未要求本地签名；未来退化时保持明确失败，不自动增加身份依赖。
