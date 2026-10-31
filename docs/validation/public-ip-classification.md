# 公网目标地址分类

## 数据来源与判断范围

规则覆盖 2026-10-31 获取的以下 IANA 注册表全部条目；两张表的页面更新日期均为 2025-10-09。

| 注册表 | 原始记录数 | 拆分后的前缀数 | CSV SHA-256 |
| --- | --- | --- | --- |
| [IPv4 Special-Purpose Address Space](https://www.iana.org/assignments/iana-ipv4-special-registry/) | 25 | 26 | `cbc4b7653c8bc640ba1e8e5d4ebbf11e7c7a1d867e7f1593aad001d3f8632aac` |
| [IPv6 Special-Purpose Address Space](https://www.iana.org/assignments/iana-ipv6-special-registry/) | 25 | 25 | `bf56772aef97fbf9b4a27c98b8511f2bb1ba96da4b9ccc12ab203a2496faba66` |

IPv4 的一条记录同时列出 `192.0.0.170/32`、`192.0.0.171/32`，所以拆分后多一个前缀。测试快照位于 `tests/MediaLens.Probe.Tests/Fixtures/iana-special-purpose-addresses.json`，保留来源、摘要、获取日期以及原始 Destination、Forwardable、Globally Reachable、Termination Date 字段。

这是一份随代码发布的固定快照。运行时不下载注册表、不查询 IP 归属地服务，不引入数据库或其他网络依赖。地址分类表示是否允许作为 HTTP 出站目标，不保证实际网络可达性、地址已分配或对方内容可信。

## 规则

1. 带非零接口区域编号的 IPv6 拒绝，避免目标地址依赖本机接口；IPv4-mapped IPv6 转为对应 IPv4 后分类。
2. 特殊用途表采用最长前缀匹配，与条目排列顺序无关。只有 Destination、Forwardable、Globally Reachable 均明确为 True 且未终止的条目允许；False、N/A、空值或已废弃条目拒绝。
3. 保留可全球路由的具体例外，例如 `192.0.0.9/32`、`192.0.0.10/32`、`2001:1::1/128`、`2001:3::/32`；它们不会被拒绝的父网段覆盖。
4. IPv4 多播 `224.0.0.0/4` 另外拒绝；该空间由独立注册表管理，不在特殊用途表中。其他未命中表的 IPv4 使用普通单播默认规则。
5. IPv6 未命中表时仅允许全球单播范围 `2000::/3`。因此多播、旧式 site-local、未指定地址、未分配的其他地址空间等不能因遗漏特殊条目而放行。
6. `64:ff9b::/96` 标准 NAT64 前缀虽然标记为可全球路由，仍需检查内嵌 IPv4，不能借此访问回环、私网、链路本地、云元数据或测试网段。这符合 [RFC 6052 §3.1](https://www.rfc-editor.org/rfc/rfc6052.html#section-3.1)。网络专用 NAT64 前缀 `64:ff9b:1::/48` 按注册表拒绝；Teredo 和 6to4 的 Globally Reachable 为 N/A，同样拒绝。

共享 HTTP 策略仍先检查平台/CDN 域名、协议、端口及 URL 凭据。构造外置 DNS 映射和建立新连接均使用同一分类器；DNS 数组为空或任意地址被拒绝，则整个目标拒绝。连接时直接使用已验证的 IP，保持原 TLS 主机校验。

本轮补齐了此前遗漏的 `192.88.99.0/24`、`2001::/23` 及其子网、`2001:2::/48`、`3fff::/20`、新 Dummy IPv6、SRv6、转换等条目。TUN 常用的 `198.18.0.0/15` 继续被拒绝；本次没有增加绕过开关或修改系统代理/DNS 配置。

自定义网络转换前缀、隧道与本机路由不能仅凭地址字节推断；部署网络应按自身边界配置出站访问。未知的后续 IPv6 分配和注册表更新需经过同样的快照复核后发布。

## 离线验证

- 独立测试快照覆盖所有 51 个前缀。对每个前缀检查首地址、末地址、内部地址、两侧相邻地址，并根据所有更具体条目确定预期结果。
- 测试预期用无符号大整数区间比较，既不读取生产规则数组，也不调用生产使用的 `IPNetwork.Contains`。
- IPv4 边界用例同时验证 IPv4-mapped 和标准 NAT64 表示，确保内嵌受限地址不能绕过检查。
- 明确回归向量覆盖公共子网例外、过渡机制 N/A、废弃网段、新 IPv6 文档/基准测试地址、多播、区域编号及 IPv4-compatible 旧格式。
- DNS 测试覆盖双栈、公开与受限地址混合、空数组及允许的特殊用途公共地址；不访问外部网络。

2026-10-31 本轮完整回归：670 项通过，其中验证工具/HTTP/地址规则项目 567 项，API/媒体项目 103 项。较此前 173 项新增 497 项离线回归；真实分享文本、作品 ID 和媒体 URL 均未用于这些测试。

```sh
dotnet test MediaLens.slnx --configuration Release
dotnet format MediaLens.slnx --no-restore --verify-no-changes
```

Linux 多阶段发布构建通过；包含本次分类器的非 root、只读容器又对视频、12 张静态图集、实况和混合图集分别解析，四类均返回 HTTP 200，文件系统变更为零。使用仓库外的临时公网 DNS 映射，真实输入及完整响应仍未入库。当前镜像与逐类统计见[容器验证记录](container-validation.json)。

## 更新流程

1. 从上表官方页面下载当前 CSV，复核新增、修改、撤销的条目及脚注，不只读取范围名称。
2. 更新快照的日期、原始字段和摘要，以及 `PublicIpAddressClassifier` 的全部规则。保留最长前缀语义，对非明确 True 的状态继续拒绝。
3. 更新快照条目数量断言和变更所需的明确回归向量。内嵌 IPv4、接口区域编号、多播及 IPv6 默认范围属于额外出站策略，不能在注册表同步时删掉。
4. 运行完整离线测试、格式与 OpenSpec 校验；核对文档日期和适用范围后发布。未验证的注册表更改不会自动进入生产。
