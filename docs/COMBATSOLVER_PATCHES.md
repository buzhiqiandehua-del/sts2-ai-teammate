# CombatSolver-0.50.1-Quark 二进制 IL 补丁归档说明书

## 一、概述与完整性校验

**自 Directive #5.98 起，`AiTeammate` 已内置 `CombatSolverInMemoryPatcher`（纯内存运行时注入器）与 `CombatSolverInsurancePatches`（Harmony 双重保险），游戏启动时直接在内存中对原版未修改的 `CombatSolver.dll`（`0.50.1`）动态完成以下全部 22 处（87 个位点）补丁注入，无需修改磁盘上的 `CombatSolver.dll` 文件，也不在仓库中分发任何第三方 DLL。**

`CombatSolver` 原生设计为单人本地出牌求解器。在双人本地同屏联机模组（`Local Multi-Control` / `DualRoleAdventure`）与 AI 队友（`AiTeammate`）架构下，为了让 CombatSolver 能够作为后台求解器完全接管 2 号玩家（Player 2）的回合决策，且不干扰 1 号玩家（Player 1）的前台操作，对 `CombatSolver.dll` 在内存映射中执行了 22 处底层 IL 字节码补丁注入。

- **官方工坊原版文件 SHA-256**: `832060172AA5EAE8D79546F120A10E4571C324B7C0D6AB2C6ABC2BCAD24323CD`
- **补丁后内存镜像 SHA-256**: `C1A2C5DF901942A3CB2FA9ED5429B0435342BF09DFDA009A71D9056F33FEEEFC`
- **自动化补丁校验脚本**: [`apply_combatsolver_patches.py`](apply_combatsolver_patches.py)

---

## 二、22 处 IL 补丁全量清单

| 补丁序号 | 文件偏移 (File Offset) | 对应 RVA | 目标类与方法 (Target) | 修改前逻辑 (Original) | 修改后逻辑 (Patched) | 用途与解决的问题 (Purpose & Resolution) |
| :---: | :---: | :---: | :--- | :--- | :--- | :--- |
| **1** | `0x067e9b` | `0x69c9b` | `MultiplayerSessionState.get_IsMultiplayerSession` | 调用 `RunState.IsMultiplayer` 判断是否为多人会话 | `ldc.i4.0; ret; nop*3`（硬编码恒返回 `false`） | 欺骗 CombatSolver 认为当前处于单人环境，绕过其因联机模式而自动禁用的全局防御机制。 |
| **2** | `0x069007` | `0x6ada8` | `SolverController.BeginCombat` | 校验玩家数量严格为 1，且玩家必须等于 `LocalContext.NetId` | 改为允许玩家数 `>=1`，并跳过 `player == LocalContext.NetId` 单人唯一校验 | 允许战斗初始化时存在多名玩家，即使前台控制权属于 P1，也能为 P2 正常启动战斗求解会话。 |
| **3** | `0x0691c7` | `0x6af5c` | `SolverController.ActivateTurnSetupResult` | 跨回合时限制玩家数量必须为 1 | 改为允许玩家数 `>=1` | 允许在多回合跨轮次战斗中持续流转回合设置结果。 |
| **4** | `0x06cc2a` | `0x6ea2c` | `SolverController.MonitorCombatPresence` | 监测战斗在场时限制单人 | 改为允许玩家数 `>=1` | 防止多人战斗场景下求解器误判战斗退出。 |
| **5** | `0x06f627` | `0x71418` | `SolverController.CanSolve` | 判断能否求解时限制单人 | 改为允许玩家数 `>=1` | 确保在双人战斗中 `CanSolve` 判定恒为真，准许求解流程发起。 |
| **6** | `0x06e194`<br>`0x06e2a9`<br>`0x06e322` | `0x6ff00` | `SolverController.StartFullAutoDeployment` | 检测到负面结果、战斗结束或死亡回合时调用 `StopFullAuto` | 将三处调用均替换为 `ldc.i4.0; nop*4` 禁用中断 | 防止求解器在出牌效果较差或出现预期偏差时中途退回手动，确保自动部署链路不被打断。 |
| **7** | `0x16ea21` | `0x170818` | `SolverController.ToggleVisibilityFromShortcut` | 快捷键切换 UI 界面时限制单人 | 改为允许玩家数 `>=1` | 允许用户在双人联机中随时按快捷键唤出 CombatSolver 控制面板。 |
| **8** | `0x0a5b27` | `0xa791c` | `TurnActionSolver.SolveCore` | 回合出牌核心求解循环限制单人 | 改为允许玩家数 `>=1` | 解锁核心多轮树搜索算法在双人上下文中的执行权限。 |
| **9** | `0x1a1fcc` | `0x1a3d90` | `TurnActionSolver.HandleMultiplayerScaling` | 遇到联机缩放参数时跳出求解 | 改为放行并适应联机状态缩放 | 防止求解器在读取到多人血量/伤害缩放时拒绝搜索。 |
| **10** | `0x186bff` | `0x1889dc` | `CombatPredictionHistory.GetCounters` | 预测历史计数限定单人 | 改为支持单人或任意多玩家 | 保证伤害与回合预测能够正确记录双人历史状态。 |
| **11** | `0x050ab1` | `0x5289c` | `CombatShowcaseCollector.IsEligibleOpening` | 开局起手收集器限定单人 | 改为允许玩家数 `>=1` | 允许收集双人起手牌型以进行特征分析。 |
| **12** | `0x04efe5` | `0x50de0` | `CombatReplayRecording.Start` | 战斗回放录制限定单人 | 改为允许玩家数 `>=1` | 支持双人场景下的动作回放与验证。 |
| **13** | `0x509b7a` | Metadata | `MethodSpec 0x2b0002e2` | 元数据指向未特化方法标记 | 覆写为 `System.Linq.Enumerable.Last<Player>(IEnumerable<Player>)` 特化元数据标记 | 为底层 IL 提供获取玩家列表最后一个玩家（P2）的高性能标准调用令牌。 |
| **14** | `0x363fc2`<br>`0x045715`<br>`0x045719` | `0x47505` | `BattleDamageTracker.GetSinglePlayer` | 原为 `private static`，限制单人且返回第一名玩家 | 更改可见性为 `public static`，放行 `>=1` 玩家，并返回 `state.Players.Last()` | 将求解器聚焦的“唯一玩家”重定向为队伍末位玩家（P2），并将该静态方法暴露给全局调用。 |
| **15** | 全文件 57 处 | 全局多处 | `LocalContext.GetMe(CombatState)` 硬编码调用 | 原生 57 处直接读取前台玩家 `LocalContext.GetMe` | 全部替换为调用 `BattleDamageTracker.GetSinglePlayer`（指令 `28 ef 09 00 06`） | **核心手术补丁**：将 CombatSolver 内部所有感知自身属性（血量、手牌、能量、费用、格挡）的代码彻底重定向至 P2，使得即使 P1 处于前台，求解器也完全根据 P2 状态进行推理。 |
| **16** | `0x18c06d`<br>`0x18c073` | `0x18bfec` | `CombatPredictionSimulator..ctor` | 构造函数限定单人并绑定 P1 | 改为放行 `>=1` 玩家并绑定 `state.Players.Last()` (P2) | 确保战斗预测模拟器在仿真推演时以 P2 的角色模型作为主体。 |
| **17** | `0x1b45e2`<br>`0x1b461a` | `0x1b4594` | `PreCombatLiveStateSnapshot.Capture` | 战前快照采集限定单人并绑定 P1 | 改为放行 `>=1` 玩家并绑定 `runState.Players.Last()` (P2) | 确保战斗前快照正确捕获 P2 的牌库、遗物与药水基线。 |
| **18** | `0x1ab6ac`<br>`0x1ab84d` | `0x1ab6ac`<br>`0x1ab84d` | `ForkMap.Register`<br>`ForkMap.RequireRemap` | 多线程搜索在预测卡牌映射冲突或缺失时直接 `throw InvalidOperationException` | 分别替换为直接 `ret` 与 `ldarg.1; ret`（直接返回原卡）+ NOP 填充 | 中和多线程树搜索中的分支映射冲突崩溃，彻底根除因 `PredictedCard` 映射丢失引发的求解器崩溃。 |
| **19** | `0x07a678` | `0x7a66c` | `SolverController.ApplyPerformancePreset` | 游戏运行时会以 `Medium` 预设无条件覆写用户配置 | 旁路预设覆写分支，强制执行 `BuildCustomPerformance` | 保证用户配置的搜索超时时间（`SearchTimeLimitSeconds`）与最大搜索节点数（`SearchMaxExpandedNodes`）100% 严格生效。 |
| **20** | `0x04ffcf`<br>`0x050022` | `0x4ffcf`<br>`0x50022` | `CombatRootSnapshot.Capture` | 校验状态投影一致性与状态突变检测，不匹配时报错并终止快照 | 替换为 `pop; pop; pop; ldc.i4.1; nop; brtrue.s` 强制视为通过 | 避免双人战斗中由于队友行动导致的状态微差引起快照采集跳闸。 |
| **21** | `0x06d948`<br>`0x06d8b0` | `0x6d948`<br>`0x6d8b0` | `SolverController.CompleteSearchCore` | 判定计划过期时丢弃计划；在方案评分劣质时关闭全自动模式 | 旁路 `SEARCH_STALE` 丢弃分支，并消除自动关闭 `FullAutoEnabled` 的操作 | 确保搜索出的有效计划能够稳定下发执行，杜绝求解器因微弱分差中途罢工。 |
| **22** | `0x069a27` | `0x6b823` | `SolverController.RequestSearch` | 在搜索发生重入或状态冲突时打印日志并执行 early `ret` | 写入 `2b 2e` (`br.s IL_01c3`) + 46 个 `00` (`nop`)，直接跳转至全新搜索的分发与启动 | **Directive #5.16 关键防假死补丁**：旁路静默 `ret`，消除了求解器卡在 `Executing...` 且无任何重算响应的死锁根源。 |

---

## 三、补丁重现与脱机验证

`AiTeammate` 模组已在游戏启动时通过内存注入自动完成上述所有修改。若开发者或高级用户需要脱机验证补丁字节码的准确性，可运行附带的验证脚本：

```bash
# 1. 安全校验模式（仅在内存中打补丁并比对 SHA-256）
python docs/apply_combatsolver_patches.py

# 2. 写入应用模式（仅在需要离线研究产物时使用）
python docs/apply_combatsolver_patches.py --apply
```

执行产物保证与 `C1A2C5DF901942A3CB2FA9ED5429B0435342BF09DFDA009A71D9056F33FEEEFC` 字节级 100% 一致。
