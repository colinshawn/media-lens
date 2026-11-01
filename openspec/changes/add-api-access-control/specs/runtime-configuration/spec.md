## ADDED Requirements

### Requirement: 提供统一的对外环境变量名称

系统 SHALL 显式支持大写、单下划线的 `SECRET_TOKEN` 与 `HTTP_PORT` 环境变量，分别映射至 `MediaLens:Auth:SecretToken` 与框架 `HTTP_PORTS` 配置键。系统 MUST 保留 .NET 层次环境变量 `MediaLens__Auth__SecretToken` 和原生监听配置的兼容入口；文档 MUST 说明这些简写名称为 MediaLens 的约定，需要程序映射，不是 Docker 自动解释的配置。

#### Scenario: 仅使用对外名称启动
- **WHEN** 运行环境提供合法的 SECRET_TOKEN 和 HTTP_PORT，没有提供同项命令行覆盖
- **THEN** 程序使用该密钥保护 API，并通过 HTTP_PORTS 监听所选容器内端口

#### Scenario: 使用 .NET 原生兼容入口
- **WHEN** 未设置简写变量，通过 MediaLens__Auth__SecretToken 提供有效密钥并使用原生监听配置
- **THEN** 程序仍可启动并正确鉴权，不要求同时重复提供简写变量

### Requirement: 相同配置键具有确定的来源优先级

针对同一个内部配置键，系统 MUST 按命令行、对外环境变量别名、.NET 原生环境变量、配置文件、既有默认值的顺序选取配置。未设置的别名 SHALL 不覆盖其他来源；最终选中的空值或非法值 MUST 明确失败，不静默回退至较低来源。别名映射 MUST 不覆盖命令行中已明确配置的同一内部键。

#### Scenario: 别名与原生密钥环境变量冲突
- **WHEN** SECRET_TOKEN 与 MediaLens__Auth__SecretToken 同时存在且为不同合法值，没有更高优先级配置
- **THEN** 程序使用 SECRET_TOKEN，原生变量中的另一密钥不能通过鉴权

#### Scenario: 命令行明确覆盖密钥
- **WHEN** 命令行指定 MediaLens:Auth:SecretToken，且环境变量提供不同的合法密钥
- **THEN** 程序只使用命令行对应的内部配置键值，不被后添加的别名映射覆盖

#### Scenario: 别名为空而低优先级配置有效
- **WHEN** SECRET_TOKEN 被设置为空或全空格，没有更高优先级配置，但配置文件或原生环境变量有合法密钥
- **THEN** 程序在启动时失败，不将已设置的空值等同于变量不存在并回退

#### Scenario: 端口别名覆盖镜像默认环境
- **WHEN** 镜像继承 ASPNETCORE_HTTP_PORTS=8080，运行时设置 HTTP_PORT=9000，且没有更高优先级监听配置
- **THEN** 程序监听容器端口 9000，镜像内置值不能遮蔽别名

### Requirement: 无有效密钥时在监听之前失败

系统 MUST 在开始接收 HTTP 请求之前验证有效密钥。最终选中的密钥 MUST 为 `32～256` 个 ASCII 字符（包含边界），并符合 Bearer token 字符语法；此约束 MUST 同样适用于 SECRET_TOKEN、原生环境变量、配置文件和命令行入口。长度 SHALL 按配置字符串本体计算，不包含 Bearer 前缀、不按解码后字节数计算，MUST 不通过裁剪、截断或大小写转换使输入合规。配置缺失、长度越界、非 ASCII 或其他格式不合法时，进程 MUST 输出只包含配置项名称与安全原因的错误，并在监听及计算密钥摘要之前以非零状态退出；MUST 不提供内置密钥、不自动生成密钥、不在日志打印密钥，不因漏配而自动关闭鉴权。长度校验 SHALL 不替代部署方使用安全随机源生成密钥。

#### Scenario: Docker 完全漏配密钥
- **WHEN** 容器启动时所有支持的配置来源均没有有效密钥
- **THEN** 应用以非零状态退出，未开放 HTTP API，日志明确指出需要配置 SECRET_TOKEN 或兼容配置键

#### Scenario: 密钥包含非法空白或控制字符
- **WHEN** 最终选中的密钥无法作为合法 Bearer token 传输
- **THEN** 应用启动失败，错误不包含原值，不对密钥静默裁剪后继续运行

#### Scenario: 密钥配置位于两个合法边界
- **WHEN** 分别通过受支持的配置入口提供语法合法的 32 和 256 字符 ASCII 密钥，且其他启动配置有效
- **THEN** 两种配置均允许启动，并可使用对应完全一致的 token 完成鉴权，不把生成前的随机字节数当作字符数

#### Scenario: 密钥配置位于边界之外
- **WHEN** 最终选中的密钥分别为语法合法的 31 或 257 个 ASCII 字符，即使较低优先级来源存在合法密钥
- **THEN** 两种配置均在监听及计算密钥摘要前非零退出，不回退、不裁剪或截断，错误和日志不包含配置原值

#### Scenario: 字符数合规的非 ASCII 配置
- **WHEN** 最终选中的密钥字符数在范围内，但包含非 ASCII 字符
- **THEN** 应用在监听前非零退出，不因字符数满足要求而接受该密钥

#### Scenario: 重启使用相同部署配置
- **WHEN** 容器重启或重建，部署方继续注入相同有效密钥
- **THEN** 仍接受相同调用凭证，不依赖可写文件系统或自动生成结果

### Requirement: 端口别名只表达单个有效 HTTP 端口

被选中的 HTTP_PORT 别名 MUST 是 `1` 至 `65535` 的单个十进制整数，不接受零、负数、小数、空字符串、空格、URL 或多端口列表；非法值 MUST 在监听前导致非零退出，不回退至 8080。未设置别名时 SHALL 保留原生监听配置；Docker 镜像 MUST 继续以 ASPNETCORE_HTTP_PORTS=8080 提供默认容器端口。

#### Scenario: 自定义容器内端口
- **WHEN** 提供有效密钥和 HTTP_PORT=9000，且没有更高优先级监听配置
- **THEN** 实际 HTTP 进程在容器内端口 9000 接收请求，不自动继续监听默认 8080

#### Scenario: 端口越界或格式错误
- **WHEN** 被选中的 HTTP_PORT 为 0、65536、负数、小数、空值、URL 或分号分隔列表
- **THEN** 进程非零退出并报告端口配置错误，不继续监听任意默认端口

#### Scenario: Docker 使用默认端口
- **WHEN** 有效密钥已配置，但没有提供 HTTP_PORT 或其他监听覆盖
- **THEN** 镜像保留容器内 HTTP 8080 的默认监听行为

### Requirement: 保留框架 URL 与显式端点优先级

系统 SHALL 将 HTTP_PORT 映射为 HTTP_PORTS，MUST 不无条件改写 ASPNETCORE_URLS、命令行 --urls 或显式 Kestrel 端点。跨配置键的监听优先级 MUST 保留框架规则，URL 或显式端点配置可以优先于端口模式。原生端口列表 SHALL 继续使用框架语义，不受单端口别名语法限制。

#### Scenario: 命令行 URL 覆盖端口模式
- **WHEN** 提供有效密钥、合法 HTTP_PORT 和显式 --urls 监听地址
- **THEN** 实际进程按框架优先级监听 --urls 指定的地址和端口，不被别名强制覆盖

#### Scenario: 原生环境 URL 与端口别名并存
- **WHEN** 提供有效密钥、ASPNETCORE_URLS 和合法 HTTP_PORT，且没有更高优先级端点配置
- **THEN** 框架 URL 配置优先于端口列表，文档说明该优先级而不宣称别名覆盖全部监听方式

#### Scenario: 原生端口列表
- **WHEN** 未设置 HTTP_PORT，通过 ASPNETCORE_HTTP_PORTS 提供多个合法端口
- **THEN** 原生列表按框架规则生效，不被别名的单端口规则误判为非法

### Requirement: Docker 监听与宿主机发布保持独立

部署说明 MUST 区分应用监听端口、Dockerfile EXPOSE 与宿主机端口发布。HTTP_PORT SHALL 只配置进程监听，不自动修改 Docker -p、Compose ports 或镜像 EXPOSE；改变容器端口时部署方 MUST 同步调整端口发布的容器目标端口。密钥 MUST 在运行时注入，镜像不得内置真实值，运行不要求新增可写卷。

#### Scenario: 自定义端口通过宿主机访问
- **WHEN** 容器设置 HTTP_PORT=9000，并发布 127.0.0.1:8080:9000
- **THEN** 携带正确凭证的请求可通过宿主机 8080 访问容器 9000，变量本身不会创建或改写发布规则

#### Scenario: 只读容器正常运行
- **WHEN** 通过环境配置提供有效密钥与监听端口，并沿用现有只读容器运行方式
- **THEN** 鉴权与端口配置正常工作，不生成密钥文件、不要求媒体卷或数据库

#### Scenario: 部署文档说明 HTTPS 和配置边界
- **WHEN** 调用方按中英文部署说明配置公开访问或 Compose 环境变量
- **THEN** 文档要求公网入口使用 HTTPS，说明 Compose 插值与容器环境的区别，并明确共享 token 和 CORS 均不构成指定网站或 App 的来源证明
