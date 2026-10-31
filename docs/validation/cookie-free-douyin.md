# 抖音严格匿名验证记录

## 当前结论

2026-10-31 的视频、纯静态图集、实况图集和混合图集均通过真实 Minimal API 解析。第一方 `api.amemv.com` 详情入口在不携带 Cookie、签名、账号、设备或游客标识时返回目标作品的完整数据。独立客户端使用 API 返回的 URL 和常规请求头取得所有必需资源，20 个文件经 ImageIO / AVFoundation 解码通过。

抖音生产注册表已启用，`video`、`imageAlbum`、`livePhoto`、`mixedAlbum` 均为 `supported`。当前状态依据下表中的样本和测试网络，不承诺所有公开作品、网络或未来平台版本持续可用；拒绝、空数据和协议变化仍返回明确错误。

| 实际类别 | 作品结构 | API / 原顺序与配对比对 | 独立客户端下载 | 系统解码 |
| --- | --- | --- | --- | --- |
| 视频 | 1 个 video 项 | 通过；统一、指定入口均 200 | 1 段完整 MP4 | 画面、声音通过 |
| 普通图集 | 12 个 image 项 | 通过，全部保持原顺序 | 12 张静态图片 | 全部通过 |
| 实况图集 | 1 个 livePhoto 项 | 通过，图片与短片保持同一项 | 1 张图片 + 1 段短片 | 全部通过 |
| 混合图集 | 3 个 image + 1 个 livePhoto 项 | 通过，顺序及短片归属一致 | 4 张图片 + 1 段短片 | 全部通过 |

最终批次共 17 张图片和 3 段视频。完整字节数与 API / CDN 响应头一致；可核对的图片尺寸和视频时长没有技术元数据差异。另一份最初标为普通图集的输入实测为 2 张静态图 + 2 个实况项，也已完成下载/解码；它不作为纯静态图集的通过依据，纯静态测试已使用补充样本完成。

[脱敏成功证据](2026-10-31-douyin-success.json)只保存类别、主机、状态、结构统计和验证结果。[最终容器证据](container-validation.json)记录 Linux 上四类真实解析、路由及文件系统检查。真实分享文本、分享 URL、作品 ID、完整重定向 URL、媒体 URL 和原始响应均未进入代码库；所有夹具为合成数据。

## 选定的匿名入口

1. 从分享文本识别链接；短链手动展开后精确取得作品 ID。已展开页面若有匹配作品的完整数据则直接复用。
2. 完整作品链接本地识别 ID 后直接请求 `https://api.amemv.com/aweme/v1/aweme/detail/`。Query 仅包含 `aweme_id` 及固定 `aid=6383`、`device_platform=web`、`version_code=190500`、`version_name=19.5.0`。应用编号和版本不是按访客创建的身份。
3. 从实际响应的 `aweme_id` / `awemeId` 匹配目标作品，按 `images` 数组保留原顺序及每个 `images[].video` 的归属；不使用图集顶层音乐作为实况短片。
4. 无详情时读取公开分享页及精选移动阅读页 `jingxuan.douyin.com/m/video/{workId}`。精选视频的 `_SSR_DATA` → `data.storeState.detail.videoData.result` 必须精确匹配 `gid`，再解析 JSON 字符串 `video_model`。独立 MP4 候选具有自己的尺寸、时长及明确的 `url_expire`；不运行脚本或拼接分离流。
5. 媒体只取得响应头，展开必要的播放跳转并确认最终 CDN。每个必需列表最多返回 2 个可用候选；最多 4 个媒体项并发，最终数组仍按输入顺序。候选失败可排除，必需部分全部失败则拒绝整次解析。

本轮发现入口的参考为[公开详情实现](https://github.com/jiuhunwl/short_videos/blob/main/api/douyin/DouyinParser.php)（MIT）及[精选阅读页实现](https://github.com/wuaishare/sharextract/blob/main/sharextract/extractors/douyin.py)（Apache-2.0）。本项目独立实现 C# 解析与边界验证，未引入这些项目的运行时、禁用 TLS 校验、媒体地址改写或第三方解析服务。

### 本地签名结论

当前详情与精选入口均已在不计算本地签名时取得真实媒体并成功下载。因此本次不需要移植 A-Bogus，也不存在客户端不透明签名计算服务。`ttwid`、UIFID、msToken、device_id、Cookie 容器、游客预热和浏览器均未引入。

这不表示平台的服务端实现可控；这些未经稳定协议承诺的公开入口仍可能变化。失效时返回匿名不可用、协议变化或暂时故障，不通过生成身份恢复访问。

## 验证环境和 Cookie 边界

- macOS arm64，.NET SDK 10.0.201 / runtime 10.0.5；真实 Kestrel 宿主使用正式路由和默认平台注册表，仅替换诊断用的公网 DNS 映射。
- 最终多阶段镜像在 Linux 上使用同一生产代码；四类实际解析均返回 200，混合图集通过指定平台入口验证。运行用户为 1654，根文件系统只读，`docker diff` 为零。
- `UseCookies=false`、`AllowAutoRedirect=false`、`UseProxy=false`。初始请求、后续跳转和不同调用均重新构造常规头，不解析或复用 `Set-Cookie`。
- 真实 C# 逐跳观测中 Cookie 请求头均缺席；TCP 替身另外从服务端验证处理器不会隐式添加 Cookie。API 拒绝入站 Cookie 和额外身份字段，响应也不转发 Set-Cookie。
- 每一跳检查允许域名、默认端口、URL 凭据和公网 DNS，并直接连接刚验证的地址；HTTPS 保留原主机 SNI 与证书校验。新增详情入口仅允许精确主机 `api.amemv.com`，不允许任意 `amemv.com` 子域。
- 元数据最多读取 2 MiB 解压后正文；每条跳转最多 5 次；总解析时限默认 30 秒，可配置 1—120 秒。
- 服务端只读取媒体响应头并立即释放，`MaxResponseDrainSize=0` 禁止为了复用连接而排空媒体正文；完整文件下载和解码属于仓库外的独立测试客户端。
- 下载头来自 User-Agent / Referer / Accept 允许列表，不包含 Cookie、Authorization 或账号凭据。不根据文件后缀猜 MIME；未知技术信息和无可靠依据的过期时间保留 null。

本机 TUN DNS 返回 `198.18.0.0/15` 合成地址，默认公网策略正确拒绝它。实际验证使用仓库外的真实公网 DNS 映射继续执行同一校验，Linux 容器通过临时 hosts 映射验证；没有关闭地址检查或把这些临时 IP 提交为生产配置。部署时应使用能解析真实公网地址的 DNS 环境。

## 公网地址分类补全

本轮后续补全了 IPv4 / IPv6 特殊用途地址分类，使用完整 IANA 快照及最长前缀匹配，加入映射地址和 NAT64 的内嵌 IPv4 检查。最新离线回归及规则维护依据见[公网目标地址分类](public-ip-classification.md)。下方首批 173 项验收数量保留为当时记录；当前完整回归为 670 项。

## 系统 DNS 重测

2026-10-31 用户调整 Fake-IP 排除后，使用系统 DNS、正式原生 Kestrel 宿主及默认 Docker 网络重新验证，没有传入 DNS 覆盖或 hosts 映射。`api.amemv.com` 已返回真实公网地址，正式 .NET HTTP 组件取得 HTTP 200 和约 90 KiB 元数据；短链、分享页和当前图片/视频 CDN 仍返回 198.18.*。系统地址解析、直接 DNS 查询和 .NET 进程结果一致。

四类原始分享均在首次连接前被目标策略拒绝，原生 API 及默认 Docker 的四类请求均返回 503 / UPSTREAM_UNAVAILABLE。直接作品链接也未取得可返回的 CDN 候选，本轮没有进入独立客户端下载。这说明当前默认网络尚未完成兼容配置，不改变此前外置公网映射下的媒体成功记录，也不把本轮网络失败推断为需要 Cookie。

需让 `douyin.com`、`iesdouyin.com`、`douyinvod.com`、`douyinpic.com` 及对应子域返回真实公网 IP。可能是排除规则覆盖或代理/DNS 缓存尚未完全生效，单凭这些结果不判断配置原因。本次没有修改系统网络配置或放宽地址检查。[脱敏重测记录](2026-10-31-system-dns-recheck.json)。

## 补充排除后的复测

2026-10-31 再次增加域名排除后，直接 DNS 查询已对全部 8 个检查主机返回公网地址，但 .NET / macOS 系统解析结果仍存在旧 Fake-IP 差异。普通用户缓存刷新已执行；管理员级刷新因 sudo 要求系统密码未完成，没有修改系统 DNS、代理规则或项目安全检查。

| 运行路径 | 四类原始分享 | 四类直接作品链接 | 完整资源验证 |
| --- | --- | --- | --- |
| 本机默认网络 | 503，短链主机仍出现 Fake-IP | 全部 HTTP 200 | 本轮完整下载使用下方容器结果 |
| Docker 默认 DNS | 全部 503 | 未单独验证 | 未进入下载 |
| 临时 Docker，显式 `--dns 114.114.114.114` | 全部 HTTP 200 | 原始分享链路已通过 | 20 个必需资源全部下载并解码 |

显式 DNS 容器使用的是本机当前配置的同一 DNS 服务器；没有 hosts、固定媒体 IP 或外部地址映射。独立客户端下载也直接使用系统 DNS 和返回的安全头部，17 张图片、3 段视频均通过完整字节和系统解码检查。新容器为非 root、只读文件系统，写入变更为零，测试后已删除。

因此已验证正常 DNS 服务器路径下的完整匿名解析与下载；默认本机/容器解析链路仍需消除缓存或解析差异后再确认，不能将显式 DNS 容器的成功描述为默认配置全部恢复。[本轮脱敏记录](2026-10-31-system-dns-recheck-follow-up.json)。

## CDN 候选与请求头核验

2026-10-31 对同一视频的两个 CDN 候选进行独立客户端核验。使用每个候选返回的完整 headers 时，两者均返回 HTTP 200，完整文件下载成功且 SHA-256 一致；系统读取的尺寸和时长一致，两份视频均可解码首帧。全程未发送 Cookie，文件和完整结果仅保存在仓库外。

仅移除 Referer、保持其他请求头不变时，首个候选返回 403，第二个仍为 200。因此下载参数是每个候选的 url 与 headers；浏览器地址栏单独打开 URL 的结果不能替代使用完整下载参数的验证。同一列表是同一逻辑媒体的备用来源，本次相同文件的结果不表示其他作品的候选必然具有相同格式、尺寸或码率。

## 历史失败记录

[2026-10-30 初始观察](2026-10-30-douyin-anonymous.json)中的 20 次检查和[后续补测](2026-10-30-follow-up.json)中的头部、入口及本地签名检查均未取得目标媒体：

| 入口或方式 | 当时结果 |
| --- | --- |
| 移动分享页 / 桌面作品页 | 200，但只有空 loader 或页面壳 |
| Web 详情接口及本地 A-Bogus | 403，提示缺少 UIFID |
| 固定 x-tt-argus 头及常规参数 | 200，正文为零字节 |
| 旧 iteminfo 入口 | 200，空正文 |

这些入口的失败仍是有效历史证据，不能据此推断所有匿名路线失败或下载必需 Cookie。后来验证成功的是不同的匿名详情入口。早期记录中按提供者标注的样本类别未全部独立核实；当前成功矩阵已依据实际媒体结构修正。

## 构建、测试与复测

```sh
dotnet restore MediaLens.slnx
dotnet build MediaLens.slnx --no-restore --configuration Release
dotnet test MediaLens.slnx --no-build --configuration Release
dotnet format MediaLens.slnx --no-restore --verify-no-changes
```

173 项离线测试通过：70 项验证工具/传输回归，103 项媒体、契约、并发与真实 Kestrel API 验证。覆盖 Cookie 隔离、目标和 DNS 拒绝、媒体正文不读取、最终 CDN 判定、取消与总时限、错误形状、顺序/配对、源字段/null/过期规则、日志与报告脱敏、第二个平台替身。它们不包含真实分享输入。

真实复测输入必须存放在仓库外，只接受 `{ "kind": "video", "text": "公开分享文本" }` 数组，类别可为 video / imageAlbum / livePhoto / mixedAlbum；不接受额外身份字段。

```sh
dotnet run --project tools/MediaLens.Probe -- \
  --input /absolute/path/outside-repo/samples.private.json --entry adapter
```

`adapter` 执行与生产共用的解析器，报告不回显输入、作品 ID、媒体 URL 或原始响应。share / mobile / desktop / detail / legacy 模式保留用于单入口诊断，不能把旧入口失败当成当前适配器失败。报告需要保存时只写仓库外。

若系统 DNS 为代理合成地址，可另提供仓库外的真实公网 DNS JSON，通过 `--dns-overrides /absolute/path/outside-repo/dns.private.json` 传入；任一空数组或私网/保留地址仍被拒绝。此参数不关闭网络安全检查，也不是固定 IP 的部署建议。

退出码 0 仅表示本批次完整媒体地址观察通过，独立客户端下载仍需额外执行；1 表示至少一项未通过，2 表示输入不合法。相册权限、格式转换、原生实况封装及系统相册导入仍由调用方实现。
