# MediaLens

**从分享链接，看见媒体内容。**

[English](README.md) | 简体中文

MediaLens 是一个使用 .NET 10 开发的社交分享内容解析项目，目标是将公开分享文本解析为结构化的视频、图片和实况照片资源。调用方取得资源信息后，自行下载并保存到本地存储或相册。

> **当前状态：项目初始化。** 仓库目前包含项目文档和通用开发配置。平台适配及可运行的 API 尚处于规划阶段，严格无 Cookie 的解析链路尚未经过线上验证。

## 项目目标

- 从粘贴的分享文本中提取支持的链接，并展开短链。
- 识别平台、资源类型和作品 ID。
- 获取公开可访问的作品信息，返回最终 CDN 地址及下载所需的请求头。
- 保留图集顺序，以及每张实况照片的静态图与动态短片的配对关系。
- 提供精简的 HTTP API，通过平台适配器组织各平台的解析逻辑。

首个验证目标是抖音公开分享。其他平台将逐一评估；上述目标不代表当前已经支持这些能力。

## 无 Cookie 边界

严格无 Cookie 是项目要求，平台是否允许这种访问方式仍需验证：

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

## 结果模型草案

以下示例说明预期的数据形态，不是已经实现的 API 契约。示例中的链接不指向真实媒体。

```json
{
  "platform": "douyin",
  "workId": "123456",
  "workType": "album",
  "items": [
    {
      "type": "image",
      "imageUrls": ["https://cdn.example/image-01.jpg"]
    },
    {
      "type": "livePhoto",
      "imageUrls": ["https://cdn.example/image-02.jpg"],
      "videoUrls": ["https://cdn.example/image-02.mp4"]
    }
  ],
  "requestHeaders": {
    "User-Agent": "<download user agent>",
    "Referer": "https://www.douyin.com/"
  },
  "expiresAt": null
}
```

- `workType` 区分 `video` 和 `album`；每个资源项分别标记为 `video`、`image` 或 `livePhoto`。同一图集可以混合普通图片和实况照片。
- 视频资源使用 `videoUrls`；实况资源将静态图与动态短片放在同一个资源项内。
- 保留完整 URL，包括其中的查询参数。媒体地址可能有时效性，失效后调用方应重新解析分享内容。
- 下载请求头中不得包含 Cookie 或上游账号凭据。如果不同资源需要不同请求头，应按资源分别返回。
- 无法确定过期时间时使用 `null`，不推测有效期。还需要在调用方的实际网络环境中验证最终 CDN 下载链路。

## 技术选型

| 范围 | 选型 |
| --- | --- |
| 开发语言 | C# |
| 开发平台 | .NET 10，目标框架为 `net10.0` |
| API 宿主规划 | ASP.NET Core 10 |
| 推荐部署方式 | Docker，使用 Linux 容器 |
| 开源许可证 | MIT |

## 开发环境

安装稳定版 .NET 10.0 SDK，并在仓库根目录检查版本：

```sh
dotnet --version
```

[`global.json`](global.json) 使用 `latestFeature` 策略选择已安装的 `10.0.100` 或更高 10.0 功能带 SDK，并禁用预览版 SDK。[`Directory.Build.props`](Directory.Build.props) 定义统一的目标框架、可空引用类型、隐式 using 和项目元数据。SDK 选择规则见 [.NET 官方文档](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)。

当前尚无可构建或运行的应用项目。首条匿名解析链路验证通过后，再加入运行项目及相应验证测试。

## Docker 部署（推荐）

计划使用多阶段 Docker 构建：

| 阶段 | Microsoft 基础镜像 |
| --- | --- |
| 构建与发布 | `mcr.microsoft.com/dotnet/sdk:10.0` |
| 运行 API | `mcr.microsoft.com/dotnet/aspnet:10.0` |

应用配置通过环境变量传入，日志采用容器标准输出方式。核心解析流程不应依赖持久化媒体存储。[`.dockerignore`](.dockerignore) 排除本地构建产物、IDE 状态和本地配置。

Dockerfile、Compose 配置和可执行部署命令将在 API 实现时一并提供。基础镜像与多阶段构建方式可参考 Microsoft 的 [ASP.NET Core 容器文档](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images?view=aspnetcore-10.0)。

## 首批里程碑

- [ ] 验证抖音公开视频、普通图集、实况图集和混合图集的无 Cookie 解析链路。
- [ ] 实现最小 API、统一结果模型和明确的失败响应。
- [ ] 从调用方网络验证最终 CDN 下载，并保留实况资源的配对关系。
- [ ] 补充可重复的测试、Docker 打包和部署说明。

## 许可

MediaLens 使用 [MIT License](LICENSE)。
