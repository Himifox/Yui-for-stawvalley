<div align="center">

# Yui to Issho! — Farm Days Together
<img width="463" height="355" alt="image" src="https://github.com/user-attachments/assets/56db3509-5323-4236-8b60-4a137abf9021" />

**陪你生活、交谈，并在劳动时搭把手的《星露谷物语》农场伙伴。**

<p>
  <img alt="Version 0.31.0" src="https://img.shields.io/badge/version-0.31.0-ec8fb4?style=flat-square">
  <img alt="Stardew Valley 1.6.15" src="https://img.shields.io/badge/Stardew%20Valley-1.6.15-79a95d?style=flat-square">
  <img alt="SMAPI 4.5.2" src="https://img.shields.io/badge/SMAPI-4.5.2%2B-d08b45?style=flat-square">
  <img alt="MIT License" src="https://img.shields.io/badge/license-MIT-6f86d6?style=flat-square">
</p>

</div>

Yui 拥有持续保存的身份、工具、物品与体力状态，支持跟随、交谈、拥抱、送礼和陪坐。基础陪伴与本地反馈不依赖远程模型服务。

- **日常陪伴**：根据亲密度、季节、天气和场景作出对话与反应。
- **独立亲密度**：10 心关系系统，不影响原版村民、任务和婚姻数据。
- **共同劳动**：跟随时，在玩家真实挥斧或挥镰后使用自己的工具协助附近工作；玩家停工后回到陪伴状态。
- **多人支持**：由 Host 验证世界动作；保存、断线或目标失效时取消或暂停未完成动作。

当前版本为 `0.31.0`。启用实验功能前建议备份存档。

## 安装与使用

需要 Stardew Valley `1.6.15` 和 SMAPI `4.5.2`（或兼容的 4.x 版本）。

1. 安装 SMAPI，从 [Releases](https://github.com/Himifox/Yui-for-stawvalley/releases) 下载模组包，或自行构建。
2. 将完整的 `YuiToIssho` 文件夹放入游戏的 `Mods` 目录。
3. 通过 SMAPI 启动游戏并载入存档，默认首次载入会创建并召唤 Yui。

靠近 Yui 后，按动作键或鼠标右键打开互动菜单，可交谈、拥抱、送礼、坐下或起身。交谈和亲昵每天首次增加亲密度，送礼每天限一件、每周限两件；心数可在社交页或 `yui status` 查看。

跟随时，Yui 会在玩家坐下后寻找附近空位陪坐。

## 常用命令

在 SMAPI 控制台中输入：

| 命令 | 作用 |
|---|---|
| `yui` / `yui help` | 查看帮助 |
| `yui status` | 查看状态与亲密度 |
| `yui summon` / `yui dismiss` | 召唤 / 暂时离开 |
| `yui follow` / `yui stay` | 跟随 / 原地等待 |
| `yui sit` / `yui stand` | 寻找座位坐下 / 起身 |
| `yui hug` | 在身边拥抱或摸摸她 |
| `yui assist on` / `yui assist off` / `yui assist status` | 开启 / 关闭 / 查看共同劳动状态 |

## 配置与实验功能

首次运行后，在模组目录的 `config.json` 中调整设置和输入按键。

| 配置项 | 默认值 | 作用 |
|---|:---:|---|
| `AutoSummonOnFirstLoad` | `true` | 首次载入时创建并召唤 Yui |
| `EnableNaturalWorkAssist` | `true` | 跟随时响应玩家挥斧或挥镰 |
| `EnableExperimentalFeatures` | `false` | 开启扩展工作与指挥输入 |
| `EnableDiagnostics` | `false` | 开启 F8 诊断面板 |
| `EnableNekoBridge` | `false` | 开启仅供本机使用的 Agent Gateway |

实验功能包括范围指挥、播种、制作、递送、授权储物、钓鱼、战斗、动物照料，以及 F7 制作菜单、F9 播种菜单和 Agent Gateway 扩展动作。开启后使用 `yui help advanced` 查看命令；诊断面板与 Agent Gateway 仍需分别启用。

## 自行构建

需要 .NET 6 SDK 和 Node.js。在已安装游戏与 SMAPI 的环境中，用 Git Bash 运行：

```bash
node scripts/validate-release.mjs
dotnet build YuiToIssho.csproj -c Release -p:EnableModDeploy=false
node scripts/validate-release.mjs --artifact
```

压缩包输出至 `bin/Release/net6.0/`。构建默认不自动部署到游戏目录。

## 许可证

[MIT License](LICENSE)
