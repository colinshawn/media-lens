# 公网 IP 校验开关验证

验证日期：2026-11-01（Asia/Shanghai）。分支：`features/public-ip-validation-toggle`；归档变更：[add-public-ip-validation-toggle](../../openspec/changes/archive/2026-11-01-add-public-ip-validation-toggle/proposal.md)。

## 结果

| 检查 | 结果 |
| --- | --- |
| 解决方案构建 | 通过；0 警告、0 错误 |
| 本机完整测试 | 879 通过：MediaLens.Tests 293、MediaLens.Probe.Tests 586；0 失败、0 跳过 |
| 本次新增覆盖 | 配置测试 24 项，策略与 HTTP 测试新增 19 项，共 43 项，包含在完整测试中 |
| C# 格式检查 | `dotnet format --verify-no-changes` 通过 |
| 严格 OpenSpec 校验 | 7 份主规格及本变更全部通过 |
| Linux 生产镜像 | 构建通过，架构 arm64 |
| 生产镜像验证 | [82 项脱敏结果](public-ip-validation.json)全部通过 |

生产镜像：`media-lens:public-ip-validation`，标识：`sha256:55a7690024ad635930d124235f7331bc4e52eeed97d2c990e772e91088fa7270`。重建后的标识以新报告为准。

## 开关与配置边界

- `VALIDATE_PUBLIC_IP` 映射至 `MediaLens:Http:ValidatePublicIp`，兼容 `MediaLens__Http__ValidatePublicIp`；未设置时为 true。
- 覆盖缺省值、true/false 及大小写、空值、空格、0、1、yes 和合成非法字符串。
- 四种来源均覆盖：命令行 > 简写环境变量 > .NET 原生环境变量 > 配置文件 > true；有效高优先级值可覆盖非法低优先级值，非法选中值不回退。
- 非法配置在监听前退出，退出码为 1；日志仅包含固定的安全配置错误，不含原始非法字符串或合成密钥。
- 关闭时记录固定启动告警；启动后配置改变不修改已验证的快照，重新启动配置快照后恢复检查。
- Dockerfile 不为开关添加 ENV 默认值，避免遮蔽其他配置来源；`.env.example` 显式展示默认 true。

## HTTP 策略与回归范围

共享策略保留默认公网分类，覆盖受限 IPv4、IPv6、IPv4-mapped IPv6 和混合地址结果。明确关闭时允许这些映射，仍拒绝空地址、未允许域名、非 HTTP(S)、非默认端口及 URL 凭据。

两平台均使用生产 `PublicHttpTargetPolicy.ConnectAsync` 对合成本地 HTTP 上游建立真实 TCP 连接，确认关闭模式和地址映射副本生效。该测试单独验证连接回调；URI 范围由独立的双平台策略测试验证。

受控 `CookieFreeHttpClient` 测试覆盖元数据与最终 CDN 的重定向，断言各跳和两个平台继续不发送 Cookie、Authorization、Proxy-Authorization，Referer 保持各平台配置。关闭模式仍在连接前拒绝越界重定向。地址分类器本身及既有媒体、图集、实况、路由、鉴权测试继续通过。

## Docker 验证范围

[验证脚本](../../tools/validate-public-ip-validation.py)使用合成凭证，验证默认、开启、关闭、大小写、原生变量、冲突优先级、命令行覆盖及八种非法配置。有效配置下继续验证鉴权、双平台解析入口的非法输入拒绝、Cookie 拒绝、安全日志、非 root、只读根文件系统与无持久化写入；临时容器逐一清理。

生产容器检查只覆盖启动配置和 API 边界，不建立上游连接。真实本地连接与重定向由本机受控测试覆盖；本次未重新执行真实公网分享解析、CDN 下载或实际 TUN Fake-IP 路由验证。开关不保证 Fake-IP 地址可达。

## 复现

在已还原依赖的仓库根目录执行：

```sh
dotnet build MediaLens.slnx --no-restore
dotnet test MediaLens.slnx --no-build
dotnet format MediaLens.slnx --no-restore --verify-no-changes
docker build -t media-lens:public-ip-validation .
python3 tools/validate-public-ip-validation.py
OPENSPEC_TELEMETRY=0 openspec validate --all --strict --no-interactive
```

临时关闭时，向 API 运行环境传入 `VALIDATE_PUBLIC_IP=false` 并重启；恢复时传入 true 并重启。关闭后允许域名可能解析到环回、内网、保留或合成地址，部署方需控制诊断网络边界。
