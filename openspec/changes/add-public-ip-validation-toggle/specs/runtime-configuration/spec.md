## ADDED Requirements

### Requirement: 公网 IP 校验可由运行配置临时关闭

系统 SHALL 支持 `VALIDATE_PUBLIC_IP` 环境变量，映射至 `MediaLens:Http:ValidatePublicIp`，并兼容 `MediaLens__Http__ValidatePublicIp`。缺省值 MUST 为 true，布尔配置 SHALL 接受可由 bool.TryParse 解析的 true/false。针对同一个内部键，优先级 MUST 为命令行、简写变量、原生环境变量、配置文件、true；已选中的空值或非法值 MUST 在监听前以安全配置错误和退出码 1 结束，不回退或回显原值。系统 MUST 在启动时取得开关快照并同时应用于抖音、Bilibili 的全部上游连接；修改配置 SHALL 在重启或重建后生效。关闭时 MUST 记录固定启动告警，文档 MUST 说明允许域名可能解析到环回、内网、保留或合成地址，恢复 true 并重启即可重新开启；不得从 API 请求中读取该开关。

#### Scenario: 默认或明确开启校验
- **WHEN** 所有来源均未设置开关，或最终配置为 true
- **THEN** 启动时采用公网 IP 校验，继续拒绝受限地址，不记录关闭告警

#### Scenario: 环境变量临时关闭校验
- **WHEN** 提供有效密钥和 VALIDATE_PUBLIC_IP=false，没有更高优先级覆盖
- **THEN** 两个平台的 HTTP 连接策略均跳过 IP 公网分类，记录关闭告警，鉴权、其他目标限制和无 Cookie 规则继续生效

#### Scenario: 非法配置不回退
- **WHEN** 最终开关配置为空、0、1、yes 或其他非布尔内容，即使低优先级配置有效
- **THEN** 进程在监听前退出并仅报告安全的配置项错误，不记录原始输入

#### Scenario: 多配置来源冲突
- **WHEN** 配置文件、原生变量和简写变量存在冲突，或命令行覆盖了非法简写变量
- **THEN** 使用同键最高优先级值，选中的合法值决定是否校验，低优先级值不影响启动

#### Scenario: 恢复检查需要重启
- **WHEN** 部署方将有效开关从 false 改为 true 并重启服务
- **THEN** 新进程重新拒绝受限地址，修改配置本身不会热切换旧进程的策略
