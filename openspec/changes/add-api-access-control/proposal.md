## Why

MediaLens 当前三个 API 入口可直接调用，部署到可访问的网络后，缺少区分受信任调用方的低成本访问控制。需要通过共享 secret token 限制调用资格，并提供统一、易于容器部署的环境变量，避免漏配密钥时意外开放接口。

## What Changes

- **BREAKING**：三个现有 API 入口统一要求 `Authorization: Bearer <token>`；通过现有前置 HTTP 检查后，缺少、格式错误或不匹配的凭证返回 `401`，不进入 JSON 绑定或平台解析。
- **BREAKING**：应用启动前必须取得长度为 `32～256` 个 ASCII 字符（包含边界）、符合 Bearer 字符语法的有效 secret token；未配置、长度越界、非 ASCII 或其他格式错误时明确报错并以非零状态退出，不使用内置密钥、不自动生成密钥、不因漏配而关闭鉴权。
- 长度按实际传输的 token 本体计数，不包含 `Bearer ` 前缀，不先解码、不裁剪或截断。到达鉴权层的长度或字符不合法请求在计算摘要前返回统一 `401 / UNAUTHORIZED`；长度限制不替代安全随机生成密钥的要求。
- 对外统一使用全大写、单下划线环境变量：`SECRET_TOKEN` 配置访问密钥，`HTTP_PORT` 配置容器内 HTTP 监听端口；镜像默认端口保持 `8080`。
- 密钥保留 .NET 配置入口 `MediaLens:Auth:SecretToken`；端口环境变量统一为 `HTTP_PORT`，Dockerfile 直接设置 `ENV HTTP_PORT=8080`，不增加其他端口环境变量的兼容读取。保留显式 URL/端点监听方式，明确配置优先级、无效值处理及 Docker 宿主机端口映射边界。
- 鉴权失败沿用 `requestId + error` 响应结构，新增稳定错误码 `UNAUTHORIZED` 与 Bearer challenge；凭证只在 API 宿主内校验，不进入上游请求、媒体下载头、响应正文或日志。
- 补充真实 HTTP 宿主和启动配置用例、Docker 冒烟验证及中英文部署说明；明确 `31、32、256、257` 字符的配置与请求边界验证，以及非 ASCII、前缀不计数和不裁剪/截断用例；保持抖音与 Bilibili 的严格无 Cookie 解析、分享输入与媒体响应契约。

非目标：用户登录、JWT 签发、权限角色、多调用方密钥管理、请求签名、防重放、IP 白名单、客户端来源证明、自动密钥生成或持久化、Secret 文件读取、应用内 TLS 证书管理，以及独立限流策略。限流可在反向代理配置或后续独立变更中增加；共享 token 本身只证明持有凭证，不证明请求来自指定网站或 App。

## Capabilities

### New Capabilities

- `api-access-control`：定义共享 Bearer token 的校验范围、`32～256` 个 ASCII 字符限制、摘要计算前的拒绝行为、凭证隔离与部署安全边界。
- `runtime-configuration`：定义 `SECRET_TOKEN`、`HTTP_PORT` 的对外配置契约、密钥的 .NET 兼容入口、密钥长度及格式启动校验和 Docker 端口映射。

### Modified Capabilities

- `media-resolution-api`：为现有 Minimal API 增加鉴权前提及统一 `401 / UNAUTHORIZED` 错误语义，保持成功 DTO、请求正文和平台路由不变。

## Impact

- 影响 `src/MediaLens.Api` 的启动配置、请求管线、统一错误输出及路由组元数据；解析库不负责读取或传递本服务访问凭证。
- 更新 `tests/MediaLens.Tests` 的 HTTP 宿主配置和鉴权用例；现有端点测试必须显式提供合成测试密钥，继续覆盖授权后的双平台行为。
- 更新 Docker 部署示例、中英文 README 与必要的环境变量示例；镜像不得写入密钥，宿主机端口仍通过 Docker `-p` 或 Compose `ports` 发布。
- 不新增数据库、Redis、浏览器、第三方鉴权服务或运行时依赖包。现有无凭证调用方需要同步配置请求头，现有容器需要在升级时注入密钥。
- 实现与验证依据见[访问控制验证记录](../../../docs/validation/api-access-control.md)。本机、Linux 受控回归与生产镜像冒烟均使用合成输入；真实公网分享解析及 CDN 下载证据保持独立，本次未重新执行。
