# 智能人机队友 (AiTeammate) — 杀戮尖塔2 双人 AI 队友 Mod

**Slay the Spire 2 two-player AI teammate mod.** 你操作 P1，P2 由 AI 自动完成全部流程。

> 状态：私有开发中。已测试游戏版本 **v0.111.0**（Windows）。游戏更新后需要重新适配。

## 功能 / Features

P2（AI 队友）会自动处理：

- **战斗**：调用 CombatSolver 求解出牌路线，求解失败时有保底出牌（贪心）和强制结束回合兜底。
- **选牌**：卡牌奖励、战斗内选牌（弃牌/消耗/药水三选一等）、事件变牌/升级/删牌。
- **地图**：自动跟随 P1 的路线投票。
- **事件 / 篝火 / 先古之民（Neow）**：自动选择选项。
- **宝箱**：自动选遗物。
- **商店**：删牌、买遗物、买卡牌、买药水（带价值评估与黑名单）。
- **前台控制**：战斗中前台固定为 P1，P2 的牌不会出现在你的手牌里。

设置项见 `mod/aiteammate_settings.json`（自动跟路线、自动选牌、自动事件、自动篝火、自动先古、商店删牌、固定 P1 控制等开关）。

## 依赖 / Dependencies

| Mod | 说明 |
|---|---|
| DualRoleAdventure（双角色冒险 / 本地多控） | **必需**，`AiTeammate.json` 中声明的依赖，提供 P2 角色 |
| CombatSolver 0.50.1（Torch，MIT） | **必需**，P2 战斗出牌，通过反射调用。当前使用本地修补版（补丁说明待补充） |
| STS2-RitsuLib ≥ 0.6.0 | CombatSolver 的依赖（测试版本 0.6.9） |
| SpireStrategist 0.6.22（MIT） | 商店遗物价值评估 |

本仓库**不包含**以上任何 mod 或游戏文件，请从各自来源获取。详见 [THIRD_PARTY.md](THIRD_PARTY.md)。

## 安装 / Install

1. 安装上面的依赖 mod。
2. 将 `AiTeammate.dll`（见 Releases）和 `mod/` 目录里的文件放到 `<游戏目录>/mods/AiTeammate/`。
3. 启动游戏，开启双人本地模式。

## 目录结构

```
src/    C# 源码（AiTeammateMod.cs, RewardBackendFlow.cs）
mod/    mod 清单、默认设置、卡牌评分表
docs/   开发记录（HANDOFF.md）
```

## 构建 / Build

使用 .NET Framework 4.0 的 `csc.exe` 手动编译（C# 5 语法），引用游戏的 `sts2.dll`、Harmony 以及依赖 mod 的 DLL。构建脚本待补充。

## 已知问题 / Known issues

- 依赖游戏内部方法的 Harmony 补丁，游戏更新后可能失效；启动时日志中的 `INIT-FAIL` 行会指出哪些补丁失败。
- 部分第三方 mod（如 STS2RitsuLib 的右键功能）在 P2 取消出牌时可能报空指针，已在后续版本中规避。

## 开发记录

每次修复的指令与验证记录见 [docs/HANDOFF.md](docs/HANDOFF.md)。

## 许可证

待定。公开发布前请先确认依赖 mod 的许可证。