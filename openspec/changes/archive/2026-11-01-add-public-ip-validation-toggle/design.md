## Context

`PublicHttpTargetPolicy` 在诊断地址映射及实际 `ConnectAsync` 中都拒绝非公网地址，连接时固定已解析的 IP。API 为抖音和 Bilibili 分别构造策略；`RuntimeConfiguration` 已支持容器友好的环境变量映射及命令行覆盖。

## Goals / Non-Goals

**Goals:**

- 部署方可用 `VALIDATE_PUBLIC_IP=false` 临时关闭 API 的公网 IP 分类，默认仍开启。
- 两个平台及每次新连接采用同一个启动配置快照；非法配置在监听前以安全错误退出。
- 关闭模式仍保留平台域名、HTTP(S)、默认端口、URL 凭据、重定向次数和严格无 Cookie 限制。

**Non-Goals:**

- 不增加请求级绕过、热更新、代理配置、自动探测或自动降级。
- 不修改地址分类快照，不扩大允许的平台域名，不改变媒体结果和 API 鉴权。
- 不增加 Probe 命令行开关，不进行真实公网分享或 CDN 下载验证。

## Decisions

### 配置名称与优先级

`VALIDATE_PUBLIC_IP` 映射到 `MediaLens:Http:ValidatePublicIp`，兼容 .NET 环境变量 `MediaLens__Http__ValidatePublicIp`。未设置时为 `true`；接受可由 `bool.TryParse` 解析的 true/false 值，不接受空值、0、1 或 yes。优先级为命令行 > 简写变量 > 原生环境变量 > 配置文件 > true；非法的最终值不回退。Dockerfile 不为开关设置 ENV 默认值，以免遮蔽配置文件或原生变量。

在 API 启动校验中保存配置快照，两个平台的单例 HTTP 策略使用该值。关闭时记录固定启动告警，不记录密钥或配置原值。恢复 `true` 并重启即可重新开启检查。

### 有限范围的策略开关

为共享策略构造函数增加默认值为 true 的 `validatePublicIp` 参数，在地址映射和实际 DNS 结果的两处检查中共用该值。始终拒绝空地址结果；关闭时仍对解析出的具体 IP 建连，不改为使用域名再次解析，也不启用系统代理。既有调用方省略参数时行为不变。

不替换整个目标策略，因为那会同时移除域名、协议或端口等边界。地址分类器本身保持原有分类结果。

### 验证方式

策略测试覆盖默认限制、关闭后的 IPv4/IPv6 及混合地址映射、真实本地 TCP 连接、空结果和 URI 范围。受控 HTTP 测试覆盖关闭模式下的重定向、元数据和媒体请求，继续断言无 Cookie/Authorization。API 子进程测试覆盖默认值、true/false、错误输入、配置优先级及启动告警；生产 Docker 镜像验证环境变量和安全退出，不请求真实平台。

## Risks / Trade-offs

- 关闭后允许域名可能解析到本机、内网或保留地址 → 默认开启，关闭时告警，文档明确用途和恢复方法；部署方负责该诊断环境的网络边界。
- Fake-IP 是否可连接依赖部署环境的实际路由 → 该开关仅移除分类拒绝，不承诺网络可达性。
- 进程运行期间修改配置不会切换已建单例策略 → 明确要求重启或重建服务，不提供热更新。

## Migration Plan

现有部署无需设置新变量；可按需临时传入 `VALIDATE_PUBLIC_IP=false`，验证后设回 `true` 并重启。同步 `.env.example`、Dockerfile 注释和中英文部署说明，不写入真实密钥或改变端口约定。
