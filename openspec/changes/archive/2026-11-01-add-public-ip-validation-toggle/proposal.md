## Why

共享 HTTP 目标策略目前始终拒绝非公网 IP，使用受控 TUN Fake-IP 或诊断网络时无法临时调整。需要一个默认开启、可通过环境变量明确关闭的开关，并保留其余请求边界。

## What Changes

- 新增 `VALIDATE_PUBLIC_IP`，映射至 `MediaLens:Http:ValidatePublicIp`，默认 `true`；`false` 临时关闭 API 的公网 IP 分类检查，重启生效。
- 公网检查开关同时应用于抖音、Bilibili 的元数据、短链和媒体连接；域名、协议、端口、URL 凭据、重定向及无 Cookie 规则保持生效。
- 开关在监听前校验；非法值安全退出，关闭时输出明确的启动告警。沿用命令行、环境变量和配置文件优先级。
- 补充受控连接、配置边界和 Docker 验证，更新环境变量示例及中英文 README。

## Capabilities

### New Capabilities

无。

### Modified Capabilities

- `runtime-configuration`：新增公网 IP 校验开关的名称、默认值、来源优先级、校验和生效方式。
- `share-text-resolution`：公网地址分类默认开启，允许通过受控运行配置临时关闭，保留 DNS 结果固定连接及其他目标限制。
- `cookie-free-bilibili-resolution`：Bilibili 复用相同开关，并保留平台隔离和严格无 Cookie 约束。

## Impact

修改 API 运行配置和两个平台的 HTTP 策略构造、Core 的地址检查分支、相关测试、Dockerfile 注释、`.env.example` 及 README；不新增依赖或 HTTP 请求字段。共享策略构造函数新增默认开启的可选参数，既有调用方保持默认行为；本次不增加 Probe 命令行开关。

关闭公网检查后，允许域名解析到的环回、内网或保留地址也可能被访问。该模式面向部署方控制的临时诊断，不承诺修复任意代理或 Fake-IP 路由问题，也不代表新增真实平台下载验证。

实现与验证依据见[公网 IP 校验记录](../../../../docs/validation/public-ip-validation.md)，测试和 Docker 检查仅使用合成输入。
