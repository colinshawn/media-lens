## MODIFIED Requirements

### Requirement: Bilibili 请求配置与最终 CDN 按平台隔离

系统 SHALL 复用受控 HTTP 机制但按平台隔离常规请求头与允许目标，逐跳执行域名、协议、端口、URL 凭据和固定连接地址校验。完整公网 IP 分类 MUST 默认开启，并使用与抖音相同的运行配置开关，仅在部署方明确关闭时跳过该分类。只有经验证的媒体目标及响应才可作为最终 CDN；MUST 保留上游完整 URL 与查询参数，不通过改写主机生成新候选。每个候选 SHALL 携带其完整安全下载头与自身元数据；元数据有界读取，媒体仅响应头，不落盘正文。

#### Scenario: 抖音与 Bilibili 并发请求
- **WHEN** 两个平台同时解析并继续各自的元数据/播放/CDN 跳转
- **THEN** Referer、Origin 和域名范围保持所属平台，不修改全局默认头，不泄漏 Cookie 或账号凭据

#### Scenario: 新域名、私网或 Fake-IP
- **WHEN** 公网 IP 校验开启，候选指向未验证 CDN、非默认端口，或 DNS 任一 IPv4/IPv6 为受限或合成地址
- **THEN** 拒绝该候选且不连接目标，不因匿名下载需求自动绕过公网检查

#### Scenario: 明确关闭公网 IP 校验
- **WHEN** 部署方通过有效运行配置关闭公网 IP 校验，Bilibili 元数据或媒体的允许域名解析到受限地址
- **THEN** 不因 IP 公网分类拒绝连接，域名、协议、端口、URL 凭据、最终 CDN、有限重定向和无 Cookie 规则继续生效

#### Scenario: 使用候选匹配的完整下载参数
- **WHEN** 返回两个完整媒体候选且它们需要不同常规请求头或具有不同技术信息
- **THEN** 每个来源返回自己的完整 headers 与元数据，未知字段为 null，调用方使用对应 url 和 headers 而非套用另一来源的值

#### Scenario: 跨平台 Location 保留语义
- **WHEN** Bilibili 短链返回指向已注册抖音平台的 Location
- **THEN** 在请求目标前通过内部可区分原因映射到 PLATFORM_MISMATCH，不泄漏 Cookie，不被通用上游异常捕获改报为 UPSTREAM_CHANGED
