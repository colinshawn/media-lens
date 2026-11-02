# 严格无 Cookie Bilibili 解析规范

## Purpose

规定 Bilibili 公开 BV、AV 和短链视频的严格匿名解析边界，核对所选分 P 的作品归属，验证必要的本地 WBI 签名，并仅返回保留原有音频的完整单段 MP4 和独立下载候选。

## Requirements

### Requirement: Bilibili 的每一跳保持严格无 Cookie 和无身份构造

Bilibili 的元数据、公开键、短链、播放与 CDN 请求 MUST 不接收、生成、存储或发送 Cookie，不转发调用方身份字段，不复用 Set-Cookie。系统 SHALL 不生成 SESSDATA、bili_jct、buvid、设备指纹、dm_img 参数或浏览器会话来恢复访问；常规请求头和公开参数上的本地签名不构成账号身份。

#### Scenario: nav 或短链设置 Cookie
- **WHEN** 公开键或短链响应包含 Set-Cookie，随后发生跳转、播放请求或另一次调用
- **THEN** 后续全部请求仍不携带 Cookie，不建立或持久化游客身份

#### Scenario: 上游要求建立身份
- **WHEN** 选中的匿名入口明确要求登录、Cookie、游客或设备身份才能取得完整数据
- **THEN** 返回 COOKIE_FREE_UNAVAILABLE，不请求身份生成、浏览器或远程 Token 服务回退

### Requirement: 匹配请求作品及所选分 P 的元数据

系统 SHALL 从可信详情中取得规范 BV、AID 和 pages，核对请求的 BV 或 AV/AID，并按照 p（默认 1）选择对应 CID。标识 MUST 保持字符串且 BV 主体大小写不变；输入提供的 cid、w_rid、wts 或追踪参数 MUST 不被用作可信作品/播放参数。结果 webUrl SHALL 明确标识所选 p，不以其他稿件、推荐项或其他分 P 补齐失败结果。

#### Scenario: AV 输入规范化
- **WHEN** AV 完整链接的详情返回匹配 AID 和规范 BV
- **THEN** 以后续可信 BV 及所选 CID 解析，workId 返回规范 BV，webUrl 保留所选 p

#### Scenario: 多 P 明确选择
- **WHEN** 作品包含多个 pages 且输入指定有效 p=2
- **THEN** 只请求第二页对应的 CID，返回一个 video 项，其时长取该 P 的播放数据而非整稿总时长

#### Scenario: 身份或页码不匹配
- **WHEN** 元数据返回另一个作品、页缺少 CID，或 p 超出实际页数
- **THEN** 作品/结构不匹配返回 UPSTREAM_CHANGED，页码越界返回 INVALID_INPUT，不回到第 1 P 或推荐作品

#### Scenario: 同稿不同 P 的标识与字段兼容
- **WHEN** 同一 BV 分别解析默认 P 和有效的另一个 P
- **THEN** workId 继续标识同一稿件，webUrl 的 p 与各自的候选、时长和文件名一致，不增加请求体 p 或将多个 P 混入备用列表

### Requirement: 必要 WBI 签名可在本地独立验证

若匿名入口需要 WBI，系统 SHALL 无 Cookie 地从第一方 nav 响应读取公开键材料，在本地以确定的键混合、参数排序、字符处理、编码与时间规则计算签名。签名覆盖的 UTF-8 Query MUST 与实际请求字节一致；公开键只允许进程内有界复用，刷新和请求共享取消及总时限。系统 MUST 不调用第三方黑盒签名服务、不生成访客参数、不把公开键永久写死或落盘。

#### Scenario: 固定输入复现与真实请求一致
- **WHEN** 使用合成公开键、固定 Unix 秒和包含空格、中文及保留字符的固定参数签名
- **THEN** 规范化 Query 与 w_rid 匹配独立参考向量，受控上游观察到的请求字节与被签名内容一致

#### Scenario: nav 表示未登录但包含有效公开键
- **WHEN** nav 的业务码表示未登录，同时 wbi_img 结构与键材料有效
- **THEN** 仅使用已验证的公开材料计算本地签名，不把该响应当作登录凭据或启动登录流程

#### Scenario: 键刷新失败或重复签名失败
- **WHEN** 没有有效公开键，或有明确签名失效依据后的一次刷新仍失败
- **THEN** 按网络/协议证据明确失败，不无限刷新，不使用身份回退，也不超出同一解析时限

### Requirement: 只将完整 progressive MP4 作为成功视频候选

首期 Bilibili 成功结果 MUST 来自已确认格式的单段 progressive MP4，覆盖所选分 P 且保留其原有音频。主 URL 与 backup_url SHALL 是该完整资源的备用来源。只有 DASH 音视频分离、多个连续 durl、FLV/HLS、预览或其他需合并结构时 MUST 明确失败，不能返回第一段、无声轨道或部分成功。

#### Scenario: 完整单段 MP4 及备用地址
- **WHEN** 上游确认 MP4 且 durl 仅一项，包含可用主地址及备份地址
- **THEN** 按主地址和备份优先级验证，首个完整 MP4 来源通过后立即停止探测，返回一个 video 项及该来源

#### Scenario: 只有 DASH 或多个连续分段
- **WHEN** 播放响应只有 DASH video/audio，或 durl 含多个连续片段
- **THEN** 返回 UNSUPPORTED_RESOURCE，不将轨道或连续片段放入互为备用的 videoSources，不进行服务端合并

#### Scenario: 付费预览或明显时长截断
- **WHEN** 作品或播放数据明确标记受限预览，或文件播放时长明显不能覆盖所选 P
- **THEN** 拒绝该结果，不把可下载预览当成完整作品成功

### Requirement: Bilibili 请求配置与最终 CDN 按平台隔离

系统 SHALL 复用受控 HTTP 机制但按平台隔离常规请求头与允许目标，逐跳执行域名、协议、端口、URL 凭据和固定连接地址校验。完整公网 IP 分类 MUST 默认开启，并使用与抖音相同的运行配置开关，仅在部署方明确关闭时跳过该分类。只有经验证的媒体目标及响应才可作为最终 CDN；MUST 保留上游完整 URL 与查询参数，不通过改写主机生成新候选。每个候选 SHALL 携带其完整安全下载头与自身元数据；元数据有界读取，媒体仅响应头，不落盘正文。

共享 HTTP 机制 SHALL 与抖音一致，使用双栈错峰连接及 DNS/TCP、TLS、响应头、元数据正文的独立阶段预算；所有阶段及候选回退 MUST 服从同一解析总时限，并在成功或取消时释放落选连接。

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
- **WHEN** 主候选失败后，另一个完整媒体候选通过验证且具有自己的技术信息
- **THEN** 返回成功来源自己的完整 headers 与元数据，未知字段为 null，不套用失败来源的参数或继续探测其他备用地址

#### Scenario: 跨平台 Location 保留语义
- **WHEN** Bilibili 短链返回指向已注册抖音平台的 Location
- **THEN** 在请求目标前通过内部可区分原因映射到 PLATFORM_MISMATCH，不泄漏 Cookie，不被通用上游异常捕获改报为 UPSTREAM_CHANGED

### Requirement: Bilibili 匿名失败沿用准确的通用错误契约

系统 SHALL 沿用 requestId/error 和既有稳定错误码，按明确证据区分资源范围、身份要求、完整性、内容不可用、协议变化、暂时故障与总超时。普通 HTTP 403/412、CDN 拒绝或未知业务码 MUST 不被直接解释为作品删除、登录要求或签名已失效；不得以失败结果冒充成功媒体。

#### Scenario: 媒体候选均失效
- **WHEN** 已确认完整 MP4 但所有候选在无 Cookie 验证时不可用，且无暂时网络故障证据
- **THEN** 返回 MEDIA_INCOMPLETE，不返回空 videoSources 的成功项

#### Scenario: 作品明确不可用与结构未知
- **WHEN** 可信响应明确表示目标作品删除/不可访问，或另一个响应结构无法识别
- **THEN** 分别返回 CONTENT_UNAVAILABLE 或 UPSTREAM_CHANGED，不暴露原始上游响应或内部异常

#### Scenario: 网络暂时故障与总超时
- **WHEN** 上游发生明确限流/网络暂时故障，或公开键/元数据/CDN 操作耗尽总时限
- **THEN** 分别返回 UPSTREAM_UNAVAILABLE 或 RESOLUTION_TIMEOUT，并保留通用 retryable 与 retryAfterSeconds 语义

### Requirement: Bilibili 生产支持须通过严格匿名及完整下载验收

系统 MUST 在真实 BV、AV、b23.tv、默认分 P 及非首 P 样本通过无 Cookie 元数据、完整 CDN 解析和独立客户端下载后才生产注册和启用 Bilibili。验证 SHALL 包含选定 P 归属、完整字节、可解码画面和源有音频时的音轨；仅有链接、200 响应或 video/mp4 MIME 不算完整支持证据。未通过时 MUST 保留成功验收任务未完成，不改变抖音支持状态。

#### Scenario: 匿名和完整下载全部通过
- **WHEN** 所要求的真实链接类型和分 P 验证通过，且独立客户端按返回头取得完整、可播放的所选视频
- **THEN** 才能将 Bilibili 的 video 标记 supported 并启用，其他三种媒体能力标记 unsupported

#### Scenario: 替身通过但仅真实 DASH 可得
- **WHEN** 离线测试通过，真实匿名入口却只返回需要合并的轨道、试看或未取得完整文件
- **THEN** 记录支持限制并保留生产未启用及验收未完成，不以替身或降级文件宣称本提案完成

### Requirement: Bilibili 真实样本与签名请求不进入代码库

真实分享文本、BV/AID/CID、完整重定向或 CDN URL、签名请求 Query 和原始响应 MUST 只在仓库外使用。仓库 SHALL 只保留合成夹具、固定合成签名向量和无样本标识的类别/主机/状态/结构统计；报告、日志、文档和提交记录均不得回显真实输入或身份材料。

#### Scenario: 运行平台真实验证
- **WHEN** 验证工具读取仓库外输入并执行 Bilibili 适配器或正式 API 验证
- **THEN** 输出仅含脱敏步骤、主机、状态、Cookie 缺席及结构统计，不输出作品标识、完整地址、签名 Query 或原始响应

#### Scenario: 保存协议回归
- **WHEN** 新增 BV/AV、分 P、WBI 或播放结构测试
- **THEN** 使用合成输入和本地上游，公开真实样本不固化到测试、规格示例或仓库夹具
