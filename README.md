<div align="center">

# Yui to Issho! — Farm Days Together

**会陪你生活、回应你，并在劳动时自然搭把手的 Stardew Valley 农场伙伴。**

<p>
  <img alt="Version 0.31.0" src="https://img.shields.io/badge/version-0.31.0-ec8fb4?style=flat-square">
  <img alt="Stardew Valley 1.6.15" src="https://img.shields.io/badge/Stardew%20Valley-1.6.15-79a95d?style=flat-square">
  <img alt="SMAPI 4.5.2" src="https://img.shields.io/badge/SMAPI-4.5.2%2B-d08b45?style=flat-square">
  <img alt="MIT License" src="https://img.shields.io/badge/license-MIT-6f86d6?style=flat-square">
</p>

陪伴 · 交谈 · 共同劳动 · 持久身份

</div>

## 关于 Yui

Yui 是生活在游戏世界中的农场伙伴，而不是一键清空农场的自动化菜单。她拥有持续保存的身份、身体、工具、物品与体力状态，会跟随玩家、进行交谈，并在玩家真正开始劳动时参与附近工作。

核心体验很简单：

```text
遇见 Yui → 一起移动或交谈 → 玩家开始劳动
         → Yui 加入工作 → 完成、停下或反馈困难
```

> [!NOTE]
> 当前版本为 `0.31.0`。Release 构建已经通过；实验功能仍建议先备份存档再启用。

## 核心体验

| 能力 | 表现 |
|---|---|
| 持久伙伴 | 每位玩家拥有唯一的 Yui；召回、换图和读档不会更换身份 |
| 陪伴移动 | 支持召唤、跟随、等待、停止与重聚 |
| 自然表达 | 动作键打开统一互动菜单，可交谈、拥抱、送礼、陪坐，并使用原版对话框、世界气泡与爱心反馈 |
| 独立亲密度 | 交谈、每日亲昵与送礼会积累 10 心亲密度，不污染原版村民、任务或婚姻数据 |
| 共同劳动 | 玩家真实挥斧或挥镰后，Yui 使用自己的工具协助附近工作 |
| 状态边界 | 缺少工具、体力不足、目标失效或保存时不会产生假成功 |
| 多人协作 | Host 负责验证世界动作，客户端展示确认后的结果 |

Yui 的默认行为强调“一起做”。仅仅拿着工具不会触发工作；玩家停止劳动后，她会完成当前安全步骤并回到陪伴状态。

## 快速开始

### 环境要求

- Stardew Valley `1.6.15`
- SMAPI `4.5.2` 或兼容的 4.x 版本
- .NET 6 SDK（仅自行构建时需要）

### 安装

1. 安装 Stardew Valley 与 SMAPI。
2. 下载模组包，或按照下方命令自行构建。
3. 将完整的 `YuiToIssho` 模组文件夹放入游戏的 `Mods` 目录。
4. 通过 SMAPI 启动游戏并载入存档。

首次载入时，默认配置会创建并召唤 Yui。之后可以使用简短的 `yui` 控制台命令切换她的状态。

### 自行构建

在 Git Bash 中运行：

```bash
dotnet build YuiToIssho.csproj -c Release -p:EnableModDeploy=false
```

生成的模组压缩包位于 `bin/Release/net6.0/`。`EnableModDeploy` 默认关闭，因此构建不会自动修改游戏的 `Mods` 目录。

## 常用命令

| 命令 | 作用 |
|---|---|
| `yui` 或 `yui help` | 显示简洁帮助 |
| `yui status` | 查看 Yui 当前状态 |
| `yui summon` | 召唤 Yui |
| `yui dismiss` | 让 Yui 暂时离开 |
| `yui follow` | 让 Yui 跟随玩家 |
| `yui stay` | 让 Yui 原地等待 |
| `yui sit` | 让 Yui 寻找附近空椅子或秋千坐下 |
| `yui stand` | 让 Yui 起身并释放座位 |
| `yui hug` | 在 Yui 身边拥抱或摸摸她 |
| `yui assist on` | 开启共同劳动等待状态 |
| `yui assist status` | 查看共同劳动状态 |
| `yui assist off` | 停止共同劳动 |

开启实验功能后，可通过 `yui help advanced` 查看扩展命令组。

## 默认功能与实验功能

默认启用的体验包括首次相遇、跟随、交谈、自然共同劳动、座椅休闲以及必要的状态反馈。Follow 模式下玩家坐下时，Yui 会尝试寻找附近空位陪坐；也可以使用 `yui sit` 和 `yui stand` 主动控制。

靠近 Yui 后，按键盘或手柄的动作键（鼠标也可右键）会打开统一互动菜单，可明确选择交谈、拥抱、送礼、坐下或起身。交谈和亲昵每天首次增加亲密度；礼物有 Yui 自己的五档偏好，并遵循每天一件、每周两件的限制。当前心数会显示在社交页与 `yui status` 中。Yui 还会根据亲密度、季节、天气、地点、时间和当前工作改变对话，并在每天起床、首次靠近、雨天、回家和睡前作出一次自然反应。

以下能力仍属于实验范围，需通过 `EnableExperimentalFeatures` 显式开启：

- 指挥光标与范围工作；
- 播种、制作、递送与授权储物；
- 钓鱼、战斗与动物照料；
- F7 制作菜单和 F9 播种菜单；
- Agent Gateway 提交的扩展动作。

诊断面板和 Agent Gateway 分别由自己的配置项控制，不会随实验功能自动开启。

## 配置

首次运行后可在模组目录的 `config.json` 中调整设置。

| 配置项 | 默认值 | 作用 |
|---|:---:|---|
| `AutoSummonOnFirstLoad` | `true` | 首次载入时创建并召唤 Yui |
| `EnableNaturalWorkAssist` | `true` | Follow 状态下响应玩家真实挥斧或挥镰 |
| `EnableExperimentalFeatures` | `false` | 开启扩展工作、制作、播种与指挥输入 |
| `EnableDiagnostics` | `false` | 开启 F8 开发诊断面板 |
| `EnableNekoBridge` | `false` | 开启仅供本机使用的 Agent Gateway |

输入按键也可以通过 `config.json` 调整，包括指挥模式、第二角点、确认/取消、制作菜单和播种菜单。

## 设计原则

- Yui 使用自己持有的工具、物品、生命和体力。
- 世界动作在提交前重新验证，并通过操作回执避免重复执行。
- 保存、日切、召回、断线和目标失效会取消或暂停未完成动作。
- 物品、制作、播种、交付与储物流程保留明确的责任状态。
- 基础陪伴与本地反馈不依赖远程模型服务。

## 源码结构

项目保持单程序集部署，源码按职责分区：

```text
src/
├── Domain/          身份、体征、工作指令、回执与持久化模型
├── Companions/      伙伴注册、身体绑定、跟随、亲密度与体征协调
├── Runtime/         Agent 感知、计划、调度与任务执行
├── Work/            工作策略、动作注册与工作执行器
├── Inventory/       背包、储物、制作、播种与交付
├── Multiplayer/     Host 权威、消息协议与客户端投影
├── Presentation/    外观、对话、菜单、诊断与世界交互
├── Commands/        控制台命令与指挥输入
├── Integrations/    可选本机集成
├── Patches/         Harmony 接入点
├── Hosting/         组合根、配置与生命周期管线
└── Entry/           SMAPI 入口
```

工作执行器由注册表路由，更新和取消通过统一运行时模块广播。持久化模型按业务域拆分；当前使用 `schema-v11`，并会在载入时按 `v9 → v10 → v11` 自动迁移现有存档。

## 许可证

本项目采用 [MIT License](LICENSE)。
