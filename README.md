# 智能人机队友 (AiTeammate) — 杀戮尖塔 2 双人 AI 队友模组

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Game Version: v0.111.0+](https://img.shields.io/badge/StS2-v0.111.0+-brightgreen.svg)](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)]()

**AiTeammate (智能人机队友)** 是为《杀戮尖塔 2》（Slay the Spire 2）开发的本地双人同屏 AI 队友模组。你只需专注操作 1 号玩家（P1 人类），2 号玩家（P2 队友）将由高智能 AI 自动化协同接管全部流程，带给你原汁原味的“联机开黑”双人爬塔体验！

---

## 🌟 核心特性 (Key Features)

### 1. 本地双人 AI 协同模式
- **一键开启，无缝体验**：在游戏主菜单一键开启“AI队友模式”，自由挑选 P1 与 P2 角色职业（铁甲战士、静默猎手、故障机器人、死灵术士等）。
- **固定 P1 前台控制**：战斗内操作权、快捷键与视角始终锁定于 P1，P2 决策全部在后台异步推进，出牌与行动互不抢焦。

### 2. 战斗内高强度智能决策 (Powered by CombatSolver)
- **纯内存动态适配**：内置 `CombatSolverInMemoryPatcher`，在游戏启动时对原版创意工坊 `CombatSolver (0.50.1)` 自动进行 22 处纯内存 IL 补丁注入与 Harmony 双重保底，**完全无需修改磁盘上的任何第三方文件，零磁盘文件侵入**。
- **深度路径规划**：P2 手牌、能量、药水与目标推演完全接入蒙特卡洛/束搜索求解器，打出最优合牌与斩杀线。
- **智能重算节流与兜底**：包含后台异步预热求解、回合内增量重算节流、单牌快速决策和超时防卡死熔断机制。

### 3. 全局非战斗策略协同 (Powered by SpireStrategist)
- **非战斗全流程托管**：通过反射无侵入连接本地安装的 `SpireStrategist (0.6.72+)`，全面接管 P2 的全流程决策：
  - **涅奥开局**：自动评估并选取最适合 P2 角色构筑的初始祝福/诅咒。
  - **战后奖励**：后台静默领奖，三选一卡牌智能抓取，药水智能拾取（药水栏已满时基于战术价值智能替换）。
  - **随机事件**：依据当前牌组流派、血量与危险度，自动做出最稳健或收益最大的事件抉择。
  - **休息处（营火）**：根据全队危急度和卡牌升级权重，智能在睡觉回血、关键牌锻造/升级间权衡。
  - **商店采购**：智能删牌、购买关键核心卡、遗物评估与药水补给。
  - **Boss / 宝箱遗物**：深度结合全队遗物搭配，精准评估抓取高契合度遗物。
- **双人队伍协同感知**：P2 在评估策略时会实时感知 P1 血量危机程度、团队防御压力与队伍遗物去重。

### 4. 极致清爽的原生交互
- **P2 悬浮手牌实时预览**：战斗中鼠标悬停在左上角 P2 角色状态栏上方，即可展开原生美观的 **P2 手牌悬浮窗**，实时查看 P2 当前手牌、真实耗能、升级状态与完整动态描述。
- **快捷开关面板 (HUD Toggles)**：右上角内置半透明极简控制栏，支持热键一键切换界面显示：
  - `F7`：一键隐藏 / 显示 `CombatSolver` 运算分析面板。
  - `F8`：一键隐藏 / 显示本地多控辅助调试 UI。
  - 切换状态自动保存至配置文件，下次进游戏无需重复设置。

---

## 📦 前置依赖 (Prerequisites)

为了正常运行本模组，请确保游戏已订阅并安装以下前置项目（均可在 Steam 创意工坊或 GitHub 获得）：

| 依赖组件 | 推荐版本 | 说明与获取途径 |
| :--- | :--- | :--- |
| **[DualRoleAdventure](https://github.com/GuyGinat/STS2_DualRoleAdventure)** (双角色冒险 / 本地多控) | 最新版 | **必需**，提供单机双角色同屏基础会话与数据层支撑 |
| **[CombatSolver](https://steamcommunity.com/sharedfiles/filedetails/?id=3790899961)** (战斗求解器) | `0.50.1` | **必需**，驱动 P2 战斗出牌。[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3790899961) 原版即可，无需任何磁盘修改 |
| **[SpireStrategist](https://github.com/shaokangqian4-art/SpireStrategist/releases)** (全局策略助手) | `0.6.72+` | **强烈推荐**，驱动 P2 选牌、事件、商店、营火与遗物决策。[GitHub 仓库](https://github.com/shaokangqian4-art/SpireStrategist) |
| **[STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib/releases)** (基础运行库) | `0.6.0+` | `CombatSolver` 与 `SpireStrategist` 的底层依赖，创意工坊订阅即可 |

> **提示**：本模组遵循社区开源规范，仓库中**不包含、不重新分发**上述任何第三方的 DLL 二进制文件。

---

## 🚀 安装指南 (Installation)

1. **安装前置模组**：在 Steam 创意工坊订阅并启用上述前置模组。
2. **下载或放置模组文件**：
   - 前往本仓库 [Releases](../../releases) 页面下载最新发布的压缩包，或将本仓库 `mod/` 目录下的所有文件解压复制；
   - 放置到你的游戏根目录下：`<Slay the Spire 2 根目录>/mods/AiTeammate/`。
3. **确保目录结构如下**：
   ```
   Slay the Spire 2/
   └── mods/
       └── AiTeammate/
           ├── AiTeammate.dll
           ├── AiTeammate.json
           ├── card_scores.json
           └── aiteammate_settings.json
   ```
4. **启动游戏**：在游戏主菜单点击“AI队友模式”，选择双方角色后即可开始冒险！

---

## ⚙️ 个性化配置 (Configuration)

配置文件位于 `mods/AiTeammate/aiteammate_settings.json`，可随时修改自定义参数：

```json
{
  "PreferredTeammateCharacterIndex": 1,   // 默认 P2 角色索引
  "AiDecisionProfile": 1,                 // AI 决策激进/平衡预设
  "ActionDelaySeconds": 0.45,             // P2 动作间执行延迟（秒），使观感更自然
  "SearchTimeLimitSeconds": 2.5,          // CombatSolver 单回合搜索时间上限
  "SearchMaxExpandedNodes": 4096,         // 搜索树最大展开节点数
  "SolverMemoryBudgetMB": 4096,           // 求解器内存预算 (MB)
  "EnablePlannedTurnExecution": true,     // 启用完整规划下发
  "MaxCombatActionsPerTurn": 25,          // 每回合最大动作上限（防止死循环）
  "AutoAdvanceScreens": true,             // 战后自动结算与跳过非交互界面
  "AutoHandleEvents": true,               // P2 自动选择事件
  "AutoHandleRestSite": true,             // P2 自动篝火营火抉择
  "AutoHandleMerchant": true,             // P2 自动商店采购与删牌
  "EnableDebugVerbosity": false,          // 调试诊断日志输出开关
  "HideSolverUi": false,                  // 是否默认隐藏 CombatSolver UI (快捷键 F7)
  "HideMultiControlUi": false             // 是否默认隐藏多控 UI (快捷键 F8)
}
```

---

## 🛠️ 从源码构建 (Building from Source)

本项目采用兼容 .NET 9 与 .NET Framework 4.0 的 C# 5 语法编写，仓库已自带一键编译脚本：

### 构建步骤
1. 打开 PowerShell 终端，进入仓库根目录；
2. 执行构建脚本：
   ```powershell
   .\build.ps1
   ```
   *若游戏未安装在默认路径，可指定环境参数：`.\build.ps1 -Sts2Root "D:\Games\Slay the Spire 2"`*
3. 编译完成后，生成的 `AiTeammate.dll` 将自动输出至 `mod/AiTeammate.dll`。

---

## 📂 仓库目录结构 (Repository Structure)

```
AiTeammate/
├── src/                         # 核心 C# 源码
│   ├── AiTeammateMod.cs         # 模组主入口、协调器、内存补丁与 UI 组件
│   └── RewardBackendFlow.cs     # 奖励/事件/商店/营火后台全自动推进流
├── mod/                         # 发布产物与默认配置
│   ├── AiTeammate.dll           # 模组二进制编译文件
│   ├── AiTeammate.json          # 模组清单描述文件
│   ├── aiteammate_settings.json # 用户可配置设置项
│   └── card_scores.json         # 基础启发式卡牌权值评分库
├── docs/                        # 深度架构设计与补丁归档文档
│   ├── COMBATSOLVER_PATCHES.md  # CombatSolver 22 处内存补丁详细技术规范
│   └── apply_combatsolver_patches.py # 脱机补丁验证与复现工具
├── build.ps1                    # 一键构建 PowerShell 脚本
├── THIRD_PARTY.md               # 第三方开源项目归属与致谢声明
├── LICENSE                      # MIT 开源许可证
└── README.md                    # 本项目使用说明文档
```

---

## 🤝 致谢与开源协议 (Acknowledgments & License)

本项目采用 **[MIT License](LICENSE)** 开源许可证。

向以下优秀的开源项目作者表示由衷的感谢（详细说明见 [THIRD_PARTY.md](THIRD_PARTY.md)）：
- **GuyGinat** — [STS2_DualRoleAdventure](https://github.com/GuyGinat/STS2_DualRoleAdventure)
- **Torch (Torch1230)** — [CombatSolver](https://github.com/Torch1230/CombatSolver)
- **hotwords123** — [StS2.RandomForeseer](https://github.com/hotwords123/StS2.RandomForeseer)
- **穷仔 (shaokangqian4-art)** — [SpireStrategist](https://github.com/shaokangqian4-art/SpireStrategist)
