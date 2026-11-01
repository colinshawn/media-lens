# MediaLens

**从分享链接，看见媒体内容。**

[English](README.md) | 简体中文

MediaLens 是一个使用 .NET 10 开发的社交分享内容解析项目，目标是将公开分享文本解析为结构化的视频、图片和实况照片资源。调用方取得资源信息后，自行下载并保存到本地存储或相册。

> **当前状态：已支持抖音四类媒体及 Bilibili 普通公开视频的完整 MP4 解析。** 两个平台均保持严格无 Cookie。抖音视频、普通图集、实况和混合图集的验证见[抖音记录](docs/validation/cookie-free-douyin.md)；Bilibili 的 BV/AV、短链、分 P、完整音视频下载及网络条件见[Bilibili 记录](docs/validation/cookie-free-bilibili.md)。

## 项目目标

- 从粘贴的分享文本中提取支持的链接，并展开短链。
- 识别平台、资源类型和作品 ID。
- 获取公开可访问的作品信息，返回最终 CDN 地址及下载所需的请求头。
- 保留图集顺序，以及每张实况照片的静态图与动态短片的配对关系。
- 提供精简的 HTTP API，通过平台适配器组织各平台的解析逻辑。

当前支持抖音公开分享与 Bilibili 普通 UGC 视频；其他平台通过独立适配器及匿名验证接入。

## 无 Cookie 边界

实现始终保持以下严格无 Cookie 边界：

- 不接收、生成、存储或发送 Cookie。不转发调用方的 `Cookie` 请求头，不复用上游的 `Set-Cookie`，短链及播放地址的重定向过程同样遵守这一要求。
- 不构造账号或游客身份，不维护身份池，不使用浏览器会话作为回退方案。
- 可以使用常规 HTTP 请求头和本地签名计算，前提是不依赖身份或会话构造。
- 匿名链路无法返回媒体数据时，明确返回不支持或不可用的结果，不隐式切换到 Cookie 方案。

公开分享可被查看，不等于作品信息接口一定允许无会话访问。在宣称支持某个平台或媒体类型前，需要先完成无 Cookie 可行性验证。

## 解析流程

```text
分享文本
  -> 提取 URL 并展开短链
  -> 识别平台与作品 ID
  -> 无 Cookie 请求作品信息
  -> 整理视频、图片和实况资源
  -> 将播放接口跳转解析为最终 CDN 地址
  -> 返回结构化媒体信息
```

解析服务负责返回资源地址与元数据。媒体下载、本地文件保存、相册权限，以及系统原生实况照片的封装或导入，由调用方负责。

## 平台支持范围

| 平台 | 视频 | 普通图集 | 实况 / 混合图集 |
| --- | --- | --- | --- |
| 抖音 douyin | 已支持 | 已支持 | 已支持 |
| Bilibili bilibili | 普通 UGC 的完整单段 MP4 | 未实现 | 未实现 |

Bilibili 接受 BV/AV 完整视频链接与 b23.tv 短链；AV 通过可信详情规范化为 BV。一个请求仅解析一个分 P，省略 p 时选择第 1 P，指定时从分享 URL 的 `?p=2` 等查询参数读取。无效或越界 p 明确失败，不增加 JSON 请求字段、平台专用路由或批量行为。

Bilibili 的 workId 标识整稿 BV，所选 P 由 webUrl 的 p 区分，文件名也包含 P。同一稿件不同 P 可以有相同 workId，保存或缓存分 P 资源时需保留这个区别。

首期返回无需组合的完整 MP4，并保留作品原有音频；DASH 分离轨道、连续多段、FLV/HLS、试看、番剧、直播、互动或图文返回明确失败。画质以实际匿名响应为准；请求 qn 不代表取得对应画质，未知候选尺寸保持 null。本次样本取得 quality=64 的完整媒体，不承诺所有作品都能取得同一清晰度。

## HTTP 接口

以下三个端点均使用 ASP.NET Core 10 Minimal API。当前注册并启用 `douyin` 与 `bilibili`，共享路由、静态 DTO 和错误契约。

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| POST | `/api/v1/resolve` | 根据分享文本自动识别平台并解析媒体。 |
| POST | `/api/v1/platforms/{platform}/resolve` | 指定平台解析，例如 `/api/v1/platforms/douyin/resolve` 或 `/api/v1/platforms/bilibili/resolve`。 |
| GET | `/api/v1/platforms` | 查询已注册平台、启用状态和经验证的媒体能力。 |

两个 POST 接口共用 `{ "text": "公开分享文本" }` 请求体及媒体结果模型。显式平台仅增加平台约束，链接归属不匹配时明确失败，不切换到其他适配器。平台发现接口读取内存中的适配器注册表，不请求上游平台、不依赖数据库。

增加平台时保持统一入口不变。平台标识使用稳定的小写字符串；视频、图片、实况通过结果类型表达，不分别建立 `/videos`、`/images`、`/live-photos` 路由。兼容版本统一位于 `/api/v1`，单纯增加平台不升级为 `/api/v2`。

能力状态区分 `unverified`（未验证）、`supported`（已支持）、`unsupported`（未支持）。当前抖音四项均已支持，Bilibili 仅视频已支持，其他三项为 `unsupported`；未经验证的类型不标记为已支持。能力状态反映实现的验证覆盖，不作为上游实时可用性探测。

## 响应契约

两个 POST 接口已实现相同的静态响应契约。下方成功示例仅为合成数据，示例地址不能下载。

```json
{
  "requestId": "example-request-video",
  "platform": "douyin",
  "workId": "123456",
  "workType": "video",
  "webUrl": "https://www.douyin.com/video/123456",
  "description": "公开视频示例",
  "items": [
    {
      "index": 1,
      "type": "video",
      "imageSources": [],
      "videoSources": [
        {
          "url": "https://cdn.example/01_video.mp4",
          "headers": {
            "User-Agent": "<download user agent>",
            "Referer": "https://www.douyin.com/"
          },
          "mimeType": "video/mp4",
          "fileName": "douyin_123456_01_video.mp4",
          "width": 1080,
          "height": 1920,
          "durationMs": 12000,
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

- 每个响应包含服务端生成的 `requestId`；作品通过 `(platform, workId)` 标识，ID 使用字符串。
- `workType` 为 `video` 或 `album`。媒体项使用从 1 开始的连续 `index`，`type` 为 `video`、`image` 或 `livePhoto`。
- `imageSources`、`videoSources` 为同一张图片或同一段视频的有序备用候选，不是额外作品项。调用方从每个必需列表中择一下载，不需要下载全部候选；不适用的列表为 `[]`。
- 实况的静态图和动态短片候选保存在同一媒体项中；混合图集保持原始项顺序。
- 每个候选携带完整 `url`、独立 `headers` 及可空的 `mimeType`、`fileName`、`width`、`height`、`durationMs`、`sizeBytes`、`expiresAt`。下载头没有跨层继承，不同候选的格式、尺寸和大小可以不同。
- 保留 URL 及全部查询参数；下载请求头不得包含 Cookie 或上游账号凭据。
- 未知技术元数据显式返回 `null`，图片时长为 `null`。顶层 `expiresAt` 仅在所有返回候选的过期时间均已知时取最早值，否则为 `null`。
- 错误形状为 `{ "requestId": "...", "error": { "code": "...", "message": "...", "retryable": false, "retryAfterSeconds": null } }`；平台发现返回 `requestId` 与 `platforms`。
- 完整的[响应字段设计](openspec/changes/archive/2026-10-31-add-cookie-free-douyin-resolution/design.md)包含混合图集示例、类型、可空性及错误语义。调用方负责本地保存和原生实况封装/导入。

下载时必须同时使用候选的 `url` 与 `headers`。只在浏览器地址栏打开 URL 可能因缺少正确的 Referer 等请求头而返回 403；候选失败后应按序尝试其他来源。

CDN 地址可能失效。下载因地址过期失败时，应重新解析原分享文本，保留 URL 的全部查询参数，并使用该候选自己的请求头。未知过期时间为 `null`，不代表永久有效。

错误通过稳定的 `code` 判断：`INVALID_INPUT`、`PLATFORM_MISMATCH` 使用 400；`PLATFORM_NOT_FOUND`、有明确证据的 `CONTENT_UNAVAILABLE` 使用 404；平台关闭、匿名不可用、媒体缺失和资源不支持使用 422。`UPSTREAM_CHANGED` 为 502，暂时故障 `UPSTREAM_UNAVAILABLE` 为 503，`RESOLUTION_TIMEOUT` 为 504。框架错误使用相同结构；仅暂时故障提示可重试，不会因身份或输入问题建立会话回退。

## 技术选型

| 范围 | 选型 |
| --- | --- |
| 开发语言 | C# |
| 开发平台 | .NET 10，目标框架为 `net10.0` |
| API 宿主 | ASP.NET Core 10 Minimal API |
| 推荐部署方式 | Docker，使用 Linux 容器 |
| 开源许可证 | MIT |

## 开发环境

安装稳定版 .NET 10.0 SDK，并在仓库根目录检查版本：

```sh
dotnet --version
```

[`global.json`](global.json) 使用 `latestFeature` 策略选择已安装的 `10.0.100` 或更高 10.0 功能带 SDK，并禁用预览版 SDK。[`Directory.Build.props`](Directory.Build.props) 定义统一的目标框架、可空引用类型、隐式 using 和项目元数据。SDK 选择规则见 [.NET 官方文档](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)。

在仓库根目录构建并测试验证工具：

```sh
dotnet restore MediaLens.slnx
dotnet build MediaLens.slnx --no-restore
dotnet test MediaLens.slnx --no-build
dotnet format MediaLens.slnx --no-restore --verify-no-changes
```

`src/MediaLens.Api` 提供 HTTP 宿主；`src/MediaLens.Core` 包含契约、注册表、适配器，以及与验证工具共用的无 Cookie 传输组件。两个测试项目使用合成输入、本地 TCP 上游和真实 Kestrel 宿主，验证 Cookie 隔离、媒体配对、路由、序列化、错误、取消、过期规则与脱敏。

本地启动 API：

```sh
export SECRET_TOKEN="$(openssl rand -hex 32)"
dotnet run --project src/MediaLens.Api -- --urls http://127.0.0.1:8080
curl -H "Authorization: Bearer ${SECRET_TOKEN}" http://127.0.0.1:8080/api/v1/platforms
curl -X POST http://127.0.0.1:8080/api/v1/resolve \
  -H "Authorization: Bearer ${SECRET_TOKEN}" \
  -H 'Content-Type: application/json' \
  -d '{"text":"https://www.douyin.com/video/123456"}'
```

发现接口返回 `douyin`、`bilibili` 两个平台及各自能力；两者均为 `enabled: true`。两个 POST 可对可访问作品返回媒体结果；运行示例时需将合成 ID 替换为公开分享文本。入站 Cookie 头或额外身份字段会被拒绝；服务不使用数据库、媒体存储、浏览器或会话预热。

```sh
dotnet run --project tools/MediaLens.Probe -- \
  --input /absolute/path/outside-repo/samples.private.json --entry share
```

输入为 `{ "kind": "video", "text": "分享文本" }` 的数组；类别可为 `video`、`imageAlbum`、`livePhoto`、`mixedAlbum`。入口可为 `share`、`mobile`、`desktop`、`detail`、`legacy`、`adapter`；后者直接验证与生产共用的适配器。报告不含原分享文本、URL、作品 ID、原始响应或 Cookie 值。真实输入和本地输出保存在仓库外，测试夹具只使用合成数据。

Bilibili 使用相同仓库外输入格式，kind 仅为 video。`--platform` 缺省 douyin；Bilibili 缺省 adapter 入口，也可显式指定：

```sh
dotnet run --project tools/MediaLens.Probe -- \
  --platform bilibili --entry adapter \
  --input /absolute/path/outside-repo/bilibili.private.json
```

其他诊断入口保留用于抖音。报告不输出真实分享、BV/AID/CID、签名 Query 或完整媒体地址；工具只验证元数据，完整下载仍由独立客户端完成。

退出码 `0` 仅表示所有输入均观察到完整媒体元数据，**不代表**调用方下载通过；`1` 表示至少一个样本未通过，`2` 表示输入不合法。[验证记录](docs/validation/cookie-free-douyin.md)包含当前结果、公网 DNS 映射限制及成功证据的适用范围。

## Docker 部署（推荐）

Dockerfile 使用 Linux 多阶段构建：

| 阶段 | Microsoft 基础镜像 |
| --- | --- |
| 构建与发布 | `mcr.microsoft.com/dotnet/sdk:10.0` |
| 运行 API | `mcr.microsoft.com/dotnet/aspnet:10.0` |

应用配置通过环境变量传入，日志采用容器标准输出方式。核心解析流程不应依赖持久化媒体存储。[`.dockerignore`](.dockerignore) 排除本地构建产物、IDE 状态和本地配置。

```sh
docker build -t media-lens:local .
docker run --rm --read-only --tmpfs /tmp:rw,nosuid,size=32m \
  -e SECRET_TOKEN -p 127.0.0.1:8080:8080 media-lens:local
```

本机启用 TUN Fake-IP DNS 时，需让平台和 CDN 主机返回真实公网地址；Bilibili 包括 bilibili.com、b23.tv 和 bilivideo.com 的相应主机。服务继续拒绝合成、内网和保留地址，详见[抖音网络条件](docs/validation/cookie-free-douyin.md)与[Bilibili 网络条件](docs/validation/cookie-free-bilibili.md)。诊断 DNS 或 hosts 不作为项目部署配置。

容器使用基础镜像提供的非 root 用户，不依赖浏览器、数据库、Redis、媒体卷或上游账号配置。环境变量 `MediaLens__ResolutionTimeoutSeconds` 可设置 1—120 秒的总解析时限，默认 30 秒。

### 接口访问与运行配置

**部署兼容性变更：**三个 API 均要求 `Authorization: Bearer <token>`，平台发现和探测请求也需要携带凭证。最终生效的密钥缺失或非法时，程序在监听前以非零状态退出，不提供默认密钥、自动生成或匿名回退。升级现有容器时必须同步注入密钥并更新调用方。

| 环境变量 | 用途 | 校验与默认值 | 原生兼容入口 |
| --- | --- | --- | --- |
| `SECRET_TOKEN` | 本服务共享访问凭证 | 必填；32～256 个符合 Bearer 字符语法的 ASCII 字符，包含边界 | `MediaLens__Auth__SecretToken` / `MediaLens:Auth:SecretToken` |
| `HTTP_PORT` | 容器内 HTTP 监听端口 | 单个十进制整数，1～65535；镜像默认 8080 | 显式 URL / Kestrel 端点 |

Dockerfile 直接设置 `ENV HTTP_PORT=8080`，启动容器时覆盖 `HTTP_PORT` 即可修改端口，并需注入必填的 `SECRET_TOKEN`。端口环境变量统一使用 `HTTP_PORT`，程序不再为其他端口环境变量提供兼容别名。开发用 `launchSettings.json` 已排除出构建上下文。

长度按 token 本体计数，不包含 `Bearer ` 前缀，也不按 Base64 解码后的字节数计算。非 ASCII、空白、非法填充及长度越界均被拒绝，不裁剪或截断。建议使用 32 个安全随机字节生成密钥，例如 64 字符十六进制或 43 字符无填充 Base64URL；长度合规不等于随机强度合格。密钥仅用于 API 入站校验，不传入平台请求、响应下载头或日志。进入鉴权层的无效凭证返回 `401 / UNAUTHORIZED`、`WWW-Authenticate: Bearer` 和既有 `requestId + error` JSON；非法格式在计算摘要与正文绑定前拒绝。

针对同一个内部配置键，优先级为：命令行 > 对外环境变量 > 配置文件 > 默认值。密钥仍支持 .NET 原生环境变量，优先级位于对外变量与配置文件之间。已选中的非法值直接失败，不回退。`HTTP_PORT` 在内部映射为框架的 `HTTP_PORTS` 配置键；`--urls`、`ASPNETCORE_URLS` 和显式 Kestrel 端点继续按框架规则优先于端口模式。单端口校验只在 `HTTP_PORT` 提供最终端口配置时生效。非容器运行且完全没有监听配置时保留框架默认值；Dockerfile 设置 `HTTP_PORT=8080`。

上方 Docker 命令从当前 shell 环境读取 `SECRET_TOKEN`。也可将 [`.env.example`](.env.example) 复制为已忽略的 `.env`，在本地填入空的密钥占位，再用 `--env-file .env` 替换 `-e SECRET_TOKEN`。真实密钥不得提交到仓库。Compose 可显式把 shell/`.env` 的插值结果传入容器：

```yaml
services:
  media-lens:
    image: media-lens:local
    environment:
      SECRET_TOKEN: "${SECRET_TOKEN:?请先配置 SECRET_TOKEN}"
      HTTP_PORT: "8080"
    ports:
      - "127.0.0.1:8080:8080"
    read_only: true
    tmpfs:
      - /tmp:rw,nosuid,size=32m
```

修改 `HTTP_PORT` 不会自动修改 Docker 发布端口或 `EXPOSE`。例如 `-e HTTP_PORT=9000 -p 127.0.0.1:8080:9000` 表示宿主机 8080 转发到容器内 9000。公网入口应由可信反向代理或部署平台提供 HTTPS，代理至容器的链路也应受控。共享 token 只证明持有密钥，不能证明来自指定网站或 App；避免在公开前端内置共享密钥，CORS 也不能替代接口鉴权。更新运行配置并重启或重建服务即可更换密钥，旧密钥随即失效。

构建镜像后可运行 `python3 tools/validate-api-access-control.py` 复现生产镜像检查；脚本仅使用合成凭证，并清理本次临时容器。长度边界、回归与 Docker 证据及适用范围见[访问控制验证记录](docs/validation/api-access-control.md)。

镜像构建、平台发现、两个 POST、Cookie 输入拒绝和只读文件系统验证均已通过，未产生持久化写入。真实媒体与部署检查分别记录，详见[抖音容器验证](docs/validation/container-validation.json)与[Bilibili API/容器证据](docs/validation/2026-10-31-bilibili-success.json)。

## 首批里程碑

- [x] 验证抖音公开视频、普通图集、实况图集和混合图集的无 Cookie 解析链路。
- [x] 实现最小 API、统一结果模型和明确的失败响应。
- [x] 从调用方网络验证最终 CDN 下载，并保留实况资源的配对关系。
- [x] 补充可重复的测试、Docker 打包和部署说明。

## 许可

MediaLens 使用 [MIT License](LICENSE)。
