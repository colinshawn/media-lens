## ADDED Requirements

### Requirement: 从分享文本选择首个受支持链接

统一入口 SHALL 从非空分享文本按出现顺序提取首个已注册且启用适配器支持的链接，首期仅抖音；指定平台入口 SHALL 选择文本中首个属于指定适配器的链接。系统 MUST 处理包围链接的中文标点及可识别的无协议前缀链接，不隐式批量解析；输入或平台约束不满足时在平台网络请求前明确失败。

#### Scenario: 文案包含短链和中文标点
- **WHEN** 分享文本包含说明、口令及被中文标点包围的抖音短链
- **THEN** 系统提取正确 URL，不把周围文案或标点作为 URL 的一部分

#### Scenario: 文本包含多个链接
- **WHEN** 文本包含一个不受支持的链接及两个受支持的抖音链接
- **THEN** 系统仅选择最先出现的受支持抖音链接，不发起批量解析

#### Scenario: 没有有效文本或 URL
- **WHEN** 文本为空、超出输入限制或不包含 URL
- **THEN** 系统返回 `INVALID_INPUT`，不发起平台请求

#### Scenario: 统一入口只有未支持平台链接
- **WHEN** 文本包含 URL，但没有已注册且启用适配器支持的链接
- **THEN** 系统返回 `PLATFORM_NOT_SUPPORTED`，不尝试未知平台请求

#### Scenario: 指定入口包含不同平台的链接
- **WHEN** 指定抖音平台的文本包含其他平台链接及一个抖音链接
- **THEN** 系统选择首个抖音链接；若没有抖音链接，则返回 `PLATFORM_MISMATCH`，不切换到其他适配器

### Requirement: 无 Cookie 地逐跳展开短链

系统 MUST 手动处理有限次数的 HTTP(S) 重定向，正确解析相对 `Location`，并在每一跳验证允许的平台/媒体目标与公开网络地址。全部跳转 SHALL 不发送 Cookie、不复用 `Set-Cookie`、不构造账号或游客身份；无法匿名展开时 MUST 明确失败。

#### Scenario: 相对地址与多跳短链
- **WHEN** 短链经过绝对或相对 `Location` 跳转到完整作品链接
- **THEN** 系统逐跳取得最终作品链接，且任一请求都不包含 Cookie

#### Scenario: 上游在跳转中设置 Cookie
- **WHEN** 首跳响应包含 `Set-Cookie` 并返回下一跳
- **THEN** 下一跳仍不发送 Cookie，且不建立会话或身份作为重试条件

#### Scenario: 越界或无法完成的跳转
- **WHEN** 跳转目标包含 URL 凭据、指向非公开地址/未允许域名，或出现循环、超限、无有效目标
- **THEN** 系统终止解析并返回明确错误，不访问被拒绝的目标

### Requirement: 识别作品 ID 并限制资源范围

系统 SHALL 识别受支持的 `/video/`、`/note/`、`/slides/` 及短链展开后的作品链接，并优先从 `modal_id`、`vid` 查询参数识别作品。作品 ID MUST 使用字符串。用户主页、合集、音乐、直播等非单个作品 SHALL 返回 `UNSUPPORTED_RESOURCE`。

#### Scenario: 主页路径上打开的作品
- **WHEN** 公开链接为用户主页路径并携带有效 `modal_id`
- **THEN** 系统选择该作品 ID，不把输入转为作者列表解析

#### Scenario: 图集与分享路径
- **WHEN** 完整链接或短链展开结果为受支持的视频/图集分享路径
- **THEN** 系统提取对应作品 ID，以作品信息响应确认最终媒体类型

#### Scenario: 不支持的资源与大整数 ID
- **WHEN** 链接仅指向作者主页、合集或其他非单作品资源
- **THEN** 系统返回 `UNSUPPORTED_RESOURCE`，不枚举其作品；任何已提取作品 ID 均不得转换为可能丢失精度的数值
