# API 访问控制验证

验证日期：2026-11-01（Asia/Shanghai）。变更：`add-api-access-control`，分支：`features/api-access-control`。

归档提案：[API 访问控制与运行配置](../../openspec/changes/archive/2026-11-01-add-api-access-control/proposal.md)。

## 结果

| 检查 | 结果 |
| --- | --- |
| .NET SDK | 本机 10.0.201 |
| 依赖还原、解决方案构建 | 通过；构建 0 警告、0 错误 |
| 本机完整测试 | 836 通过：MediaLens.Tests 269、MediaLens.Probe.Tests 567；0 失败、0 跳过 |
| Linux SDK 容器完整测试 | 相同 836 项通过；0 失败、0 跳过 |
| 新增测试 | SecretTokenTests 35、ApiAccessControlTests 12、RuntimeConfigurationTests 47，共 94 项，包含在完整测试中 |
| C# 格式检查 | `dotnet format --verify-no-changes` 通过 |
| Linux 生产镜像 | 构建通过，架构 arm64 |
| 生产镜像验证 | [51 项脱敏结果](api-access-control.json)全部通过 |

生产镜像标识：`sha256:f6ab7c94e8e0e03e40ab91ac86969b4f4ce59a363058439c117278435790b519`。镜像标签为 `media-lens:local`；重新构建时标识可能变化，以新生成的 JSON 报告为准。

## 长度边界与凭证校验

长度仅统计实际配置或传输的 token 本体，包含上下限，不计 `Bearer ` 前缀，不进行 Base64 解码、裁剪、截断或大小写转换。除长度外继续检查 ASCII 和 Bearer 字符语法；满足长度不代表随机强度或凭证内容匹配。

| 合成 token 长度 | 作为最终配置 | 作为到达鉴权层的请求凭证 |
| --- | --- | --- |
| 31 | 监听前失败，退出码 1 | 401 / UNAUTHORIZED |
| 32 | 允许启动 | 完全匹配时平台发现 200；不匹配时 401 |
| 256 | 允许启动 | 完全匹配时平台发现 200；不匹配时 401 |
| 257 | 监听前失败，退出码 1 | 401 / UNAUTHORIZED，不截断后接受 |

- 四种配置入口均覆盖上述边界：`SECRET_TOKEN`、`MediaLens__Auth__SecretToken`、配置文件、命令行。
- 覆盖缺失、空字符串、全空格、非 ASCII、控制字符、非法填充、大小写、错误认证方案、重复 Authorization 头和多重凭证。
- 配置来源覆盖命令行 > 简写变量 > 原生变量 > 配置文件；选中的非法值不会回退，有效高优先级值可以覆盖非法的低优先级值。
- 密钥更换用真实子进程重启验证：重启后旧密钥被拒绝，新密钥有效；每个配置用例使用独立环境和临时目录。
- [SecretToken](../../src/MediaLens.Api/SecretToken.cs) 先执行长度和字符检查，再编码、散列与固定时间摘要比较；顺序由源码确认，测试不以时间差推断固定时间特性。

## HTTP 与无 Cookie 回归

两个解析入口和平台发现统一鉴权。无凭证、错误凭证及越界凭证返回统一 401、Bearer challenge 和 `requestId + error`；未授权的非法 JSON 不先进入绑定。有效凭证后的输入、平台、上游错误、取消、DTO 和逐候选媒体语义继续走原有处理。

现有 Cookie 拒绝、413、404、405 保留。受控上游覆盖抖音、Bilibili 的授权解析与两个 POST 路由；多跳重定向和后续请求均没有本服务 Authorization、Proxy-Authorization 或 Cookie。成功响应和捕获日志不包含密钥或其摘要；图集顺序、混合项、实况配对和完整独立候选继续由完整回归覆盖。

生产镜像的 POST 冒烟使用无效合成分享文本，正确凭证下预期为 `400 / INVALID_INPUT`；成功媒体响应由本机与 Linux SDK 容器的受控上游测试覆盖。本次未重新执行真实公网分享解析或 CDN 下载，既有真实平台证据保持独立。

## Docker 范围

Dockerfile 直接设置 `ENV HTTP_PORT=8080`，端口环境变量统一使用 `HTTP_PORT`，应用不再添加其他端口环境变量的兼容读取。开发用 `Properties/launchSettings.json` 已排除出构建上下文。更新后重新构建镜像并复验以下 51 项检查，全部通过，包含对镜像默认 HTTP_PORT 的直接检查。

[验证脚本](../../tools/validate-api-access-control.py)仅使用合成凭证，检查：

- 缺失、空值、31/257 字符、非 ASCII 密钥及非法端口均启动失败；非法密钥别名不回退至合法原生值。
- 32/256 字符密钥、三条受保护路由的请求边界、授权平台发现、两个平台的显式解析路由输入拒绝及 Cookie 拒绝。
- 镜像默认 HTTP_PORT=8080、运行时覆盖 HTTP_PORT、命令行密钥和端口优先级及显式 URL 监听方式。
- 宿主机回环端口发布、非 root 用户、只读根文件系统及无根文件系统持久化写入。
- 日志同时捕获标准输出和标准错误，断言不含合成密钥或其摘要；临时容器在用例结束时清理。

Linux SDK 回归使用本机构建的托管测试程序集，将仓库只读挂载到同一绝对路径后在 Linux 执行。生产镜像则由 Dockerfile 中的 Linux SDK 独立编译、发布，两类证据分别记录，不将 SDK 测试容器当作生产运行镜像。

## 实现期间解决的验证问题

- 缺失密钥原先通过未捕获异常退出，容器未及时结束；现由入口记录固定的安全配置错误并返回退出码 1，生产镜像负向用例已复验。
- Linux 回归暴露测试宿主在启动后修改动态端口地址集合的时序问题。夹具改为在启动前使用 Kestrel 选项绑定回环动态端口，仍使用真实 TCP；参见 [WebApplicationFactory 10.0.5 源码](https://github.com/dotnet/aspnetcore/blob/v10.0.5/src/Mvc/Mvc.Testing/src/WebApplicationFactory.cs)。
- 超限正文可能使服务器关闭连接，相关测试使用独立客户端，避免后续路由断言复用该连接；保留全部状态和错误契约断言。

## 复现

在仓库根目录执行：

```sh
dotnet restore MediaLens.slnx
dotnet build MediaLens.slnx --no-restore
dotnet test MediaLens.slnx --no-build
dotnet format MediaLens.slnx --no-restore --verify-no-changes
docker build -t media-lens:local .
python3 tools/validate-api-access-control.py
openspec validate --all --strict --no-interactive
```

完整 Linux SDK 回归（先完成本机构建）：

```sh
medialens_repo_dir="$(pwd)"
docker run --rm --read-only --tmpfs /tmp:rw,nosuid,size=256m \
  -e DOTNET_CLI_HOME=/tmp/dotnet -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -v "${medialens_repo_dir}:${medialens_repo_dir}:ro" -w "${medialens_repo_dir}" \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet vstest \
  tests/MediaLens.Tests/bin/Debug/net10.0/MediaLens.Tests.dll \
  tests/MediaLens.Probe.Tests/bin/Debug/net10.0/MediaLens.Probe.Tests.dll \
  --ResultsDirectory:/tmp/TestResults --logger:console
```

真实 `.env`、分享链接、作品 ID、CDN URL 和实际部署密钥不进入本验证记录；示例与夹具仅使用合成输入。
