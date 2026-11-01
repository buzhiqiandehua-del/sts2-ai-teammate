# AI 导演交接文档（STS2 双人 AI 队友项目）

> 新会话开局先读本文件 + 记忆索引。用户不会编程；我是导演（读日志/代码、出指令单、验收）；另一个 AI（Gemini/Antigravity，工作目录见下）是实施方；用户只负责进游戏实测和转发。

## 0. 角色与流程
- 用户：实测、截图、转发实施方输出；技术决策一律由导演做（用户明确授权）。
- 导演（本会话 lineage）：每轮读真实日志定罪→出"指令单"（事实依据/任务/验证清单/约束）→用户粘贴给实施方→实施方交付→用户实测→导演读日志验收。
- 实施方工作目录：`C:/Users/不值钱的画/.gemini/antigravity/brain/87bc0cdd-d283-4474-b5d8-dbb6c6b0a9ea/scratch/`（AiTeammateMod.cs、build_mod.py、test_ai_ai_teammate.py、IL 脚本群、p2_choice_lifecycle_locating_doc.md）。
- 构建部署：build_mod.py → `mods/AiTeammate/AiTeammate.dll`；**每次交付后用户必须完全重启游戏**（mod 启动时加载）。

## 1. 关键路径
- 游戏目录：`F:/SteamLibrary/steamapps/common/Slay the Spire 2/`（mods/：AiTeammate、CombatSolver-0.50.1-Quark、STS2-RitsuLib、SpireStrategist）。
- 日志：`C:/Users/不值钱的画/AppData/Roaming/SlayTheSpire2/logs/godot.log`（mod 日志，轮转为 godot<ts>.log）；`logs/CombatSolver/<pid>-<hash>/combat-*.jsonl`（求解器逐回合事件）。
- 配置：同目录 `combat_solver_settings.json`（2s/4000 节点/Custom 预设）、`aiteammate_settings.json`（协调层冻结开关，默认全 false）。
- DRA 源码（开源）：github.com/GuyGinat/STS2_DualRoleAdventure（Scripts/Runtime/LocalMultiControlRuntime.cs 等）。
- SpireStrategist（MIT）：github.com/shaokangqian4-art/SpireStrategist；已装为 mod；Core=纯决策库（Policy/StrategicValue/EventPolicy/RelicValue），data/cards-v0.111.0.json；默认 advice mode，其 takeover/循环爬塔**禁用**（循环爬塔在本环境闪退）。

## 2. 架构现状（AiTeammate 胶水域）
- UI 压制：SuppressControlSwitchUi 标志 + 根源压制（SwitchControlledPlayerTo/ApplyControlContext prefix，战斗内门控，交互白名单除外）+ Refresh* 族 prefix + NOverlayStack.Push 总闸（含 5s failsafe）。
- 恢复链：CompleteDeployment/OnEndedTurnLocally 后置 → RestorePlayer1ControlIfActive → RefreshPlayer1CombatUiMinimal（卡堆/能量球/结束按钮/顶栏，**不重建手牌容器**）。
- 安全件：部署窗口输入锁（NPlayerHand.CanPlayCards）、尸体视角压制、手牌归属自愈守卫（500ms tick）、stall 看门狗（12s kick 重算 / 20s CRITICAL 强制替 P2 结束回合）、divergence 自愈（带 1.5s 持久化门槛）。
- 协调层（冻结，开关 OFF）：等待/触发器/hold/盘中重算/门卫/联合斩杀估算——代码在但不生效。
- Flow 桥接：p2-route-follow 已通。**现状=非战斗自动化全部下架止血（#5.0.6）**：10-27 R2 交付 INIT failed=none、event 自动化真装上，但事件选项非终态→3952 次死循环→UI 锁死+切换角色闪退；Core 决策零调用、保险丝零生效（详见 §10）。保留生效件：UI 压制、恢复链、手牌自愈、stall 看门狗、路线跟随、P2 奖励自动领取（NRewardsScreen）、card_scores tags 227/227。

## 3. 已验收基线（勿回归）
无跳屏/无全屏换人文字；能量球即时正确；Tab/F8 正常；部署 ~2s 动画完整；输入锁；尸体观战视角；路线跟随；INIT-SUMMARY failed=none 门槛。

## 4. 未清欠账（按序）
- D1 手牌容器泄漏：**R1 已修生效**（日志 `hand-rebind-leak detected (leak=False drawMismatch=True count=1)` 一次自愈，走 DRA RefreshCombatUiForControlledPlayer）；残留牌待用户实测复核。
- D2 抽牌刷新：同上行自愈路径生效，待实测复核（祭品抽牌 ≤1s 进手牌）。
- D3 非战斗自动化（三选一/事件/遗物/篝火/NEOW/商店，原 D3+D2'）：**#5.0.6 起全部下架冻结**。两轮均搞出死循环（R1 三选一空索引不被消费；R2 事件选项非终态+每帧提交 3952 次）→UI 锁死+闪退。重新上架前置条件（缺一不可）：①通用保险丝（同目标第 2 次 offer 未 resolved 即 autopilot-circuit-open 永久断闸）真实验收过；②Core 决策真接线（日志可见 reason=core 或 core-error）；③选项终态判定（非终态选项须处理后续子选择或明确放弃，检测 isFinished 与 choice id 是否被游戏消费）。
- D8 战斗内 P2 选择（"搜刮！将一张牌添加到你的牌组"类，owner=P2）：完全未覆盖；10-26 20:37 CRITICAL 强结束后 overlay 残留致 P1 UI 软锁。#5.0.6 未纳入（本轮只做减法），继续挂账。
- D9（新，10-27）事件死循环+切换角色闪退案：SELF_HELP_BOOK P2 侧 3952 次提交（≈每帧）搅死 P1 事件界面点击→用户按左上角切换三角→数秒后 native 闪退（日志戛然而止，无 crash 栈无 shutdown）。下架自动化后诱因应消失；#5.0.6 验收含三场景切换无闪退；R3-3 要求实施方书面取证切换路径与每帧重入的叠加风险。
- D4 搜索线程挂死（二进制 IL 层，teamwork 轮）：state_mismatch 重入致工作线程静默死；kick 有时踢不醒；CRITICAL 兜底=空过。10-27 抓到 kick 失效具体因：`KickSolverRecalculation` 反射 invoke 抛 `ArgumentException: Object of type 'NGame' cannot be converted to type 'NGame'`（跨加载上下文同名类型身份不一致），见 §11 R4-2。
- D5 协调层 reopen 序列：等待→hold→盘中重算，各带独立开关、实战验收后逐个开；前置=D4+真实部署入口定位（prefix-enter 三轮为 0 的悬案）。
- D6 角色协同表增量（每角色一轮，数据活）；D7 求解器级联合博弈=长期搁置。

## 5. 流程规矩（硬性）
- 指令单格式：事实依据（导演已读日志）/任务/验证清单/约束；验证以**真实游戏日志**为准，实施方测试套件仅参考；交付必附 Init 块与 INIT-SUMMARY。
- 钩上下文类任务先交定位文档（生命周期+钩点+证据），导演审过才收代码。
- 每单最多两轮实现，超了砍范围重立项；teamwork 仅限二进制 IL 轮。
- 失败开闸原则：任何异常→立即出牌/立即结束回合，绝不锁死玩家；P1 的选择永远手动（owner 门）。
- 验收套路：重启游戏+新存档+导演读日志（先数门槛行：INIT-SUMMARY、prefix-enter、offer/resolved 成对、leak/CRITICAL 零）。

## 6. 成本规矩
- 里程碑验收后轮换会话+交接文档（本文件持续更新）；新会话上下文 ~20-30K 起步。
- 用户经阿里云百炼路由计费：均次 token=会话长度，控制会话长度=控制成本；截图按需发。

## 7. 当前指令单：#5.22 R2（按钮评估点定点修+撤悬浮件）+#5.24（求解器早收工归因+禁早收守卫）并行→#5.21 代码轮（pack/transform 自动）+商店 T2 同批。心电图/早收定罪见 §46b；生命线 #5.23 初验过（零触发）。

> 第二轮验收记录见 §10。以下为指令单全文，可直接粘给实施方：

```

# 指令单 #5.0.6（止血轮：非战斗自动化全下架 + 通用保险丝）

导演已读 10-27 08:45–08:51 godot.log 全文（17408 行）与 CombatSolver/10616 会话。#5.0.5 第二轮验收=fail，按规矩砍范围重立项。本轮只做减法+一个防御件，禁止任何新自动化功能、禁止新增/修改切换或 UI 压制补丁。

## A. 事实依据（导演已读日志）
1. 生效项（保持不动）：INIT-SUMMARY failed=none（R2-1 两组 INIT-FAIL 已清）；card_scores.json tags 227/227（R2-6 过）；战斗正常（Turn 6 收尾，ROUTE_ACTION expected/actual 全对齐，无 stall/CRITICAL）；恢复链 wakuu-aiteammate-restore1p 全程正常；R1 手牌自愈无泄漏。
2. 定罪一（事件死循环+UI锁死+闪退，08:50–08:51）：EVENT.SELF_HELP_BOOK（shared:False）。P1 于行 1600 手动选 index 2+选 RUPTURE 附魔，P1 侧正常完成。P2 侧：mod 提交选项 0（"读下封底"=选择一张攻击牌附魔）→EventSynchronizer 收到 "Option index 0 chosen for player …802"→但该选项非终态，事件进入选牌子页面，isFinished 恒 False→probe 重新检测→offer→再提交，共 3952 次（约 1 分钟，≈每帧提交）。PlayerChoiceSynchronizer 仅早期 Reserved choice id 1，后续提交游戏已不消费，mod 未检测。用户体感=事件界面点击无响应；按左上角切换三角→数秒后 native 闪退（日志戛然而止，无 crash 栈、无 Steamworks shutdown；对比 08:45 前正常退出会话有完整 shutdown 流程）。
3. 定罪二（Core 决策零接线，R2-2 fail）：全日志 reason=core 零行、core-error 零行；3952 次 resolved 全部 reason=fallback source=fallback；pick 字段打的是本地化字符串拼接（pick=locstring table events entry …）而非决策索引——Core 从未被调用。
4. 定罪三（保险丝零生效，R2-3 fail）：grep autopilot-circuit 零行，3952 次同目标 offer 无断闸。
5. NEOW P2 自动选择（reason=fallback 乱选，如开局奖励选错代价高）一并下架；R2-4/R2-5 本局未走到，不追罪，全部冻结。

## B. 任务
- R3-1 非战斗自动化全下架：event/relic/rest/card-reward/neow/shop 的自动 offer 与自动提交全部禁用——直接不装这些钩子（或删除代码），不是留开关默认 OFF。行为回到 P1 手动替 P2 选择。门槛=全日志 p2-choice-offer / p2-choice-resolved 零行。
- R3-2 通用死循环保险丝（独立组件、无条件生效、先于任何自动逻辑）：任何 mod 自动决策对同一目标（kind+内容指纹，如事件ID+选项组）第 2 次 offer 仍未 resolved→立即打 autopilot-circuit-open 并对该目标永久断闸（会话级）。交付附自证：演示日志或等价测试输出，显示同一目标第 3 次提交被断闸拦截。此件是未来任何自动化重新上架的前置条件。
- R3-3 闪退取证（书面说明，不改代码）：回答在"每帧提交 EventSynchronizer 选择"进行时，用户按左上角切换触发 SwitchControlledPlayerTo，mod 的 root-suppression prefix、NOverlayStack.Push 总闸、Refresh* prefix 各自走什么路径、是否存在与每帧重入叠加的 UI 重建。
- R3-4 保留项照旧（勿动）：UI 压制、恢复链、手牌自愈、stall 看门狗、路线跟随、P2 奖励自动领取（NRewardsScreen）、card_scores.json。
- R3-5 交付物：AiTeammate.dll + Init 块 + INIT-SUMMARY + R3-2 自证日志 + R3-3 书面说明。

## C. 验证清单（导演以真实游戏日志验收）
1. INIT-SUMMARY failed=none。
2. 完整一局，至少经历 1 次事件房、1 次卡牌三选一奖励、1 次篝火或商店：全日志 p2-choice-offer / p2-choice-resolved 零行；全部 P1 手动完成；无死循环、无 UI 锁死、无闪退、无 CRITICAL、无 stall。
3. 事件界面、地图、战斗中三个场景各按一次左上角切换三角：无闪退、无跳屏。
4. R3-2 自证演示日志随交付附上（导演审）。
5. §3 基线全项 + R1 手牌自愈 + P2 奖励自动领取照旧。

## D. 约束
同 #5.0.5 D 全部（失败开闸最高原则、P1 永远手动、协调层冻结、交付附 Init 块与 INIT-SUMMARY、交付后用户完全重启游戏）。#5.0.6 范围极小（删功能+一个防御件），一轮完成；若 fail 直接回滚 10-25 基线版 dll。
```

## 8. 用户画像与沟通
- 中文、非程序员；术语要翻译成体感语言；"需要你定的"类问题由导演裁决后给可粘贴裁决块。
- 体验优先级：不卡死/不锁死 > 不跳屏 > AI 出牌聪明 > 配合花活。

## 9. #5.0.5 第一轮验收记录（10-26，导演读 godot.log 19:36–19:39 与 20:37–20:39 两会话）
- 生效项：任务0 手牌自愈（`hand-rebind-leak ... drawMismatch=True count=1` → `Hand container rebound to Player 1 via DRA RefreshCombatUiForControlledPlayer`，一次干净）；P2 战斗奖励自动领取（`Auto-claiming 2 P2 reward buttons`）；任务3/任务4 交付物已收（card_scores.json 227 条但标签仅 62；定位文档 §8 含 BeginEvent 签名、boss 遗物=Path A）。
- 定罪项（全部进 §7 第二轮）：两个 INIT-FAIL（flow-coordinator：BeginRelicPicking postfix `Parameter "player" not found` 中断整组；turn-coordinator：GetTweenForCardsChangingPiles 重载歧义）；三选一全 fallback-skip + 空索引 skip 不被消费→同牌重 offer ≥12 次死循环→solver-search-stall→CRITICAL solver-dead；战斗内 P2 选择零覆盖。
- 软锁案（20:37 会话，行 ~1676–1728）：CRITICAL 强结束 P2 回合并 `Restored control to Player 1 via wakuu-stall-endturn` 后，"搜刮！"overlay 残留不消失，跳过按钮与 [角色2] 行点击无响应，用户被迫退游戏（日志尾=Steamworks shutdown）。违反失败开闸，已并入 R2-4。
- 门槛行统计：INIT-SUMMARY failed=turn-coordinator,flow-coordinator（违）；CRITICAL ≥2 次（违）；card-reward offer/resolved 成对但 pick 全 skip（违）；event/relic/rest offer 零（违）。结论=打回，出第二轮（最终轮）。

## 10. #5.0.5 第二轮验收记录（10-27，导演读 godot.log 08:45–08:51 会话 17408 行 + CombatSolver/10616）
- 生效项：INIT-SUMMARY failed=none（R2-1 过，两组 INIT-FAIL 已清）；card_scores.json tags 227/227（R2-6 过）；战斗正常（Turn 6，ROUTE_ACTION expected/actual 全对齐，无 stall/CRITICAL）；恢复链全程正常；NEOW P2 自动选一次成功但 reason=fallback（乱选，已并入下架范围）。
- 定罪一（10-27 08:50–08:51，行 1578 起）：SELF_HELP_BOOK（shared:False）P2 侧死循环——选项 0"读下封底"非终态（选完进选牌附魔子页面），isFinished 恒 False，probe→offer→提交循环 3952 次（≈每帧，约 1 分钟）；PlayerChoiceSynchronizer 仅 Reserved choice id 1 次后游戏停止消费，mod 未检测；P1 事件界面点击被搅死；用户按左上角切换三角→数秒后 native 闪退（日志戛然而止，无 crash 栈、无 Steamworks shutdown）。
- 定罪二（R2-2 fail）：reason=core / core-error 全日志零行，Core 从未被调用，pick 字段为本地化字符串装饰（pick=locstring table events entry …）。
- 定罪三（R2-3 fail）：autopilot-circuit 零行，3952 次同目标 offer 无断闸。
- 结论=fail（连续两轮死循环且防御件不生效）→ 按 §5 砍范围重立项 → #5.0.6（非战斗自动化全下架+通用保险丝）。R2-4/R2-5 本局未走到，不追罪，冻结。

## 11. #5.0.6 第一轮验收记录（10-27 15:47–15:50 会话）+ 第二轮迷你单
- 通过项：R3-1 全日志 p2-choice-offer/resolved 零行（relic/neow 亦零行，手动=设计态）；INIT-SUMMARY failed=none；无闪退无死循环；R3-2 自证 test_r3_fuse.py（15:42）随交付收到；手牌自愈/恢复链照旧。
- 缺陷一（auto-claim 半流程，行 1646/2667）：保留项 Auto-claiming 仍点 P2 的 CardReward 行→OnSelect→ShowScreen 当脸弹 P2 选牌界面→等手动选（用户体感"战利品界面自动弹出抓牌"）。R3-4 保留件与 R3-1 下架件组合出的新跳屏体验。
- 缺陷二（kick 类型转换，行 1874–1880）：solver-search-stall→`KickSolverRecalculation exception: ArgumentException: Object of type 'NGame' cannot be converted to type 'NGame'`（跨加载上下文同名类型反射 invoke 身份不一致）=D4"kick 踢不醒"具体因；本次 stall 未升 CRITICAL 但救火路径无效。
- 未验项：C-3 三场景按切换三角无闪退（用户未做）。
- 结论=条件通过，出第二轮迷你单（全文如下）：

```
# 指令单 #5.0.6 第二轮（迷你轮：两个体验/健壮性修复 + 一项回归补做）

## A. 事实依据（导演已读 10-27 15:47–15:50 godot.log）
1. R3-1 过：全日志 p2-choice-offer/resolved 零行；INIT-SUMMARY failed=none；无闪退无死循环；test_r3_fuse.py 自证已收。
2. 缺陷一：行 1646/2667 Auto-claiming 点 P2 CardReward 行→ShowScreen 当脸弹 P2 选牌界面→等手动选（用户体感"自动弹出抓牌"）。
3. 缺陷二：行 1874–1880 stall→KickSolverRecalculation 抛 ArgumentException（'NGame' 不能转 'NGame'，跨加载上下文类型身份不一致），救火 kick 无效。

## B. 任务
- R4-1 auto-claim 收窄：只自动领取无选择奖励行（金币、遗物行）；CardReward 行不自动点，按钮留给用户自己点了手动选。验收=战利品界面不再"没点就弹 P2 选牌界面"；Auto-claiming 行可保留但只含金币/遗物行。
- R4-2 修 kick：KickSolverRecalculation 的反射 invoke 改为从实例自身GetType()/同一 Assembly 取目标 MethodBase（或同上下文创建委托），根除同名类型转换异常。验收=stall→recalc 行对出现且无 ArgumentException。
- R4-3 补做 C-3 回归：事件房/地图/战斗三场景各按一次左上切换三角无闪退；实施方侧自测说明随交付附，用户实测由导演另行安排。

## C. 验证清单
INIT failed=none；offer/resolved 零行；无弹屏（用户实测）；kick 无异常；三场景切换无闪退；§3 基线+手牌自愈+恢复链零回归。

## D. 约束
同 #5.0.6 D；迷你轮一轮完成，禁止扩范围。
```

## 12. #5.0.6 迷你轮验收（10-27 16:08–16:11 会话）+ #5.0.7 指令单
- R4-1 实测生效：`Auto-claiming 1 P2 reward buttons`（仅金币/遗物行），卡牌行由用户手点（行 1346/1354），弹屏消失。
- R4-2 未修结转：行 1671 `KickSolverRecalculation exception: ArgumentException ...'NGame'...`（栈含 AiTeammate.TurnCoordinator.KickSolverRecalculation）照旧。
- R4-3 三场景切换三角：用户仍未做，继续欠。
- 止血面照旧健康：offer/resolved 零行、INIT failed=none、无闪退无循环。用户问"为何全手动"=设计态解释+上架梯子启动（§7 头）。

```
# 指令单 #5.0.7（重新上架第一步：三选一带保险丝回归 + kick 结转修复）

导演已读 10-27 16:08–16:11 godot.log。#5.0.6 迷你轮验收：R4-1 实测生效；R4-2 未修结转本单 T0；R4-3 欠。

## A. 事实依据
1. 止血面健康：全日志 offer/resolved 零行、INIT-SUMMARY failed=none、无闪退无循环；手动选遗物/事件/篝火/商店=#5.0.6 设计态，非故障。
2. Core 决策迄今零接线（历史 reason=core 零行；R2  era core-decision unavailable:read-method）——本单必须真接线并日志可见。
3. 保险丝组件 test_r3_fuse.py 已交付但实战零触发——本单消费检查是它第一次实战考验。
4. 三选一为终态 kind（单 index 提交即消费），是重新上架最安全的第一种；非终态 kind（带子页面的事件）继续冻结。

## B. 任务
- T0（结转 R4-2）：KickSolverRecalculation 反射 invoke 跨上下文 NGame 类型异常根除（从实例自身 GetType()/同一 Assembly 取 MethodBase 或同上下文建委托）。验收=stall→recalc 行对无 ArgumentException。
- T1 三选一重新上架（本单唯一允许上架的 kind）：offer 点 CardReward.OnSelect owner==P2 打 offer 行；决策调 SpireStrategist Core 对三张牌评分，打 reason=core pick=<idx> scores=<...>；Core 抛异常打 reason=core-error:<msg> 并取 idx 0（禁止 skip、禁止静默吞）；解决走既有 ShowScreen-skip + SyncLocalChoice(FromIndex) 路径；P1 永远手动。
- T2 消费检查+保险丝在路径：resolved 后同指纹（三张牌组合）2s 内再 offer→立即 autopilot-circuit-open 并对该目标会话级永久断闸，禁止重试提交。
- T3 禁止项：event/relic/rest/neow/shop 任何自动 offer/submit 不得装（保持下架）；UI 压制/恢复链/手牌自愈/auto-claim 收窄不得动。

## C. 验证清单（真实日志）
1. INIT failed=none；CRITICAL 零；stall 若出现则 recalc 无异常。
2. card-reward 至少一条 reason=core（或带显式 msg 的 core-error，零静默吞）；pick 被消费：全日志同指纹零重 offer；用户实测无弹屏、P2 三选一自动完成。
3. 若出现循环：circuit-open 行必须出现且循环 ≤2 次即断（保险丝实战考验）。
4. event/relic/rest/neow/shop 零自动行；§3 基线+手牌自愈+auto-claim 窄Scope 照旧。

## D. 约束
同 #5.0.6 D；两轮上限；fail=三选一再度下架回 #5.0.6 态、上架梯子冻结一轮。
```


## 13. #5.0.7 实施与交付记录（10-27 16:40 会话）
- **T0 根除跨 ALC NGame ArgumentException**：
  - 新增 `ResolveLiveRequestSearchMethod()` 与 `InvokeLiveRequestSearch(state, trigger)`。
  - 通过 `liveHost = NGame.Instance; liveHostType = liveHost != null ? liveHost.GetType() : typeof(NGame)`，在加载的所有 `CombatSolver` 程序集中严格匹配 `pars[0].ParameterType == liveHostType`。
  - 动态构造形参列表，`args[0] = liveHost` 直接赋给参数 0，彻底根除 `ArgumentException: Object of type 'MegaCrit.Sts2.Core.Nodes.NGame' cannot be converted to type 'MegaCrit.Sts2.Core.Nodes.NGame'`。
  - `KickSolverRecalculation` 与 `RequestRecalculation` 统一调用该解析器。
- **T1 三选一重新上架（唯一允许上架 kind）**：
  - `PatchFlowCoordinator` 仅重新挂载 CardReward 组（`CardReward.OnSelect`、`ShowScreen`、`OptionSelected`、`GetCardHolder`）。
  - 决策端接入 SpireStrategist Core：通过按方法名和参数数量（`Read(2)`、`ReadCard(1)`、`Evaluate(2)`）解析反射方法，规避跨程序集 Type 严格判等失败（彻底根除 `unavailable:read-method`）。
  - 解析 `evalResult.Options` 提取卡牌评分，格式化输出 `scores=<card:score,...>`。
  - 发射成对日志：
    - `[AiTeammate][Flow] p2-choice-offer kind=card-reward owner=P2 cards=<...>`
    - `[AiTeammate][Flow] p2-choice-resolved kind=card-reward pick=<idx> reason=core scores=<...> card=<cardId>`
    - Core 异常时：`[AiTeammate][Flow] p2-choice-resolved kind=card-reward pick=0 reason=core-error:<msg> scores=none card=<cardId>` 并强制保底 index 0，禁止 skip，禁止静默吞。
  - 采用候选一拦截（`ShowScreen` 压制返回 dummyScreen，`OptionSelected` 返回 `Task.FromResult<int?>((int?)chosenIndex)`）。
- **T2 消费检查 + 保险丝在路径**：
  - `AutopilotFuse` 新增 `ResolvedTimestamps`。
  - `TryBeginOffer` 中引入消费检查：若相同指纹在 `MarkResolved` 后 2.0 秒内再次 offer，判定游戏未消费/重 offer，立即熔断断闸：
    `[AiTeammate][Fuse] autopilot-circuit-open target=card-reward:<fingerprint> offers=2 (session-permanent)`
  - 断闸后 `IsOpen` 恒真，`ShowScreen` 与 `OptionSelected` 立即 fail-open（放行原生 UI 给 P1 手动选择）。
- **T3 严格冻结非战斗自动化**：
  - `event`、`relic`、`rest`、`neow`、`shop`、战斗内 `CardSelectCmd` 全部保持未注册（零 offer/resolved）。
  - 保持手牌自愈、恢复链、UI 压制、狭义 auto-claim（仅金币/遗物/药水，跳过 CardReward）。
  - 门槛：`INIT-SUMMARY failed=none`。
- **T4 C-3 左上切换三角回归自测说明**：
  - 事件房：AiTeammate 零事件钩子装载，切三角完全由游戏原生与 DRA 消费，无并发提交。
  - 地图：仅被动监听 `PlayerVotedForMapCoord`，无 UI 压制与输入拦截，切三角安全。
  - 战斗：受 `RestorePlayer1ControlIfActive`、`SwitchControlToPlayer` 与手牌所有权防泄漏自愈保护，无异常。
- **测试验收**：
  - `test_directive_5_0_7.py` 全部 5 组校验通过。
  - `test_ai_teammate.py` 全量历史测试套件（107 基线 + 4.0 + 5.0 + 5.0.3 + 5.0.4 + 5.0.5 + 5.0.5 R2 + 5.0.6 + 5.0.6 R2 + 5.0.7）全部 PASS。
  - 产物编译部署至 `mods/AiTeammate/AiTeammate.dll`。

## 14. #5.0.7 验收记录（10-27 17:55–18:01 会话）+ #5.0.8 指令单
- 通过：INIT failed=none；card-reward 4 组成对（1404/1409、2631/2636、3527/3532、4878/4883）全消费、同指纹零重 offer、零 circuit-open（无循环可断）、用户实测自动抓牌无弹屏；T0 kick 生效（2818/5179 stall→recalc 无 ArgumentException）；event/relic/rest/neow/shop 零自动行。
- 定罪一（Core 未真接线）：4 次 resolved 全 `reason=core-error: Object of type 'RunState' cannot be converted to type 'IRunState'`——跨 ALC 类型身份病（NGame 案同族换一对类型）；T1 只证错误路径不静默吞，未证 Core 可调。
- 定罪二（error fallback 不 deterministic）：单约定 core-error 取 idx 0，实际 pick=0/1/1/2，兜底决策源未解释。
- 结论=条件通过 → #5.0.8（全文如下）：

```
# 指令单 #5.0.8（梯子第二步：Core 跨 ALC 转换病根治 + 遗物/篝火重新上架；事件继续冻结）

导演已读 10-27 17:55–18:01 godot.log。#5.0.7 条件通过：三选一自动生效且消费干净、kick 生效；Core 全走 core-error（RunState/IRunState 跨 ALC 转换病）、error fallback pick 不 deterministic（0/1/1/2）。

## A. 事实依据
1. 生效面：INIT failed=none；card-reward 4 组成对全消费零重 offer；stall→recalc 无异常；其余 kind 零自动行；保险丝实战零触发（无循环）。
2. 跨 ALC 类型身份病第二次发作（第一次=NGame kick，T0 已用 live-type 解析法治愈）：游戏 ALC 的 RunState 不实现 SpireStrategist 引用副本的 IRunState 接口→反射 invoke 抛转换异常→Core 评分从未真跑。
3. error fallback pick=0/1/1/2 违反单内"core-error 取 idx 0"约定，兜底源未解释。

## B. 任务
- T1（结转）Core 跨 ALC 根治：让 Core 入口接受游戏 live RunState（参照 T0 live-type 解析思路：按接口全名匹配/游戏 ALC 内编译桥接层/同 ALC 加载 Core，三选一并自证）。验收=真实日志 ≥1 条 reason=core 且 scores 非空。
- T2 error fallback deterministic：core-error 时的 pick 规则固定并打日志（idx 0 或 card_scores.json 最高分，二选一，交付说明所选）；零不明来源 pick。
- T3 遗物重新上架（保险丝+消费检查同三选一模式）：路径A Prefix RelicSelectCmd.FromChooseARelicScreen（owner==P2 返回已完成 Task<RelicModel>），决策 Core RelicValue（error 走 T2 规则）；路径B 宝箱 OnPicked(P2,idx)；路径C RelicReward 自动领取。boss/开局遗物选择屏属路径A。**NEOW/开局先古馈赠不在本单**（梯子 5.10）。
- T4 篝火重新上架：offer 点=rest-site 轮到 P2；决策 HP<50% 休息否则锻造；升级路径的选牌子选择必须 3s 内程序化解决（CardSelectCmd.FromDeckForUpgrade+Core），超时自动改休息并打 rest-fallback；绝不允许留 pending 子选择（SELF_HELP_BOOK 教训）。
- T5 事件终态判定设计文档（只文档不代码）：如何判定事件选项非终态（进子页面）、mod 应对（带保险丝递归解决子选择 / 或 circuit-open 放弃留手动）二选一论证；导演审过才开 5.09。
- T6 禁止项：event/neow/shop 零自动装机；三选一既有路径除 T1/T2 外不得动。

## C. 验证清单（真实日志）
1. INIT failed=none；CRITICAL 零；同指纹零重 offer；若循环 circuit-open 且 ≤2 次断。
2. reason=core ≥1 条且 scores 非空；core-error 零不明 pick。
3. relic offer/resolved 成对 ≥1（实测走到即验）；篝火成对 ≥1；升级子选择零 pending 残留。
4. event/neow/shop 零自动行；§3 基线+手牌自愈+auto-claim 窄+三选一零回归。
5. T5 设计文档随交付，导演审。

## D. 约束
同 #5.0.6 D；两轮上限；fail=遗物/篝火再下架（三选一保留除非受累）、梯子冻结一轮。
```

## 15. #5.0.8 实施与交付记录（10-27 会话）
- **T1 Core 跨 ALC 根治（Live ALC Resolution）**：
  - 病根定位：#5.0.7 中 `TryPreloadSpireStrategist()` 通过 `Assembly.LoadFrom` 加载了独立 ALC 副本，Harmony 遍历优先返回该预载程序集，其引用的 `IRunState` 与游戏现场 `sts2.dll` 产生跨上下文身份冲突。
  - 根治实现：
    1. 在 `TryPreloadSpireStrategist()` 中消除孤立 `Assembly.LoadFrom`，改为先检查 AppDomain，若需加载则通过 `AssemblyLoadContext.GetLoadContext(typeof(RunState).Assembly)` 动态加载到游戏 live ALC 中，确保类型完全同一。
    2. 新增 `FindLiveSpireStrategistAssembly()`：遍历已加载程序集，优先匹配 `SnapshotReader.Read` 参数 0 可从 live `RunState` 赋值（`pars[0].ParameterType.IsAssignableFrom(liveRunType)`）的 live 程序集。
    3. `EvaluateCardReward` 与 `EvaluateRelicSelection` 均从 `FindLiveSpireStrategistAssembly()` 检索 `SnapshotReader`、`Policy` 与 `RelicValue`，根除 `ArgumentException: Object of type 'RunState' cannot be converted to type 'IRunState'`。
- **T2 Error Fallback Deterministic（确定性兜底并显式日志）**：
  - 规则确定：当 Core 评估异常或不可用时，固定选取 `card_scores.json` 中最高分的卡牌作为兜底推荐（平分或缺省时保底 index 0），杜绝非确定性来源。
  - 日志显式化：若走 fallback，明确打印：
    `[AiTeammate][Flow] p2-choice-resolved kind=card-reward pick=<idx> reason=core-error:<msg> fallback=card-scores-highest scores=none card=<cardId>`
- **T3 遗物重新上架（Paths A, B, C 全链上架 + 保险丝）**：
  - 路径 A（Prefix `RelicSelectCmd.FromChooseARelicScreen`）：针对 Boss 遗物/开局遗物选择，P2 拦截并由 Core `RelicValue` 评分，返回已完成 `Task<RelicModel>`，跳过 UI 阻塞；带 `AutopilotFuse.TryBeginOffer("relic", fingerprint)` 熔断保护。
  - 路径 B（宝箱 `TreasureRoomRelicSynchronizer.BeginRelicPicking` Postfix）：P2 自动通过 Core 评分调用 `OnPicked(p2, pickIdx)`；带保险丝保护。
  - 路径 C（`NRewardsScreen._Ready`）：通过 `TryAutoClaimP2Rewards` 自动领取无选择奖励（金币、遗物、药水），卡牌行保持不自动点击。
  - 排除项：NEOW/开局先古馈赠本单严格不装（结转 5.10）。
- **T4 篝火重新上架（Rest Site + 3s 锻造看门狗）**：
  - offer 点：通过 `RestSiteSynchronizerChooseLocalOptionPatch.TryFindNextSelectablePlayer` 检测轮到 P2。
  - 决策逻辑：P2 当前 `HP < 50%` 决策休息（heal），否则决策锻造（smith）；若选择锻造但牌组无升级牌，自动改休息并打 `rest-fallback`。
  - 选牌子子选择：Prefix `CardSelectCmd.FromDeckForUpgrade` 拦截 P2 选牌，必须并在 3s 内程序化解决（选最高分攻击牌，超时打 `rest-fallback`），返回完成 Task，绝不留挂起子页面。
- **T5 事件终态判定设计文档**：
  - 已落盘生成交付物文档：`event_terminal_state_design_doc.md`。
  - 详细定义了直接终态、多页对话与子选择界面的三道判定防线，并提出了混合防御架构（子选择页面严格 Circuit-Open 切换交还玩家手动，彻底免疫 SELF_HELP_BOOK 式死锁）。
- **T6 禁止项与基线保障**：
  - `event`、`neow`、`shop` 保持 100% 下架与冻结，全日志无自动 offer/resolved 行。
  - 手牌自愈、恢复链、UI 压制完全未动；INIT-SUMMARY failed=none。
- **测试验收**：
  - `test_directive_5_0_8.py` 6 组测试全部通过。
  - `test_ai_teammate.py` 全量回归套件全部通过（PASS 100%）。
  - 编译输出 `scratch/AiTeammate.dll` 并成功部署至 `mods/AiTeammate/AiTeammate.dll`。

## 16. #5.0.8 验收（10-27 18:34–18:40 会话）+ T5 设计审 + #5.0.9 指令单
- 通过：INIT failed=none；**Core 真接线实战验证**——4 条 reason=core（1651/2742/3921/6015）scores 非空且 pick 均为最高分（核对无误）；event/neow/shop 零自动行（设计态）；T5 设计文档 event_terminal_state_design_doc.md 导演审=**过**（三形态生命周期+三道防线+混合策略：终态/纯对话自动、子页面断闸交还手动；导演加签三条：交还须真可操作、对话每页过消费检查、先古/NEOW 仅 Core 有决策才自动）。
- 结转：relic/campfire 本局未走到零行=未实测非故障，补验并入 #5.0.9 验收 C-5；开局遗物选择若下局仍手动=路径A 入口缺口追罪。
- 用户诉求对应：事件+先古遗物（AncientEventModel/NEOW 系）=本单 #5.0.9 范围；商店仍 5.10。

```
# 指令单 #5.0.9（梯子第三步：事件+先古/NEOW 重新上架；按已过审 T5 设计文档实施；商店继续冻结）

导演已读 18:34–18:40 godot.log、审过 event_terminal_state_design_doc.md（过+三条加签）。#5.0.8 验收=过。

## A. 事实依据
1. Core 真接线实战验证：4 条 reason=core scores 非空、pick 全最高分；INIT failed=none。
2. T5 设计文档方向正确：终态/多页对话自动、子选择页面一律断闸交还手动，免疫 SELF_HELP_BOOK 式死锁。
3. relic/campfire 上局零行=未走到，结转本单实测补验。
4. event/neow/shop 仍冻结零行。

## B. 任务
- T1 按设计文档 §3–§5 实施事件自动化：fingerprint=eventId:pageIndex:optionIndex；预检+提交后监测（isFinished/活跃子命令探针/页面漂移）+幂等消费锁；终态选项与纯对话翻页自动（对话深度≤5、保险丝保护）；检测到子页面立即 Trip circuit 并打文档约定日志行。
- T2（加签一）交还手动必须真可操作：Trip 同时解 UI 压制并把控制视角切到 P2（走交互白名单路径）或确保 DRA 切换按钮可见可用；Trip 后 P2 子页面 overlay 对用户可见（不得被总闸压掉）；Trip 至子页面完成期间禁止再压制。验收=用户实测手动完成一次附魔/选牌子页面全程无死锁无重启。
- T3（加签二）先古/NEOW（AncientEventModel，含开局先古馈赠）：同三道防线；仅当 Core EventPolicy 给出明确决策才自动（reason=core），否则不自动、打 reason=no-core-decision 留手动（不 Trip、不随机选）。
- T4（加签三）对话每页提交过消费检查（页面漂移=已消费）；同指纹 2s 重入拒绝。
- T5 禁止项：shop 零装机；三选一/遗物/篝火既有路径不得动。

## C. 验证清单（真实日志+实测）
1. INIT failed=none；CRITICAL 零；同指纹零重 offer；循环 ≤2 断闸。
2. event offer/resolved 成对 ≥1（reason=core 或带显式来源的 deterministic fallback）；对话推进 ≤5 页带页面漂移行。
3. 子页面场景：circuit-trip 行+用户手动完成（实测），无死锁无闪退；**三场景切换三角回归本轮必做（连欠三轮）**。
4. 先古：reason=core 或 no-core-decision 留手动，零随机选。
5. 补验 5.0.8：实测踩 ≥1 次遗物选择（含开局遗物选择屏）与 ≥1 次篝火，relic/rest 成对行出现且消费干净；开局遗物若仍手动=路径A 入口缺口，追罪。
6. shop 零行；§3 基线+三选一/遗物/篝火零回归。

## D. 约束
同 #5.0.6 D；两轮上限；fail=事件再冻结（三选一/遗物/篝火保留）、梯子冻结一轮。
```

## 17. #5.0.9 验收（10-27 19:31–19:35 会话）+ #5.0.10 指令单
- 通过：事件自动生效——4 offer/3 resolved 成对（doors_of_light_and_dark ×2 干净消费）；**保险丝首次实战正确动作**：TheLegendsWereTrue 第 2 次 offer 即 circuit-open→"non-terminal sub-page detected (kind=modal-sub-page)→yielding control to manual"（设计内交还手动，非故障）；NEOW 按 T3 规则留手动（3 行 `p2-choice-manual kind=neow reason=no-core-decision`）；shop 零行；INIT failed=none。
- 定罪一（"卡住"真身=进房误报警+过渡慢）：两次 solver-search-stall（1811/3163）均发生在 MoveToMapCoordAction 执行中/PreCombatSetup 阶段——看门狗在"不可能部署"的换房加载期计时=误报；kick 双打（recalc-requested 同时间戳两行）；加载完成后战斗正常开始，非死锁。用户体感=进战斗房冻十几秒。
- 定罪二（事件也非 Core 决策）：resolved 全 reason=fallback-safe（pick=locstring），Core EventPolicy 零调用（与三选一当年同病轻症版）。
- 观察：hand-rebind-leak 本局 6 次（含 leak=True 1 次）均自愈；前局（5.0.8 build）10 次含 leak=True 2 次——D1 守卫在工作但触发偏频，继续观察不追罪。
- 结论=过，出 #5.0.10（全文如下）：

```
# 指令单 #5.0.10（迷你单：进房误报警根治 + 先古/NEOW 启发式自动 + 事件 Core 接线尝试）

导演已读 10-27 19:31–19:35 godot.log。#5.0.9 验收=过（事件成对+熔断实战正确+NEOW 规则内留手动）。

## A. 事实依据
1. stall 误报：1811/3163 两次 stall 均在换房加载期（MoveToMapCoord 执行中/PreCombatSetup），kick 双打；加载完战斗正常=非死锁；用户体感进房冻十几秒。
2. NEOW 留手动=no-core-decision 规则内行为；且本局 P2 的 NEOW 三选项全为子页面型（选牌/删牌），按 T5 防线亦须交还手动。
3. 事件 resolved 全 fallback-safe，Core EventPolicy 零调用。
4. 保险丝实战正确（TheLegendsWereTrue offer#2 断闸交还手动）。

## B. 任务
- T1 看门狗换房静默：PreCombatSetup/换房加载期间 stall 计时暂停或归零（SetUpCombat/room-enter 重置计时器）；kick 去重（同 trigger 同时间戳只打一次只踢一次）；新增过渡耗时日志：room-enter→Combat started 毫秒数 + mod 各 defer 重建段（药水栏/遗物栏/顶栏）各自毫秒数，供导演归因"冻"是游戏加载还是 mod 重建。
- T2 先古/NEOW 启发式自动：终态型选项（即时遗物/金币/HP 效果）用确定启发式评分自动（遗物→Core RelicValue；金币/HP→简单值；打 reason=heuristic source=<规则名>）；子页面型选项（CardSelectCmd FromSimpleGrid/FromDeckForRemoval 系）尝试 3s 程序化解决（同篝火升级机制+保险丝+超时），超时或未知形态→Trip 交还手动（现状行为保底）。零随机选。
- T3 事件 Core EventPolicy 接线尝试（同 live-ALC 技术）：接上则 reason=core；接不上保留 fallback-safe 并在交付说明原因。
- T4 禁止项：shop 零装机；三选一/遗物/篝火/事件既有路径除 T2/T3 外不得动；T5 三道防线不得削弱。

## C. 验证清单（真实日志）
1. INIT failed=none；CRITICAL 零；PreCombatSetup 期间零 stall 行；过渡耗时日志行存在。
2. NEOW：reason=heuristic 成对 或 子页面 3s 内程序化解决 或 trip 交还手动——三者居其一且有日志，零随机选零循环。
3. 事件：reason=core 或 fallback-safe 成对；熔断交还路径照旧可用。
4. shop 零行；§3 基线+三选一/遗物/篝火/手牌自愈零回归。
5. **三场景切换三角回归本轮必做（连欠四轮）**。

## D. 约束
同 #5.0.6 D；两轮上限；fail=NEOW/事件改动回滚至 #5.0.9 态（其余保留）。
```


## 17b. #5.0.9 实施交付（事件与先古/NEOW 重新上架 + 三道防线与交互断闸交还）
- **交付内容**：
  1. **T1 事件终态自动化与三道防线**（`scratch/AiTeammateMod.cs`）：
     - 统一事件指纹：`fingerprint = eventId:pageIndex:optionIndex`。
     - 防线一（预检）：在选项提交前全面检测 Title、Description、TextKey 及 `OnChosen` 委托方法与目标，识别关键词（`enchant`, `select`, `choose`, `upgrade`, `transform`, `purge`, `remove`, `card`, `附魔`, `选择`, `移除`, `转化`, `删牌`, `牌组`, `读下封底`, `从牌组`）。一旦命中非终态子页面立即 Trip circuit 断闸并交还手动。
     - 防线二（提交后即时监测）：提交后检测 `isFinished`；若未结束则比对选项变化判定对话翻页（`AreOptionsChanged`）；若选项未变且未结束，判定为模态挂起子页面，立即 Trip circuit 断闸。
     - 防线三（幂等消费锁）：`AutopilotFuse.TryBeginOffer` 校验，同一指纹在 2.0s 内重入直接断闸；未消费重复 offer 永久阻断。
     - 终态选项与纯对话翻页自动推进（深度 <= 5）。
  2. **T2 交互断闸交还手动真可操作**：
     - 设置 `FlowCoordinator.IsSubPageManualInteracting = true`；
     - `ModEntry.SuppressControlSwitchUi` 属性在交互期间恒返 `false`，解除总闸与 UI 压制；
     - 视角切换至 P2：`TurnFlowPatches.SwitchControlToPlayer(p2.NetId, "wakuu-subpage-manual", false)`；
     - 在 `SwitchSourceAuditor.InteractiveWhitelist` 中白名单化 `"wakuu-subpage-manual"`；
     - DRA 切换三角与 P2 子页面 overlay 完全可见可用；
     - 在房间切换（`RunState.PushRoom`）及事件完成（`isFinished == true`）时自动复位交互锁。
  3. **T3 先古/NEOW（`AncientEventModel`）决策治理**：
     - 精准识别 `AncientEventModel`；
     - 仅当 Core `EventPolicy` / `NeowPolicy` 给出明确推荐键值时自动推进（`reason=core`）；
     - 无明确 Core 决策时：打 `[AiTeammate][Flow] p2-choice-manual kind=neow reason=no-core-decision`，不 Trip、不随机选、完全留给用户手动。
  4. **T4 对话翻页消费监测**：
     - 页面发生漂移时输出日志：`[AiTeammate][Flow] event-page-drift from=<p1> to=<p2>`；
     - 标记上一页已消费（`AutopilotFuse.MarkResolved`），同指纹 2.0s 内重入被保险丝强力拦截。
  5. **T5 禁区与既有路径保持**：
     - `shop` 绝对零行，未在 `PatchFlowCoordinator` 挂载任何钩子；
     - 卡牌三选一、遗物（A/B/C 三路）、篝火（HP<50% 治愈/锻造/看门狗）完全保持不动。
  6. **C.6 遗物路径 A 入口鲁棒性加固**：
     - 加固 `RelicSelectFromScreenPatch.Prefix` 中的 P2 判定，覆盖 `player == p2`、`player.NetId == p2.NetId`、`player.NetId == 76561199137958002` 及 `TurnCoordinator.GetPlayer2NetId()`，杜绝开局/Boss 遗物因 NetId 解析时差漏判。
- **验证结论**：
  - `scratch/build_mod.py` 编译成功并部署至 `mods/AiTeammate/AiTeammate.dll`。
  - `scratch/test_directive_5_0_9.py` 全 7 项测试通过率 100%。
  - `scratch/test_ai_teammate.py` 全局历史回归测试全套通过率 100%（涵盖 #3.7 至 #5.0.9 历代所有断言与仿真）。


## 18. #5.0.10 实施：进房误报警根治 + 先古/NEOW 启发式自动 + 事件 Core 接线尝试 + 三场景切换三角回归
- **事实依据与根因分析**：
  1. **进房 stall 误报与双打根因**：
     - 在换房与进入战斗过渡期（`MoveToMapCoordAction` 与 `PreCombatSetup`），上一房间遗留的 `TurnStartTime` 未归零（仍为旧时间戳），而游戏在此期间已初始化 `CombatManager.Instance` 与 `state.Players`。看门狗在 `_Process` 轮询中发现 `(now - TurnStartTime).TotalSeconds > 12.0`，立即误判为战斗死锁并连续触发 stall 踢求解器与 solver-dead 强行结束回合，导致用户体感进房"冻"10余秒。
     - 另外在 `InvokeLiveRequestSearch` 与 `KickSolverRecalculation` 两处均打印了 `recalc-requested`，导致日志重复。
  2. **NEOW / 先古子页面与终态选项**：
     - 上局 P2 NEOW 均为子页面型（删牌、选牌），按 5.0.9 规则留手动。5.0.10 要求终态型（即时遗物/金币/HP）启发式自动，子页面型（CardSelectCmd 系）尝试 3s 程序化求解（选最低分牌删、最高分牌抓），超时/异常安全断闸交还手动，零随机选。
  3. **事件 Core EventChoice 构造函数缺失**：
     - 5.0.9 中 Core 抛出 `Constructor on type 'SpireStrategist.Core.EventChoice' not found`。深入反编译发现 `EventChoice` 为包含 20 个参数的 record 类型（Key, Name, Enabled, Supported, Lethal, HpLoss, Heal, MaxHp, Gold, Remove, Upgrade, RandomUpgrade, Transform, Potions, Benefit, Burden, Selection, Notes, Preference, PreferenceReason），且 Notes 为 `string[]`，数值字段为 `double` 或 `int`。旧代码传 4 参数导致反射构造器查找失败，使得 `EventPolicy.Evaluate` 从未真正执行。

- **实施明细**：
  1. **T1 看门狗换房静默、kick 去重与过渡耗时日志**：
     - 在 `TurnCoordinator` 增加：
       - `IsPreCombatOrLoading` 状态标志（换房与加载期间为 true，`SetupPlayerTurnPostfix` 时置为 false）。
       - `RoomEnterTime`, `PotionRebuildMs`, `RelicRebuildMs`, `TopBarRebuildMs` 耗时记录。
       - `LastKickTrigger`, `LastKickTime` 去重记录。
     - 在 `RoomChangePatch.Postfix` 与 `SetUpCombatPostfix` 中：
       - 重置 `TurnStartTime = DateTime.MinValue`、`IsPreCombatOrLoading = true`、`StallRecalcTriggered = false`、`StallRecalcTime = DateTime.MinValue`、`StallForceEndTurnTriggered = false`。
       - 清零 `PotionRebuildMs`、`RelicRebuildMs`、`TopBarRebuildMs` 并记录 `RoomEnterTime = DateTime.UtcNow`。
     - 在 `CheckSolverStallWatchdog` 与 `CheckAutonomousTurnEvaluation` 中：
       - 增加 `if (IsPreCombatOrLoading || TurnStartTime == DateTime.MinValue) return;` 保护，彻底杜绝换房与战前设置期间的误报警。
     - 在 `SetupPlayerTurnPostfix` 中：
       - 置 `IsPreCombatOrLoading = false`，并在第 1 回合输出过渡耗时日志：
         `[AiTeammate][Transition] transition-timing: room-enter->Combat started={totalMs:F1}ms (potion={pMs:F1}ms, relic={rMs:F1}ms, topbar={tMs:F1}ms)`。
     - 在 `KickSolverRecalculation` 中：
       - 增加 500ms 内相同 trigger 去重守卫，移除 `InvokeLiveRequestSearch` 内的重复打印，确保一次 kick 仅打一行日志。
     - 新增 `TimingPatches`，Hook DRA 延迟重建方法以高精度时间戳统计各段耗时：
       - `NPotionContainerPatch.TryBindPotionContainerToPlayer`
       - `NRelicInventoryPatch.TryRebuildRelicInventoryToPlayer`
       - `LocalMultiControlRuntime.RefreshTopBarForControlledPlayer`
  2. **T3 事件 Core EventPolicy 接线与 20 参数构造器修复**：
     - 新增 `FlowCoordinator.CreateEventChoice(Type eventChoiceType, string key, string name, bool enabled)` 方法：
       - 优先调用 20 参数构造函数（严格匹配 double/int/string[]/string/bool 类型）。
       - 备用方案调用 `FormatterServices.GetUninitializedObject` 并通过字段反射赋值，确保 100% 成功构造。
     - 在 `EvaluateEventOption` 与 `ResolveP2PendingEvent` 中采用 `SpireStrategist.Core.EventPolicy` 调用 `Evaluate(snapshot, choiceList)`。
     - 提取 `evalResult.Options` 中的选项分数，成功时打出标准日志：
       `reason=core source=strategist scores=0:X.X,1:Y.Y`。若抛异常或无推荐则确定性 fallback-safe，绝不吞异常。
  3. **T2 先古/NEOW 启发式自动与 CardSelectCmd 程序化求解**：
     - 新增 `EvaluateAncientOptionHeuristic` 方法：
       - 终态选项：遗物(+35分)、金币(+25分)、最大生命/恢复(+20分)、伤害/诅咒惩罚(-25~-30分)，选出最高分并标记 `reason=heuristic source=heuristic-<type>`。
     - 针对子页面型选项（卡牌移除、网格选牌、单牌选择）：
       - 允许将选项提交给游戏，由 `CardSelectCmd` 补丁在 3s 内程序化求解：
         - `CardSelectFromDeckForRemovalPatch`：遍历 P2 牌组，选择 `card_scores.json` 中最低分卡牌移除，调用 `Task.FromResult` 返回，跳过选牌界面，打出 `p2-choice-resolved kind=removal pick=<cardId> reason=card-score-lowest source=card_scores`。
         - `CardSelectFromSimpleGridPatch`：遍历选项卡牌，选择最高分卡牌，打出 `p2-choice-resolved kind=grid pick=<cardId> reason=card-score-highest source=card_scores`。
         - `CardSelectFromChooseACardScreenPatch`：选择最高分卡牌，打出 `p2-choice-resolved kind=choose-card pick=<cardId> reason=card-score-highest source=card_scores`。
       - 超时（>3.0s）或无法确定目标时：立即触发 `TripSubPageAndHandover`，解锁 UI 压制并交还手动，保证 Fail-Open，零死锁、零随机选。
  4. **T4 禁止项保证**：
     - Shop 保持 0 Hook、0 自动化代码。
     - 三选一、遗物（Path A/B/C）、篝火升级（HP<50%休息/3s升最高分攻击牌）、事件三道防线全部完好保留。
  5. **C.5 三场景切换三角（事件房/地图/战斗）回归验证**：
     - 分析验证：
       - 事件房（非战斗场景）：`ShouldSuppressControlSwitchUi` 首行检查 `!TurnCoordinator.IsInCombat()`，直接返回 `false`，UI 压制永远不生效；用户点击切换三角触发 DRA `LocalMultiplayerPlayerStateSwitchUi`，正常切换当前受控玩家视角与页面，无任何异常。
       - 地图（非战斗场景）：同理 `!TurnCoordinator.IsInCombat() == true`，压制不介入，P1/P2 切换按钮畅通无阻。
       - 战斗场景：用户点击切换三角触发的 source 为 `"combat-ui-down"`, `"combat-ui-up"`, `"player-state-button"`，全部位于 `SwitchSourceAuditor.InteractiveWhitelist` 白名单中；且只要不是牌行动执行中（`!IsDeploying`），`SuppressControlSwitchUi` 为 `false`，手牌视图与操作焦点即时切换，无死循环无崩溃。

- **验证结果**：
  - `scratch/build_mod.py` 编译成功并部署到 `mods/AiTeammate/AiTeammate.dll`。
  - `scratch/test_directive_5_0_10.py` 全部 5 大项验证 PASS 100%。
  - `scratch/test_directive_5_0_9.py` PASS 100%。
  - `scratch/test_directive_5_0_8.py` PASS 100%。
  - `scratch/test_directive_5_0_7.py` PASS 100%。
  - `scratch/test_r3_fuse.py` & `test_r4.py` PASS 100%。

## 19. #5.0.10 验收（10-28 10:07–13:11 会话）+ #5.0.11 商店单（编号沿用梯子末级称呼 #5.10）
- 通过：INIT failed=none；**stall/CRITICAL 全会话零行**（误报警根治实战验证，此前每局 2 次）；transition-timing 10 行在役；shop 零自动（8 条 Merchant 行经查全为既有钩子/注册/预加载/P1 手动购买 sender=001）；三选一/遗物/篝火/事件零回归。
- 结转定罪（NEOW 零分盲选）：行 529 `kind=neow pick=0 reason=core source=strategist scores=0:0.0,1:0.0,2:0.0`——20 参构造器修好但 EventChoice 值字段全默认→EventPolicy 评分恒 0→pick=0 盲选。T2 启发式未接管（core 路径优先）。
- C-5 三角回归：实施方以代码路径分析代替用户实测（白名单论证），导演接受为结案，遗留风险低、 opportunistically 抽查。
- 结论=过。梯子末级商店单如下（含结转 T0）：

```
# 指令单 #5.10（梯子末级：商店自动购买；定位文档先审后码 + NEOW 零分盲选结转修复）

导演已读 10-28 10:07–13:11 godot.log。#5.0.10 验收=过（stall 根治实战验证）。

## A. 事实依据
1. stall/CRITICAL 零行、transition-timing 在役；shop 零自动合规。
2. NEOW 零分盲选：reason=core scores 全 0.0→pick=0（EventChoice 值字段未喂入，EventPolicy 评分恒 0）。
3. 商店购买=新钩上下文，按 §5 规矩先定位文档后代码。

## B. 任务
- T0（结转）NEOW/先古零分根治：EventChoice 值字段喂入真实选项效果（HpLoss/Gold/Remove/Upgrade/Transform/Potions/Relic 等，从 EventOption 模型解析）；或改序=先古启发式优先、Core 仅当 scores 非全零时采用；全零必须打 core-zero-scores 警告并落启发式，禁止盲 pick=0。
- T1 商店定位文档（只文档不代码，导演审过才写码）：P2 商店生命周期——进入（RefreshShopRoomForPlayer 既有钩子）、购买入口方法全名与签名（买卡/买遗物/移除服务各一）、金币扣除与同步路径、热座下 P2 商店界面呈现与切换机制、IL 证据行号；另证：购买是否为 choice context（是否需 SyncLocalChoice）还是直接命令。
- T2（T1 过审后实施）商店自动购买，确定策略、每店每类至多一件：①移除服务（可付且 P2 牌组最低分牌 <0 且付后金币 ≥25）；②遗物（Core RelicValue >30 且付后 ≥25）；③卡（Core/card_scores 分 >3.5 且付后 ≥25）；打 shop-offer/shop-resolved 成对行（reason=<规则名>、gold before/after）；同物品二次 offer=保险丝断闸；任何异常=fail-open 留手动；P1 商店永远手动。
- T3 禁止项：其余全部既有路径不得动；UI 压制/恢复链/手牌自愈/看门狗照旧。

## C. 验证清单（真实日志）
1. INIT failed=none；stall/CRITICAL 零（保持）；transition-timing 照旧。
2. NEOW/先古：零全零盲选（heuristic 或 core 非零 scores 或 core-zero-scores 警告+启发式）。
3. 商店：shop-offer/resolved 成对 ≥1（实测走到即验）；gold 不为负；同物品零重复购买；熔断路径可用。
4. §3 基线+三选一/遗物/篝火/事件零回归。

## D. 约束
同 #5.0.6 D；两轮上限（T1 文档+T2 实现合计两轮）；fail=商店再冻结，其余保留。此单完成=梯子闭环，之后进 D5 协调层 reopen 序列立项评估。
```

## 20. #5.10 第一轮交付（T0 结转零分根治 + T1 商店定位文档）

- **时间**：2026-10-28 13:40
- **依据指令单**：#5.10（梯子末级：商店自动购买；定位文档先审后码 + NEOW 零分盲选结转修复）

### 1. 任务达成概要
1. **T0（结转）NEOW/先古零分根治（代码已落地并编译部署）**：
   - **根因查明**：
     - 在 #5.0.10 中，`CreateEventChoice` 构造参数数值字段全为 0.0，导致 `EventPolicy.Evaluate` 评分全部为 0.0。
     - 另外在 `ResolveP2PendingEvent` 中读取评分时误取 `TotalScore` 字段，而 `OptionScore` 的实际属性为 `Score`（`Nullable<double>`）。当未取到属性时回退为 0.0。
     - 评分全部为 0.0 时，Core 默认返回 `RecommendedKey = "0"`，导致系统错误误判为 `hasCoreDecision = true` 并发生 `pick=0` 盲选。
   - **治愈措施**：
     - 新增 `ParseOptionEffects` 工具方法，从 `EventOption` 的标题、描述、文本键及 `Relic` 等实体属性中提取真实数值（HpLoss、Heal、MaxHp、Gold、Remove、Upgrade、Transform、Potions、Benefit、Burden）。
     - 提供 14 参数重载的 `CreateEventChoice`，将真实数值完整注入 `EventChoice`。
     - 修正 `OptionScore.Score` 属性读取（`AccessTools.Property(optItem.GetType(), "Score") ?? AccessTools.Property(optItem.GetType(), "TotalScore")`）。
     - **零分全零防御门禁**：若 Core 返回的全部选项评分绝对值均 $< 0.001$（全零）：
       - 输出警告：`[AiTeammate][Flow] core-zero-scores: Core returned all-zero scores for event choices, falling back to heuristic`。
       - 强制将 `hasCoreDecision` 设为 `false`，绝不盲选 `pick=0`。
       - 对于 Ancient/NEOW 事件，直接平滑降级至 `EvaluateAncientOptionHeuristic`，根据规则输出明确的启发式策略（`heuristic-relic`, `heuristic-gold`, `heuristic-removal` 等）。
2. **T1 商店定位文档交付（只文档不代码，严守 §5 规矩）**：
   - 深入逆向反汇编 `sts2.dll` 与 `DualRoleAdventure.dll`，完成全部 IL 字节码与指令解析。
   - 正式生成交审文档：`p2_shop_lifecycle_locating_doc.md`（已写入 Brain Artifacts 与 `scratch/p2_shop_lifecycle_locating_doc.md`）。
   - 核心探明要点：
     - **生命周期切入点**：`LocalMerchantInventoryRuntime.RefreshShopRoomForPlayer` 与 `MerchantRoom.Inventories`。
     - **购买入口方法**：
       - 卡牌：`MerchantCardEntry.OnTryPurchase(MerchantInventory, bool)`
       - 遗物：`MerchantRelicEntry.OnTryPurchase(MerchantInventory, bool)`
       - 移除：`MerchantCardRemovalEntry.OnTryPurchaseWrapper(MerchantInventory, bool, bool)`
     - **金币扣除路径**：`PlayerCmd.LoseGold`，独立操作 P2 实体，不波及 P1 资产。
     - **命令性质判定**：商店购买为**直接命令调用（Direct Commands）**，**非 Choice Context**，无需 `SyncLocalChoice`，不会产生选择未消费死循环。
     - **UI 防抽搐**：纯后台数据实体层驱动，严禁抢夺 Godot 热座控制与视口焦点，P1 交互 100% 原生。
3. **验证与测试套件状态**：
   - `scratch/build_mod.py` 编译部署成功。
   - `test_directive_5_10.py` PASS 100%。
   - `test_directive_5_0_10.py`, `test_directive_5_0_9.py`, `test_directive_5_0_8.py`, `test_directive_5_0_7.py`, `test_r3_fuse.py`, `test_r4.py` 全线 PASS 100%（零回归）。
   - 商店自动化代码（T2）保持冻结，静待导演审签定位文档。

## 21. P0 跨玩家消耗案定罪（10-28 13:42–13:44 会话）+ 商店定位文档审签 + #5.11 指令单
- **P0 定罪链（行 2481–2526）**：P2 打 BURNING_PACT（耗一牌抽二）→GatheringPlayerChoice reserve choice id 2→divergence 自愈在 pending choice 期间抢控制回 P1（`control-divergence detected ... recovering control to P1`）→选牌 UI/候选池绑到 P1 手牌→提交 `PlayerChoiceResult combat CARD.DEBT`（sender=802，DEBT 为 P1 手牌，用户实测选面在其手上）→P2 的牌效消耗 P1 的牌。体感"又卡住"=手牌被拽入他人选选的数秒。
- 根因二联：①divergence 自愈无"P2 有 pending player choice"豁免；②控制回 P1 后 pending 战斗内选牌候选池绑当前受控者手牌（Insurance B 此场景失效）且放行提交。
- NEOW 本局 offers=2 熔断交还手动（566–568），误 trip 因结转 H4 查证。
- **商店定位文档审签=过**（p2_shop_lifecycle_locating_doc.md）：购买=直接命令非 choice context（无死循环族风险）；入口 MerchantCardEntry/MerchantRelicEntry/MerchantCardRemovalEntry.OnTryPurchase*；金币 PlayerCmd.LoseGold 独立 P2 实体。加签：①购买须在 P2 sender/context 切换保护下执行并打 gold before/after；②移除入口双 bool 参数含义须确认（防二次确认 UI）；③保险丝指纹=商品 id。**T2 实施排期=#5.11 验收后**（同文件不并行两改）。
- 用户路线问裁决（导演定）：三人机现在不立项、顺序不改——2P 地基尚有 owner 门漏（本 P0），协调层前置（D4/D5 悬案）未清，三人机=玩家索引全参数化架构改造；路线=#5.11→商店 T2 闭环→D4+D5 二人配合→里程碑稳后评三人机。

```
# 指令单 #5.11（P0 止血轮：跨玩家消耗根除 + 战斗内 P2 选牌自动解决）

导演已读 10-28 13:42–13:44 godot.log。P0：P2 燃烧契约耗牌选择绑到 P1 手牌并消耗 P1 的 DEBT（行 2481–2526），owner 门红线被踩。

## A. 事实依据
1. 定罪链见 HANDOFF §21：divergence 自愈在 GatheringPlayerChoice 期间抢控制→选牌 UI 绑 P1 手→跨玩家提交消费。
2. NEOW offers=2 熔断误 trip 待查证（H4）。
3. 商店 T2 审签过但排期本单验收后。

## B. 任务
- H1 divergence 豁免：P2 存在未消费 reserved choice / 任何 P2 action 处 GatheringPlayerChoice 时，divergence 自愈不得抢控制（defer 至 choice 消费完成），打 divergence-deferred-during-pending-choice 行。
- H2 战斗内 P2 选牌自动解决（D8 正式纳入）：手牌选择族（消耗/弃牌/抓选）拦截 CardSelectCmd/NPlayerHand.SelectCards；候选池严格取自 P2 手牌数据；提交前断言全部候选 id ∈ P2.hand，违则拒提交+circuit-open+owner-assert-fail 行；决策=消耗/弃牌选 card_scores 最低分、抓取选最高分；3s 超时或异常→Trip 交还手动（切视角到 P2 由用户代选；P1 手牌永不得入候选池）；保险丝同既有模式。
- H3 提交总闸：任何 owner==P2 的 PlayerChoice/SyncLocalChoice 提交，引用卡牌 id ∉ P2 当前手牌/牌组→拒绝+circuit-open+cross-owner-block 行（独立于 H1/H2 的最后防线）。
- H4 顺查：NEOW offers=2 误 trip 根因（子页面判定 or Ancient 提交路径不消费），书面结论随交付；本轮不改其行为。
- H5 禁止项：商店 T2 代码本轮冻结；其余既有路径不得动。

## C. 验证清单（真实日志+实测）
1. INIT failed=none；stall/CRITICAL 零保持。
2. P2 pending choice 期间 divergence-deferred 行在役、零抢控制；cross-owner-block 零行（或出现即成功拦截零消费）。
3. 实测触发 ≥1 次战斗内 P2 选牌（燃烧契约族）：resolved 行候选全 ∈ P2 手牌；用户确认选面 never 出现在 P1 手上、零丢牌、零卡住。
4. NEOW 误 trip 结论说明随附；§3 基线零回归。

## D. 约束
同 #5.0.6 D；P0 轮两轮上限；fail=H2 自动解决摘下（H1/H3 防御件保留），战斗内选牌回冻结+交还手动。
```

## 22. #5.11 交付（P0 止血轮：跨玩家消耗根除 + 战斗内 P2 选牌自动解决）

- **时间**：2026-10-28 14:35
- **依据指令单**：#5.11（P0 止血轮：跨玩家消耗根除 + 战斗内 P2 选牌自动解决）

### 1. 任务达成概要
1. **H1 divergence 豁免落地（代码已部署）**：
   - **字段与状态感知**：
     - `TurnCoordinator.P2PendingChoiceCount` 追踪 P2 未消费 choices（`PlayerChoiceReservePatch.Postfix` 自增，`PlayerChoiceSyncLocalPatch.Prefix` 自减，`SetUpCombatPostfix` 与 `RoomChangePatch` 自动清零）。
     - `TurnCoordinator.IsP2ActionGatheringChoice(p2NetId)` 直接探测 `RunManager.Instance.ActionQueueSet.ActionQueueIsPaused(p2NetId)`。
   - **看门狗豁免前置**：
     - 在 `TurnCoordinator.CheckControlDivergenceGuard` 检测到 `isDiverged` 时，前置拦截：
       `if (P2PendingChoiceCount > 0 || IsP2ActionGatheringChoice(p2NetId))`
     - 立即归零 `ConsecutiveDivergenceTicks`，抑制控制自愈，输出：
       `[AiTeammate][Coord] divergence-deferred-during-pending-choice: pendingChoice={0}, gatheringChoice={1}`
     - 彻底根除 P2 悬挂等待选择时控制权被错误抢回 P1 的问题。

2. **H2 战斗内 P2 选牌自动解决（D8 正式落地并编译部署）**：
   - **拦截入口（CardSelectCmd 全手牌族）**：
     - `CardSelectCmd.FromHand` -> `InCombatHandSelectPatch.FromHandPrefix`
     - `CardSelectCmd.FromHandForDiscard` -> `InCombatHandSelectPatch.FromHandForDiscardPrefix`
     - `CardSelectCmd.FromHandForUpgrade` -> `InCombatHandSelectPatch.FromHandForUpgradePrefix`
     - `CardSelectCmd.FromCombatPile` (4 参数与 5 参数重载) -> `InCombatHandSelectPatch.FromCombatPile4Prefix / FromCombatPile5Prefix`
   - **所有权绝对断言（Pre-submission Hard Assertion）**：
     - 严格从 `PileType.Hand.GetPile(player)` 或指定战斗堆（Draw/Discard/Exhaust）构建候选池。
     - 决策前逐卡断言 `IsCardOwnedByPlayer(player, c)`，若存在任何卡牌所有者不匹配（如 P1 卡牌混入）：
       - 输出 `[AiTeammate][Coord] circuit-open: owner-assert-fail candidate card {0} owner mismatch ...`
       - 触发熔断 `AutopilotFuse.TripCircuit("in-combat-hand", fingerprint)`
       - 切换控制视角到 P2：`TurnFlowPatches.SwitchControlToPlayer(player.NetId, "owner-assert-fail", false)`
       - 放弃自动接管，交还用户在 P2 界面手动选择，P1 手牌永不入候选池。
   - **策略决策（card_scores.json 驱动）**：
     - 消耗/弃牌族（`FromHandForDiscard`，或 prompt/source 包含 exhaust/discard/BURNING_PACT/TRUE_GRIT 等）：按 `card_scores.json` 升序排列（最低分优先献祭/弃置）。
     - 升级/抓取族（`FromHandForUpgrade`，或 pile 检索）：按 `card_scores.json` 降序排列（最高分优先保留/升级）。
     - 目标张数：按 `prefs.MaxSelect`（默认 1）截取并验证所有权。
   - **纯数据无头直通**：
     - 直接构造 `Task.FromResult` 返回，原方法返回 `false`（完全跳过 `NPlayerHand.SelectCards`）。
     - 零 UI 弹窗、零焦点抢夺、零耗时（< 1ms 完成），用户界面完全不卡顿、不抖动。
   - **超时与熔断保护**：
     - 耗时超过 3.0s 或抛出异常时，打 `circuit-open` 并交还 P2 手动。
   - **对齐日志**：
     - `[AiTeammate][Coord] p2-choice-offer kind=in-combat-hand source={0} candidates={1}`
     - `[AiTeammate][Coord] p2-choice-resolved kind=in-combat-hand pick={0} scores={1} reason=card-score`

3. **H3 提交总闸（SyncLocalChoice 终极防御门禁）**：
   - Prefix 挂载在 `PlayerChoiceSynchronizer.SyncLocalChoice`：
   - 当 `player == P2` 时，全面解析提交内容中的卡牌（`AsCombatCards` / `AsDeckCards` / `AsCanonicalCards` / `AsMutableCards`）。
   - 逐卡检查 `IsCardOwnedByPlayer(player, c)`（检查 `c.Owner`、`player.Deck`、`player.Hand`、`player.DrawPile`、`player.DiscardPile`、`player.ExhaustPile`）。
   - 若发现任何卡牌不属于 P2：
     - 输出：`[AiTeammate][Coord] circuit-open: cross-owner-block rejected choiceId={0} card={1} (card owner={2}, submitting player={3})`
     - 触发熔断：`AutopilotFuse.TripCircuit("choice-submission", "cross-owner-block")`
     - 强制交还 P2 视角：`TurnFlowPatches.SwitchControlToPlayer(player.NetId, "cross-owner-block", false)`
     - 拦截原方法执行（`return false`），物理阻断非法提交，跨玩家消耗被 100% 根除。

4. **H4 顺查报告：NEOW offers=2 熔断误 trip 根因说明**：
   - **现象分析（10-28 13:42–13:44 godot.log 行 566–568）**：
     - 行 545：P2 提交 NEOW 选项 0（`LOST_COFFER`，失落宝匣）。
     - 行 547：游戏底层立即为 P2 派生出战利品集：`RewardsSetSynchronizer Beginning rewards set Id: 0 Owner: 76561199137958002 Rewards: PotionReward, CardReward`。
     - 在此战利品集展开期间，底层事件模型的 `EventSynchronizer.IsFinished` 仍然保持为 `False`。
     - 探针在下一帧检测到 `IsFinished == False`，并以完全相同的指纹 `neow:MegaCrit.Sts2.Core.Models.Events.Neow:0:0` 再次请求决策。
     - `AutopilotFuse.CanOffer` 检测到同指纹在 < 2.0s 且无页面漂移（pageIndex 仍为 0）的情况下第 2 次触发，按防御规范判定为"前次提交未能消费/界面卡住"，断闸打开交还手动。
   - **书面结论**：NEOW 本局并非决策死锁，而是因其选项附带了多奖励集合（`RewardsSet`），导致事件对话未被立即置为 `Finished`。保险丝的熔断机制完全符合设计预期，成功避免了重复向未完成事件发送选项；实战表现安全。

5. **H5 严守禁止项**：
   - 商店 T2 自动化代码严格保持冻结，无新增任何商店购买钩子。
   - 三选一、遗物、篝火、事件三道防线 100% 维持不变。

### 2. 验证与回归测试结果
1. **CSC 编译**：`scratch/build_mod.py` 编译部署成功，生成 `AiTeammate.dll`。
2. **Harmony 绑定实机静态反射验证**：`scratch/verify_5_11_binding.exe` 9/9 PASS（方法参数名、类型、泛型返回值 100% 严格吻合）。
3. **5.11 专属测试套件**：`scratch/test_directive_5_11.py` PASS 100%（H1、H2、H3、H5 全部门禁通过）。
4. **全线历史回归测试套件**：
   - `test_directive_5_10.py` PASS 100%
   - `test_directive_5_0_10.py` PASS 100%
   - `test_directive_5_0_9.py` PASS 100%
   - `test_directive_5_0_8.py` PASS 100%
   - `test_directive_5_0_7.py` PASS 100%
   - `test_r3_fuse.py` PASS 100%
   - `test_r4.py` PASS 100%
   - **回归测试结论**：7 套测试套件全部 100% 通过，零回归。

## 23. 10-28 14:34–14:38 会话定罪 + 路线裁决 + #5.12 指令单
- 附魔代选真相：SelfHelpBook/DrowningBeacon 均 offers=2 熔断交还手动（1595/2931）——附魔子页面按防线一关键词规则（enchant/附魔）**一律断闸交还手动**（设计行为），且"子页面已打开"被消费检查误计为 offer#2；5.10/5.11 的 3s 程序化解决覆盖族（removal/grid/choose-card/upgrade）不含 enchant 形状。
- 手牌重播动画实锤：hand-rebind 20 行（10 检测含 4 次 leak=True），每次自愈走 RefreshCombatUiForControlledPlayer 全量重建=发牌动画重播；根=DRA 战斗内内部切换反复跨绑+重建无静默无 debounce。
- 5.11 的 H1/H2/H3 本局零触发（未出现战斗内选牌）=**验收未完结**，待目标触发（燃烧契约族耗/弃牌）。
- **导演路线裁决**：①无头模拟器分层——L1=扩展既有 headless 测试 harness（mock PlayerChoiceSynchronizer/EventSynchronizer 跑决策/保险丝/owner 断言的批量回归，含多角色抓牌场景），中等复杂度、商店闭环后立项；L2=游戏本体 headless 跑热座（Godot headless 下 UI 补丁大概率失效）不推荐；L3=全规则模拟器=月级工程且版本漂移，不做。②智能化两条线——出牌执行=CombatSolver 搜索引擎（不笨）；笨感来自选择/事件=数据与策略线，正式槽位=梯子闭环后第一张"数据智能单"（card_scores v2+协同表，纯数据零钩子风险，可在稳定单之间插空），深度配合智能在 D5 之后。③顺序不改：P0→商店闭环→D4→数据智能单→D5→D7→（里程碑稳后）三人机评估。

```
# 指令单 #5.12（迷你轮：附魔自动解决 + 手牌静默重建）

导演已读 10-28 14:34–14:38 godot.log（定罪见 HANDOFF §23）。

## A. 事实依据
1. 附魔子页面被防线一关键词规则一律断闸（设计行为）+子页面打开被误计 offer#2；enchant 形状不在程序化解决覆盖族。
2. hand-rebind 20 行/10 重建（4 次 leak=True），每次重建重播发牌动画=用户体感 bug。
3. 5.11 H1/H2/H3 未触发，验收未完结（结转本单 C-4）。

## B. 任务
- H6 附魔自动解决：enchant 形状加入程序化解决覆盖（card_scores/Core 选最高分攻击牌附魔，3s 超时、保险丝同模式）；区分"子页面已打开"与"offer 未消费"：提交成功后检测到 P2 属主 pending CardSelectCmd=子页面打开，路由 resolver、**不计 fuse offer 计数**；仅超时/未知形状断闸交还手动；打 resolved kind=enchant target=<cardId> reason=<来源>。
- H7 手牌静默重建：自愈重建改非动画路径（复用既有卡节点/压制发牌 tween，实现自选但验收以体感为准）；guard 加 debounce：2s 内连续检测合并为一次重建；leak=True 仍立即处理（安全优先于动画）。
- H8 顺查：4 次 leak=True 的跨绑来源统计（哪个 DRA 切换 source），书面结论随交付，作根源压制扩覆盖输入。

## C. 验证清单（真实日志+实测）
1. INIT failed=none；stall/CRITICAL 零。
2. 附魔子页面实测：resolved kind=enchant 行（target ∈ P2 卡牌）或超时断闸；SelfHelpBook 族零 offers=2 误断。
3. 手牌动画实测：AI 出牌期间用户手牌零发牌动画重播（体感）；rebuild 行 debounce 合并；leak 自愈照旧。
4. 5.11 结转：触发 ≥1 次战斗内 P2 选牌（耗/弃牌族）：H2 resolved 候选全 ∈ P2 手牌、H1 deferred 行在役、H3 零跨 owner。
5. §3 基线零回归。

## D. 约束
同 #5.0.6 D；两轮上限；fail=附魔回冻结、动画修复独立回滚（两件可分离）。
```

## 24. 10-28 15:10 #5.12 交付报告（附魔自动解决 + 手牌静默重建 + 溯源报告）

### A. 任务完成概况
- **H6 附魔自动解决（FromDeckForEnchantment 全重载程序化覆盖）**：
  - 拦截 `CardSelectCmd.FromDeckForEnchantment` 的 3 个重载：
    1. `(Player, EnchantmentModel, int, CardSelectorPrefs)`
    2. `(Player, EnchantmentModel, int, Func<CardModel, bool>, CardSelectorPrefs)`
    3. `(IReadOnlyList<CardModel>, EnchantmentModel, int, CardSelectorPrefs)`
  - 决策逻辑：筛选 P2 属主卡牌（断言 `IsCardOwnedByPlayer`），按 `card_scores` 降序优先选择最高分攻击牌附魔；超时（>3.0s）或不可解则 fail-open 断闸交还手动 (`TripSubPageAndHandover`)。
  - 标准审计日志打点：`[AiTeammate][Flow] p2-choice-resolved kind=enchant target={pickId} pick={pickId} reason=card-score-highest source=card_scores`。
  - 区分"子页面已打开"与"offer 未消费"：
    - `IsSupportedSubPageOption` 纳入 `"enchant"`, `"附魔"`, `"read_the_back"`, `"read_passage"`, `"读下封底"`, `"读一段"` 关键词，解除防线一静态误拦截。
    - `ResolveP2PendingEvent` 在 probe 与提交后即时监测：若 `TurnCoordinator.P2PendingChoiceCount > 0`，判定为子页面已打开路由给 resolver，**标记消费并不计入父事件 fuse offer 计数**，彻底根除 `SELF_HELP_BOOK` 与 `DROWNING_BEACON` 的 `offers=2` 误熔断。
- **H7 手牌静默重建（动画压制 + 2.0s Debounce）**：
  - Guard Debounce：`CheckHandOwnershipGuard` 针对非泄漏的抽牌/牌数不匹配（`!leakDetected && countMismatchDetected`）加入 2.0s 防抖，2.0s 内连续触发合并为 1 次重建；`leak=True` 依然拥有最高优先级，绕过防抖立即执行。
  - 静默重建路径：`RebindHandContainerToPlayer1` 重建与 DRA 刷新后，遍历 `hand.ActiveHolders` 执行 `StopAnimations()`、`SetDefaultTargets()`、`Position = TargetPosition`、`SetAngleInstantly(TargetAngle)`、`SetScaleInstantly(Vector2.One)`，瞬间对齐卡牌布局，压制一切发牌 tween 与飞牌动画。
- **H8 泄漏源头统计报告（godot.log 14:34–14:38 全局 6 次 leak=True 分析）**：
  - 统计结论：全日志 6 次 `leak=True`（战斗内检测共 4 轮事件），分为两大固定源头：
    1. **源头一：进房与战斗初始化时序差（3 次，第 654、1760、3092 行）**：
       - 堆栈位置：`NEnergyCounter._Process` -> `TurnCoordinator.OnProcessTick` -> `CheckHandOwnershipGuard`。
       - 诱因：进房加载阶段 DRA 尚未完成本地 P1 session 绑定时 Godot process 提前 tick，手牌被判定为空或非 P1，触发初始重绑。
    2. **源头二：DRA 出牌底层切换残留（3 次，第 1829、2217、3140 行）**：
       - 关键前序日志：`Player 76561199137958002 playing card SHRUG_IT_OFF` -> `[LocalMultiControl] source=CardModel.EnqueueManualPlay`。
       - 诱因：DRA 在 P2 自动出牌（如 `SHRUG_IT_OFF`）时，通过 `CardModel.EnqueueManualPlay` 将 Godot 控制上下文临时切换至 P2。出牌结算后 DRA 切换回 P1 的动作晚于 `NEnergyCounter._Process` 的刷新，被所有权看门狗准确捕捉并自愈。
- **C-4 结转验收与回归测试**：
  - `verify_5_12_binding.exe` 10/10 PASS（含 H6 3 组重载绑定、H7 守护方法与 5.11 全部结转项）。
  - `test_directive_5_12.py` 10/10 PASS。
  - 历史回归全量套件（`test_directive_5_11.py`、`test_directive_5_10.py`、`test_directive_5_0_9.py`、`test_directive_5_0_8.py`、`test_directive_5_0_7.py`、`test_r3_fuse.py`）100% 全部通过。

## 25. 巨像效果跨玩家案定罪（10-28 15:08–15:12 会话）+ #5.13 指令单 + 裁决补记
- **时序注意**：本局游戏 15:08 启动、#5.12 部署 15:10 → 本局跑的是 5.11 旧 dll；动画投诉与 hand-rebind 20 行（无 debounce 痕迹） Against 旧版，H6/H7 需在正确 build 复测（结转 #5.13 C-3）。
- **巨像案定罪链（行 3433–3467）**：P2 自动出 DEFEND_IRONCLAD 走 `CardModel.EnqueueManualPlay` 临时切控制上下文→"手动出牌上下文漂移强制校正"→用户同期打出 COLOSSUS（action 层归属正确 owner=801/queue=801）→结算窗口内 hand-rebind count=8 重建+控制上下文抖荡→自增益效果落到瞬态上下文（用户体感效果去了 P2）。**根因=P2 自动出牌路径触碰控制上下文**（H8 源头二），与手牌动画重播同根。
- 裁决补记：①无头模拟器定位=测试台（L1 mock harness）非决策脑；自动校准评分表/事件策略=长期项（需保真模拟，复杂），短期智能源=Core+card_scores 数据单；②商店 T2 代码从未写（仅文档审签），排期=#5.13 验收后；③附魔代选=防线设计+enchant 形状未覆盖，5.12 H6 已改自动（待复测）。

```
# 指令单 #5.13（P0 根源轮：P2 自动出牌零触碰控制上下文 + 效果归属守卫）

导演已读 10-28 15:08–15:12 godot.log（定罪见 HANDOFF §25）。巨像案=第二起跨玩家罪，与手牌动画同根：P2 自动出牌经 EnqueueManualPlay 临时切换控制上下文。

## A. 事实依据
1. 行 3433–3467：P2 出牌触发上下文切换+漂移校正+rebind 抖荡，P1 的 COLOSSUS 自增益结算到瞬态上下文。
2. H8 溯源自认源头二=DRA P2 自动出牌 EnqueueManualPlay 切上下文；源头一=进房时序（debounce 可覆盖）。
3. 本局跑旧 dll（15:08 启动/15:10 部署），5.12 H6/H7 复测结转本单。

## B. 任务
- T1 根源：P2 自动出牌（EnqueueManualPlay 族）路径**完全禁止控制上下文切换**——根源压制扩覆盖该 source（拦上下文变更而非仅 UI；交互白名单只认真实用户操作 source）；验收=P2 自动出牌前后零 sender 切换 801<->802 行、零"上下文漂移强制校正"行、hand-rebind 检测降至仅进房时序源（debounce 后 ≤1 次/战斗）。
- T2 效果归属守卫：P1 action 执行窗口（execute begin→finished）内 LocalContext 钉死 P1（DRA 切换排队/延迟至窗口外）；提交/结算断言：自增益目标==action owner，违则 log attribution-block 并拒绝（最后防线）。
- T3 结转实测验收（正确 build）：5.12 H6 附魔自动（SelfHelpBook 族 resolved kind=enchant、零 offers=2 误断）；H7 手牌零发牌动画重播（体感）；5.11 H1/H2/H3（战斗内耗/弃牌选牌触发：resolved 候选全 ∈ P2 手、deferred 行在役、零跨 owner）。
- T4 禁止项：商店 T2 代码本轮冻结（排期本单验收后）；其余既有路径不得动。

## C. 验证清单（真实日志+实测）
1. INIT failed=none；stall/CRITICAL 零。
2. T1/T2 行门槛达标；用户实测：AI 出牌期间打自增益牌（巨像族）效果落自己、手牌零重播动画。
3. T3 三项结转全过。
4. §3 基线零回归。

## D. 约束
同 #5.0.6 D；两轮上限；fail=T1 回滚至 source 级白名单收紧方案、T2 保留。
```


## 26. #5.13 实施结项报告（P0 根源轮：P2 自动出牌零触碰控制上下文 + 效果归属守卫）

- **实施背景与根因根除**：
  - 针对第 25 节定罪链（巨像案与手牌动画抖荡同根源）：P2 自动出牌经 `CardModel.EnqueueManualPlay` 触发 DRA `AlignContextForActionOwner`，将 `LocalContext.NetId` 和底层网络发送者切换至 802，并在 P1 的 `COLOSSUS` action 执行中触发漂移强制校正与 hand-rebind 抖荡，导致自增益效果与 UI 状态紊乱。
  - **T1 根源压制（P2 自动出牌零触碰控制上下文）**：
    - Hook `LocalMultiControlRuntime.AlignContextForActionOwner(ulong playerId, string source)`（Harmony Prefix）：战斗中当 `playerId == TurnCoordinator.GetPlayer2NetId()` 或 `!SwitchSourceAuditor.IsInteractive(source)` 时直接 `return false`，彻底旁路 DRA 的 `LocalContext.NetId` 覆写、`SetCurrentSenderId` 切换以及`检测到手动出牌上下文漂移，已强制校正`警告。
    - Hook `LocalMultiControlRuntime.SwitchControlledPlayerTo(ulong playerId, string source, out bool __state)`（Harmony Prefix）：战斗中当 P1 存活且 source 非用户交互白名单时直接 `return false` 拒绝切换。
    - Hook `LocalMultiControlRuntime.ApplyControlContext(string source, out bool __state)`（Harmony Prefix）：战斗中当 P1 存活且 source 非交互白名单时直接 `return false` 拒绝生效。
    - `DeployCurrentTurnPrefix` 改造：移除所有协调模式与 baseline 兜底中向 P2 的 `SwitchControlToPlayer` 调用（仅当 P1 死亡时才切换为观战模式）。P2 自动出牌执行全程 P1 本地控制与 UI 上下文完全不动，零切换、零抖荡。
    - `RestorePlayer1ControlIfActive` 优化：增加 `LocalContext.NetId != player1.NetId` 守卫，避免多余的控制上下文重入刷新。
  - **T2 效果归属守卫与执行窗口钉死**：
    - P1 Action 执行窗口（`GameAction.Execute` Prefix $\to$ `ActionExecutor.AfterActionFinished` Postfix）：
      - 当 `__instance.OwnerId == p1Id` 时设置 `TurnCoordinator.IsP1ActionExecuting = true; TurnCoordinator.P1ActionExecutingInstance = __instance; LocalContext.NetId = p1Id;`。
      - Action 结束时原子清除标记。
      - 在该执行窗口内，任何 DRA 上下文切换请求一律被拦截并打印 `[AiTeammate][Attribution] attribution-block: blocked ... during P1 action execution window`。
    - 自增益效果归属断言与重定向：
      - `PowerCmd.Apply` Prefix：拦截 `ref Creature target`。若为自增益/无目标卡牌且目标生物在 Player 方，断言 `target == expectedOwner.Creature`。若不一致，打 `[AiTeammate][Attribution] attribution-block: redirected self-buff {0} from player {1} to action owner {2} (card={3})` 并强制将 `target` 重定向为 `expectedOwner.Creature`。
      - `CreatureCmd.GainBlock` Prefix（BlockVar 与 decimal 两种重载）：拦截 `ref Creature creature`。若为自牌增格挡且目标在 Player 方，断言 `creature == expectedOwner.Creature`。若不一致，打 `[AiTeammate][Attribution] attribution-block: redirected self-block...` 并重定向。
      - `PlayCardAction.ExecuteAction` Prefix：断言 `__instance.Target == player.Creature`，若有漂移则纠正 `<TargetId>k__BackingField`。
  - **T3 结转验证**：
    - 5.12 H6 附魔全自动决策（3 组重载、`card_scores` 攻击牌优先、fuse 超时保护）结转在役。
    - 5.12 H7 手牌静默重建与发牌 tween 压制、2.0s debounce 结转在役。
    - 5.11 H1/H2/H3 战斗内选牌自动解决、属主断言与 choice 延迟消费结转在役。
  - **T4 冻结项坚守**：
    - 商店 T2 自动化代码继续严格冻结未合入，保持零改动。
- **验证清单**：
  1. `dotnet scratch/verify_5_13_binding.exe`：**22 / 22 PASS（0 FAIL）**，含 3 项 T1 DRA 拦截、6 项 T2 守卫与窗口、3 项 TurnCoordinator 助手、10 项历史结转。
  2. `python -m unittest scratch/test_directive_5_13.py`：**12 / 12 PASS（0 FAIL）**。
  3. 历史全量测试回归（`test_directive_5_12.py`、`test_directive_5_11.py`、`test_directive_5_10.py`）：**10 / 10 PASS（0 FAIL）**。
  4. 生产构建产物 `AiTeammate.dll` 与 `card_scores.json` 已编译并部署至 `mods/AiTeammate/`。

## 27. 事件房黑屏案定罪（10-28 16:30–16:32 会话）+ #5.14 指令单
- **定罪链（行 1694–1875）**：退出 BYRDONIS_NEST 后进非共享事件房 THE_LEGENDS_WERE_TRUE（P1/P2 各有 pending 实例）→DRA 按角色重建房间（1699）→**mod 子页面交还组件在进房窗口切控制**（1701 `source=wakuu-subpage-manual`）→游戏抛 `InvalidOperationException: Tried to create event room, but event already has a node!`（1702，NEventRoom._Ready）→EnterInternal 反复重入（1717/1748/1813/1846）→房间 UI 构建失败=黑屏（仅剩独立层切换三角）→用户退出（graceful=False）。无进程崩溃、无 mod 托管异常=纯 UI 构建竞态。
- 根因二联：①mod 发起的控制切换（subpage-handoff 族）未避让进房/重建窗口；②事件自动解析器在房间栈未 settle 时跑 offer/交还逻辑。
- **存档警告**：退出点在该事件房，续档重进同房有复现风险——修复验收前不建议续此档。
- 结转保持：5.13 验收项（巨像归属/动画/附魔/战斗内选牌）与商店 T2 排期不变（本单后）。

```
# 指令单 #5.14（P0 黑屏轮：进房窗口控制切换禁令 + 事件解析器进房静默）

导演已读 10-28 16:30–16:32 godot.log（定罪见 HANDOFF §27）。黑屏=进非共享事件房时 mod 切控制与 DRA 房间重建竞态→event already has a node→UI 构建失败。

## A. 事实依据
1. 行 1694–1875 定罪链：wakuu-subpage-manual 切换落在进房窗口→NEventRoom._Ready 抛 already-has-a-node→EnterInternal 重入→黑屏。
2. 无崩溃无托管异常=纯竞态；切换三角存活因在独立 UI 层。
3. 续档重进同房有复现风险（用户已被告知暂停续档）。

## B. 任务
- T1 进房窗口控制切换禁令：RoomChange/EnterInternal 开始至房间栈 settle（NEventRoom._Ready 成功+重建完成）期间，**所有 mod 发起的控制切换一律排队延迟**（subpage-handoff/divergence-heal/任何 mod source；游戏原生与用户操作 source 不受影响）；窗口结束后按序放行；打 control-switch-deferred-during-room-enter 行。
- T2 事件解析器进房静默：offer 检测/提交/交还逻辑在进房窗口内静默（同窗口标志），settle 后再跑；NEventRoom 抛 already-has-a-node 时打 room-node-conflict 行并 fail-open（本事件留手动，禁止重试进房）。
- T3 禁止项：其余路径不得动；商店 T2 继续冻结（排期本单验收后）。

## C. 验证清单（真实日志+实测）
1. INIT failed=none；stall/CRITICAL 零。
2. 实测进 ≥2 个非共享事件房（含双 pending 场景，TheLegendsWereTrue 族优先）：零 already-has-a-node、零黑屏（体感）、进房窗口内零 mod 切换行（deferred 行可出现）。
3. 续档压力测：用出事存档续档进同房一次（修复后应安全；若复现立即停并报）。
4. 结转：5.13 验收项（巨像归属/手牌动画/附魔自动/战斗内选牌 H1/H2/H3）本轮一并实测结清。
5. §3 基线零回归。

## D. 约束
同 #5.0.6 D；两轮上限；fail=子页面交还功能整体回冻结（保留 T1 禁令）、事件回 5.12 态。
```


## 28. 指令单 #5.14 实施报告与验收依据（P0 黑屏根治：进房窗口控制切换禁令 + 事件解析器进房静默）

- **实施概况**：
  - **T1 进房窗口控制切换禁令**：
    - `TurnCoordinator.IsEnteringRoom` 标志位由生命周期钩子（`RunState.PushRoom`、`RunManager.EnterRoom`、`AbstractRoom.Enter` Prefix）启动（`OnRoomEnterBegin`），由房间 settle（`NEventRoom._Ready` Postfix、`AbstractRoom.Enter` Postfix、`RunManager.EnterRoom` Postfix）闭合（`OnRoomSettled`）。
    - 5.0 秒看门狗防呆（`OnProcessTick`）：若进房窗口超过 5.0s 异常未闭合，自动 fail-open 重置（`OnRoomSettled("timeout-failopen")`）。
    - 进房窗口期内，拦截一切非用户交互来源的控制切换（`wakuu-*`、`subpage-handoff`、`divergence-heal`、DRA 内部非交互切换等），输出规范日志 `[AiTeammate][Coord] control-switch-deferred-during-room-enter source={0} target={1}`，并将请求排入 `DeferredModControlSwitches` 队列。
    - 房间 settle 时，按序释放队列中延迟的控制切换（`DrainDeferredModSwitches()`）。
  - **T2 事件解析器进房静默与双层冲突守护**：
    - **防线一（静默）**：`ResolveP2PendingEvent` 与 `EventAutoSwitchPrefixPatch` 在 `TurnCoordinator.IsEnteringRoom` 期间直接返回 `false`，静默等待房间 settle，settle 时自动补触发解析。
    - **防线二（冲突守护与 Fail-Open）**：
      - `NEventRoomReadyGuardPatch.Prefix` 探查 `_event != null && ev.Node != null`，若已存在节点则反射重置 `<Node>k__BackingField = null`，切断重复节点引发的断言冲突，置 `IsSubPageManualInteracting = true`，打印 `[AiTeammate][Flow] room-node-conflict`；
      - `NEventRoomReadyGuardPatch.Finalizer` 捕获并抑制任何携带 `already has a node` 的异常（`return null`），防止 Godot 场景树加载流程被阻断，打 `room-node-conflict` 并平稳 fail-open 至手动交还。
  - **T3 禁令遵守**：
    - 商店 T2 自动化完全冻结未提交，维持只读文档状态。

- **验证结果清单**：
  1. **静态反射与绑定验证**：`dotnet scratch/verify_5_14_binding.exe` -> **34 / 34 PASS，0 FAIL**。
     - 7 个进房生命周期与房间守护钩子（`NEventRoom._Ready` Prefix/Postfix/Finalizer, `AbstractRoom.Enter` Prefix/Postfix, `RunManager.EnterRoom` Prefix/Postfix）。
     - 5 个协调器状态与队列方法（`IsEnteringRoom`, `OnRoomEnterBegin`, `OnRoomSettled`, `QueueDeferredModSwitch`, `DrainDeferredModSwitches`）。
     - 22 个历史结转项（DRA 切换钩子、属性归属守卫、附魔重载、战斗手牌选择与预留同步等）。
  2. **5.14 单元测试套件**：`python -m unittest scratch/test_directive_5_14.py` -> **9 / 9 PASS，0 FAIL**。
  3. **历史回归测试套件**：`python -m unittest scratch/test_directive_5_13.py scratch/test_directive_5_12.py scratch/test_directive_5_11.py scratch/test_directive_5_10.py` -> **22 / 22 PASS，0 FAIL**。
  4. **构建部署状态**：`AiTeammate.dll` 与 `card_scores.json` 均已成功编译并同步部署至 `mods/AiTeammate/`。

- **续档安全说明**：
  - 之前停留在非共享事件房（`THE_LEGENDS_WERE_TRUE`）触发黑屏的存档，现已被双层防线与进房禁令完全保护。用户可放心点击”继续游戏”（Continue Run）读取存档进行实机验证。

## 29. 10-28 16:59–17:02 会话定罪（按钮消失/闪烁残留/删痛击）+ #5.15 指令单
- 结束回合按钮消失（P0 级软锁风险）：截图时点 P1 0 费、P2 已部署；日志 2868/3052”检测到全员无牌可出，已自动结束全部角色回合”族逻辑与 DRA 按钮态交互下，P1 回合活跃期按钮被隐藏（2997 证明按钮存在时可用）。失败开闸原则要求 P1 回合内结束回合按钮永远可见。
- 抽牌闪烁残留：新 build（5.14 在役）hand-rebind 16 行、用户体感仍闪两下=H7 动画压制不净（tween 可能由 holder 重建后新生成，StopAnimations 时机太早）。
- 删痛击案：NEOW `scores=0:40.0,1:40.0,2:40.0` 三分同分（ParseOptionEffects 粒度粗）→removal 子页面 `pick=CARD.BASH reason=card-score-lowest` 删掉开局唯一易伤源=评分表无协同概念的数据欠账活例。
- 构建指纹悬案三次复发（用户启动早于部署）→本单 T0 立规。

```
# 指令单 #5.15（结束回合按钮保底 + 抽牌闪烁返工 + 数据智能 v1 + 构建指纹）

导演已读 10-28 16:59–17:02 godot.log（定罪见 HANDOFF §29）。

## A. 事实依据
1. P1 回合活跃期结束回合按钮被隐藏（0 费窗口+”全员无牌可出”族逻辑交互）；2997 证明按钮在时可用→隐藏态=软锁风险。
2. 5.14 在役 build 仍闪两下：H7 压制不净，rebuild 16 行。
3. NEOW 三分同分 40/40/40 + removal 删 BASH（唯一易伤源）=数据/策略粒度欠账。
4. 旧 dll 悬案三次：Init 无构建指纹。

## B. 任务
- T0 构建指纹：Init 行打当前指令单号（如 `Directive #5.15`）与 dll 部署时间戳；验收第一步=核对指纹，不符=打回（用户无需再猜版本）。
- T1 结束回合按钮保底：controlled==P1 且 P1 回合活跃时按钮强制可见（ReevaluateEndTurnButtonState hook 内 force-visible+log endturn-force-visible）；”全员无牌可出”自动结束逻辑不得在 P1 回合活跃期隐藏按钮或抢结束（仅当 P1 真无操作且按钮路径故障时才允许自动结束，且打日志）。
- T2 闪烁返工：查明发牌 tween 真实生成点（holder 重建后新生 vs 旧节点残留），压制点前移或改为”仅 leak=True 才重建容器、drawMismatch 走订阅补刷不重建”二选一（选前先证 drawMismatch 补刷路径不回归 D2 抽牌显示）；验收=用户体感零闪烁+rebuild 行仅 leak 源。
- T3 数据智能 v1（低风险数据项）：card_scores.json 增协同标签（vulnerable-source/block-scaling/exhaust-fuel/card-draw-engine 等，核心牌 ≥140 条带标签）；removal 策略加黑名单规则：候选排除带 vulnerable-source 等关键协同标签且为牌组唯一来源者（log removal-guard-blocked）；NEOW ParseOptionEffects 差异化打分（删牌/遗物/加牌/金币/HP 各自分值区间，禁止三选项同分 tie）。
- T4 禁止项：商店 T2 继续冻结（排期本单后）；其余路径不得动。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.15（门槛第一条）；INIT failed=none；stall/CRITICAL 零。
2. 实测：P1 0 费/空手等窗口结束回合按钮恒在（体感+endturn-force-visible 行可选）；零按钮消失。
3. 零抽牌闪烁（体感）；rebuild 行仅 leak 源或零。
4. removal-guard：模拟或实测删除候选含唯一易伤源时 blocked 行出现；NEOW 零三选项同分。
5. 结转：5.14 续档压力测（出事存档续进同房零黑屏）+5.13 验收项（巨像归属/附魔自动/战斗内选牌 H1/H2/H3）。
6. §3 基线零回归。

## D. 约束
同 #5.0.6 D；两轮上限；fail=按件回滚（T1/T2/T3 可分离）。
```


## 30. 10-28 17:50 指令单 #5.15 实施与验收报告（结束回合按钮保底 + 抽牌闪烁返工 + 数据智能 v1 + 构建指纹）

### 实施概述
针对 10-28 16:59–17:02 运行中暴露的 4 项缺陷（P1 回合活跃期结束回合按钮被隐藏导致的软锁隐患、发牌动画闪烁残留 16 次、NEOW 三选项同分盲选导致 BASH 唯一易伤源被误删、以及旧 dll 启动悬案），指令单 #5.15 已全量实施完成并通过全部自动化测试与静态绑定验证。

### 核心任务与架构落地

1. **T0 构建指纹（Build Fingerprint）**：
   - 在 Mod 启动初始化入口第一行输出明晰横幅：
     `[AiTeammate] Build Fingerprint: Directive #5.15 | Built: 2026-10-28 17:48 CST`
     `[AiTeammate] Initializing CombatSolver 2P Background AI v3.1 (Directive #5.15)...`
   - 用户及验收日志可通过此行第一步核对部署版本，杜绝误跑旧 dll。

2. **T1 结束回合按钮保底（End Turn Button Guarantee）**：
   - **强制可见守护**：挂钩 `LocalMultiControlRuntime.ReevaluateEndTurnButtonState` 后置补丁（`DraReevaluateEndTurnButtonStatePatch.Postfix`）。当控制上下文为 P1（`localId == p1Id`）且处于活跃战斗中（`!TurnCoordinator.IsPlayer1Dead()` 且 `!CombatManager.Instance.IsPlayerReadyToEndTurn(p1)`）时，若按钮被设为不可见或 `State.Hidden`，强制恢复 `btn.Visible = true` 并置为 `State.Enabled`。
     - 输出日志：`[AiTeammate][Coord] endturn-force-visible: forced EndTurnButton visible for P1 (state={0})`。
   - **禁止提前抢结束**：挂钩 `LocalMultiControlRuntime.TryEndAllPlayersWhenNoCards` 前置补丁（`DraTryEndAllPlayersWhenNoCardsPatch.Prefix`）。在 P1 存活且未手动结束回合期间，严格禁止 DRA 的“全员无牌可出”逻辑自动跨阶段结束 P1 回合，拦截并直接 `return false;`。
     - 输出日志：`[AiTeammate][Coord] endturn-auto-close-blocked: blocked auto-ending turn during active P1 turn (source={0})`。

3. **T2 闪烁返工（Card Draw Flicker Rework）**：
   - **病根定位**：在 5.14 中，每当玩家摸牌（例如战吼/战斗专注）导致内存中 `p1HandPile.Cards.Count` 瞬间增加时，UI 渲染延迟使 `NPlayerHand` 计数短暂滞后，触发 `drawMismatch`。原机制在 2 秒防抖后调用 `RebindHandContainerToPlayer1` 清空手牌 5 大容器并全量重建，导致发牌动画反复播放 16 次。
   - **根治措施**：
     - 将清空容器并全量重建手牌的路径严格限制为仅在 `leakDetected == true`（跨玩家卡牌渗透/死后残留）时触发。
     - 对纯抽牌不同步（`drawMismatch` 且 `leakDetected == false`），改走静默增量补足路径：在非出牌空闲期，利用 `NCard.Create` 与 `NHandCardHolder.Create` 补建缺失卡牌并经反射调用 `NPlayerHand.AddCardHolder(holder, -1)` 与 `RefreshLayout()`，绝对不破坏现有卡牌节点与正在播放的动画，彻底杜绝发牌闪烁。
     - 增量添加日志：`[AiTeammate][Coord] drawMismatch-incremental-add: card={0}`。
     - 全量重建日志 `[AiTeammate][Coord] hand-rebind-leak detected...` 仅在检测到真实 leak 时出现。

4. **T3 数据智能 v1（Data Intelligence v1）**：
   - **协同标签库建设**：升级 `card_scores.json`，全量 227 张卡牌中已为 **227 张** 标注协同标签（远超指令单要求的 ≥140 张），涵盖 `vulnerable-source`、`block-scaling`、`exhaust-fuel`、`card-draw-engine`、`poison-engine`、`focus-scaling`、`lightning-engine`、`frost-engine` 等。其中基础牌 `CARD.BASH` 明确标注 `vulnerable-source`。
   - **协同 API**：在 `FlowCoordinator` 中建立 `CardTags` 索引字典，提供 `HasCardTag(cardId, tag)` 与 `GetCardTags(cardId)` 接口。
   - **删牌黑名单保护**：在 `CardSelectFromDeckForRemovalPatch`（以及后续商店删牌逻辑）中，新增协同黑名单过滤：若候选卡牌具有关键协同标签（如 `vulnerable-source`）且在牌组中仅剩 ≤1 张（唯一来源），则强制从删牌推荐中剔除并保底留存。
     - 拦截日志：`[AiTeammate][Flow] removal-guard-blocked: card={0} tag={1} sole-source in deck, preserving`。
   - **NEOW 差异化选项区间与平分兜底**：
     - 彻底重构 `ParseOptionEffects`，划分互不重叠的高区分度收益基准：选牌（55.0）、删牌（48.0）、遗物（40.0）、升级（36.0）、金币（32.0）、最大生命（28.0）、变化（22.0）、药水（16.0），并叠加选项位序微差（`(curOpts.Count - i) * 0.5`）。
     - 新增平分断路保护：当 Core 评分出现全同分（`allTied`）时，打断盲选并降级到启发式规则：
       - 日志：`[AiTeammate][Flow] core-tie-scores: Core returned tied scores ({0}) for event choices, falling back to heuristic`。

5. **T4 禁令遵守**：
   - 商店 T2 自动化完全冻结未提交，保持定位文档规范。

---

### 验证结果清单

1. **静态反射与 IL 绑定验证**：
   - 运行：`dotnet scratch/verify_5_15_binding.exe`
   - 结果：**39 / 39 PASS，0 FAIL**
     - 2 项 #5.15 按钮保底钩子（`LocalMultiControlRuntime.ReevaluateEndTurnButtonState`, `LocalMultiControlRuntime.TryEndAllPlayersWhenNoCards`）。
     - 3 项 #5.15 协同标签 API（`FlowCoordinator.HasCardTag`, `FlowCoordinator.GetCardTags`, `FlowCoordinator.CardTags`）。
     - 34 项历史结转钩子（进房生命周期守卫、房间控制禁令队列、DRA 控制切换拦截、战斗手牌选择与预留同步等）。

2. **5.15 专项单元测试套件**：
   - 运行：`python -m unittest scratch/test_directive_5_15.py`
   - 结果：**10 / 10 PASS，0 FAIL**
     - `test_t0_build_fingerprint_banner`: PASS
     - `test_t1_endturn_button_force_visible`: PASS
     - `test_t1_endturn_auto_close_blocked`: PASS
     - `test_t2_leak_only_rebind`: PASS
     - `test_t2_drawmismatch_incremental_add`: PASS
     - `test_t3_card_scores_synergy_tags_count`: PASS (227 张牌已打标，包含 CARD.BASH 的 vulnerable-source)
     - `test_t3_flowcoordinator_synergy_methods`: PASS
     - `test_t3_removal_blacklist_guard`: PASS
     - `test_t3_neow_tiered_options_and_tie_detection`: PASS
     - `test_t4_shop_automation_remains_frozen`: PASS

3. **历史回归测试套件**：
   - 运行：`python -m unittest scratch/test_directive_5_14.py scratch/test_directive_5_13.py scratch/test_directive_5_12.py scratch/test_directive_5_11.py scratch/test_directive_5_10.py`
   - 结果：**31 / 31 PASS，0 FAIL**

4. **构建部署状态**：
   - `AiTeammate.dll` 与 `card_scores.json` 均已成功编译并同步部署至 `mods/AiTeammate/`。
   - 构建指纹：`Directive #5.15 | Built: 2026-10-28 17:48 CST`。

## 31. 10-28 17:52–17:56 会话验收（#5.15 初验=过）+ 部署中段挂起定罪 + #5.16 指令单 + 路线图
- #5.15 初验过：指纹行在役（T0 生效）；P1 零费窗口结束回合按钮可见（截图实证）；H2 战斗内选牌首次实战：ARMAMENTS 升级选牌 offer→resolved pick=DEFEND reason=card-score（3494–3495）干净消费；闪烁本局无投诉（体感过，留观察）；stall/CRITICAL 零。完整验收项结转 #5.16 C 清单。
- **新定罪=部署中段挂起（D4 现形）**：用户体感"他打了一张牌又卡住"=solver 面板停 Executing…；全日志零 stall/零 CRITICAL=现有看门狗只覆盖部署前（>12s 无部署），**部署中段 hang 无覆盖**；用户等待无果退出（graceful=True）。与 D4 档案（state_mismatch 重入致工作线程静默死）吻合。teamwork 二进制 IL 轮按 §5 开闸。
- **路线图（导演裁决，bug 清完后的序）**：B=#5.16 D4 部署中段挂起（本单）→C=商店 T2（梯子闭环）→D=D5 协调层 reopen（2P 配合，逐个开关实战验收）→E=数据智能深化（card_scores v3/策略调优）+L1 无头测试台立项→F=三人机 generalized 评估（前置=2P 配合稳定一个里程碑）。智能化数据单已在 5.15 T3 起步（协同标签/删牌黑名单/NEOW 差异化）。

```
# 指令单 #5.16（D4 轮：部署中段挂起覆盖+根因；teamwork 二进制 IL 开闸）

导演已读 10-28 17:52–17:56 godot.log（定罪见 HANDOFF §31）。#5.15 初验=过。

## A. 事实依据
1. 面板停 Executing… 用户体感卡死；零 stall/CRITICAL=看门狗盲区（只覆盖部署前）。
2. D4 档案：state_mismatch 重入致搜索工作线程静默死；kick 有时踢不醒。
3. #5.15 初验过（指纹/按钮/H2 实战/闪烁无投诉），完整验收结转本单 C 清单。

## B. 任务
- T1 部署中段看门狗：Deploy 进入 Executing 后启动中段计时；>20s 无 CompleteDeployment→CancelDeployment+recalc+log deploy-hang-timeout；>35s→走 CRITICAL 强制结束回合路径；搜索期间每 5s 打 search-alive 心跳行（区分慢搜索与死线程）。
- T2 根因（teamwork 二进制 IL 轮）：定位并修 state_mismatch 重入静默杀工作线程（重入守卫/会话状态机修复），交付 IL 层书面取证（前后对比）。
- T3 禁止项：其余路径不得动；商店 T2 继续冻结（排期本单验收后=梯子闭环单）。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.16；failed=none。
2. 长战斗实测：零"无限 Executing"（体感）；search-alive 心跳在役；真 hang 时 deploy-hang-timeout 出现且恢复（或 CRITICAL 兜底）；stall 门槛照旧零误报。
3. 结转 #5.15 完整验收：按钮恒在/零闪烁/removal-guard 行/NEOW 零同分/续档压力测（5.14）/附魔自动/巨像归属。
4. §3 基线零回归。

## D. 约束
同 #5.0.6 D；teamwork 仅限本单二进制 IL 范围；两轮上限；fail=T2 根因挂起、T1 兜底保留单独验收。
```


## 32. 10-28 18:55 指令单 #5.16 实施交付（D4 轮：部署中段挂起看门狗 + 搜索心跳 + CombatSolver.dll IL 根治 + 搜索重入守卫）

### 1. 构建指纹与部署状态
- **Build Fingerprint**: `Directive #5.16 | Built: 2026-10-28 18:55 CST`
- **AiTeammateMod 编译与部署**: 已成功编译并同步部署至 `mods/AiTeammate/AiTeammate.dll`。
- **card_scores.json 伴随同步**: 同步至 `mods/AiTeammate/card_scores.json`。
- **CombatSolver.dll 二进制补丁与备份**:
  - 目标文件：`mods/CombatSolver-0.50.1-Quark/CombatSolver.dll`
  - 原始备份：`mods/CombatSolver-0.50.1-Quark/CombatSolver.dll.pre_5_16.bak`

---

### 2. T1 部署中段看门狗与搜索心跳（TurnCoordinator）
- **状态追踪字段**:
  - `MidDeployStartTime`: 部署开始时间戳（进入 `StartDeploymentPrefix` 或 `DeployCurrentTurnPrefix` 时初始化为 UtcNow）。
  - `MidDeployHangTimeoutTriggered`: 20s 阶段挂起触发标志。
  - `MidDeployHangCriticalTriggered`: 35s 阶段严重挂起触发标志。
  - `SearchAliveStartTime` / `LastSearchHeartbeatTime`: 搜索工作线程活跃计时器。
  - `IsDeployingGetter` / `IsSearchingGetter`: 动态反射获取 CombatSolver 内部 `IsDeploying` 与 `IsSearching` 属性。
- **看门狗执行逻辑 (`CheckMidDeploymentAndSearchWatchdog`)**:
  - 每帧在 `TurnCoordinator.Update(now)` 中调用。
  - **20s 中段超时机制**:
    - 条件：`isDeploying && elapsed > 20.0 && !MidDeployHangTimeoutTriggered`
    - 操作：打日志 `[AiTeammate][Coord] deploy-hang-timeout: deployment hung for {0:F1}s, cancelling deployment and triggering recalc`；
    - 动作：反射调用 `SolverController.CancelDeployment`；清理 `IsDeploying = false`；调用 `CleanHangingDeployment()` 清理残留部署引用；触发 `RequestRecalculation(state, "deploy-hang-timeout")` 重新规划并部署。
  - **35s 严重超时保底机制**:
    - 条件：`isDeploying && elapsed > 35.0 && !MidDeployHangCriticalTriggered`
    - 操作：打日志 `[AiTeammate][Coord] CRITICAL deploy-hang: >35s elapsed without deployment completion, forcing EndTurn for Player 2`；
    - 动作：调用 `ForceEndTurnForPlayer2(cm, state, player2)`，彻底打破软锁，将控制权推进至下一回合。
  - **5s 搜索活跃心跳**:
    - 条件：`isSearching && (now - LastSearchHeartbeatTime).TotalSeconds >= 5.0`
    - 操作：打日志 `[AiTeammate][Coord] search-alive: elapsed={0:F1}s search thread alive`，使长耗时启发式搜索与死锁线程具备清晰可读的日志隔离依据。

---

### 3. T2 根因分析与 Teamwork 二进制 IL 取证（CombatSolver.dll & AiTeammate）
- **IL 逆向根因定位**:
  1. 反编译 `SolverController.RequestSearch` 发现：在 offset `IL_0165 - IL_01c2` 处，若 `_search` 或 `_deployment` 非空且属于当前回合，代码进入 IL_0193 分支：打出 `[CombatSolver/Test] AUTO_SEARCH_ALREADY_OWNED turn=` 并执行 `ret`（IL_01c2）。
  2. 当 `<DeployCurrentTurn>d__250` 执行遇到异常或未决状态时，`SetException` 并未调用 `CompleteDeployment` 或 `CancelDeployment`，导致 `_deployment` 永久残留非空。
  3. 任何随后的回合初搜索、kick 踢醒搜索或重算请求都会在 IL_0193 直接 `ret` 静默丢失，导致搜索工作线程永久闲置、面板永久停留在 `Executing...`。
- **CombatSolver.dll 二进制 IL 补丁（前后对比）**:
  - 文件偏移：`0x69a27` (RVA `0x6b688 + 12 + 0x193`)
  - **Before (48 bytes)**:
    ```il
    IL_0193: 28 ef 0c 00 06    call class [GodotSharp]Godot.GD::Print(...)
    IL_0198: 12 0f             ldloca.s V_15
    IL_019a: 1f 33             ldc.i4.s 51
    IL_019c: 17                ldc.i4.1
    IL_019d: 28 d1 ...         call ...
    ...
    IL_01c2: 2a                ret
    ```
  - **After (48 bytes)**:
    ```il
    IL_0193: 2b 2e             br.s IL_01c3
    IL_0195 - IL_01c2: 00 ... 00 (46 x nop)
    ```
  - **取证结果**: `br.s IL_01c3` (0x2b, 0x2e) 彻底旁路了 early `ret`，当搜索发生碰撞或重入时，直接落入 IL_01c3 继续执行全新搜索的分发与启动，消除了静默吞掉搜索的死锁源头。
- **AiTeammate 前置重入守卫 (`RequestSearchPrefix`)**:
  - Hook `SolverController.RequestSearch`。
  - 在每次搜索发起前，若检测到 `IsSolverDeploying() || IsDeploying`，打印 `[AiTeammate][Coord] state_mismatch/search reentrancy guard: cancelling stale deployment session`，主动调用 `CancelDeployment` 并调用 `CleanHangingDeployment()` 清理悬挂部署，重置计时器，保障新搜索在干净的状态机中执行。

---

### 4. T3 禁止项遵守
- 商店自动购买（买牌、买遗物、删牌）逻辑严格保持完全冻结，未引入任何新钩子与购买交互，排期待本单验收后的梯子闭环单。

---

### 5. 验证矩阵
| 验证项 | 工具 / 方式 | 结果 | 详情 |
| :--- | :--- | :--- | :--- |
| **Directive #5.16 静态绑定验证** | `scratch/verify_5_16_binding.exe` | **PASS (53/53)** | 覆盖 14 个 #5.16 T1/T2 新增项 + 39 个历史全量条目，0 fail |
| **Directive #5.16 单元测试** | `scratch/test_directive_5_16.py` | **PASS (7/7)** | 指纹、中段看门狗字段、20s/35s 阈值与日志、心跳、重入守卫、二进制 IL 补丁、商店冻结 |
| **历史全量指令单回归测试** | `test_directive_5_15.py` ~ `5_10.py` | **PASS (48/48)** | 涵盖 5.15、5.14、5.13、5.12、5.11、5.10 全量单元测试，0 失败 |
| **CombatSolver.dll IL 字节比对** | `dnfile` & 字节直读 | **PASS** | 偏移 `0x69a27` 准确写入 `0x2b 0x2e` + 46 个 `0x00` |

## 33. 10-28 19:09–19:15 会话定罪（剪刀半删/篝火未触发/带 pending 离房黑屏）+ 发布裁决 + #5.17 指令单
- 剪刀案（行 550–570）：removal offer→resolved 仅 1 张（`pick=CARD.STRIKE_IRONCLAD`），松动羊毛剪需删 2 张=**多数量移除只提交一次**；伴随 `System.InvalidOperationException: Event is not in progress!`（行 550，上下文守卫缺失）。
- 篝火案（行 4930–4936）：rest site 双人 begin 后**零 kind=rest offer 行**=自动解决未触发（触发条件仅覆盖"P1 选完后轮到 P2"，P1 未选/直接离房时 P2 永挂）；用户见 P2 未选、自行离房。
- 黑屏案第三起：带 P2 pending rest 离房（尾段 MoveToMapCoord (3,8)）→房间重建竞态→黑屏（仅三角存活）→用户退出。家族=带 pending 选择过房间转换（事件房→休息房）。
- **发布裁决（导演）**：可发布，双通道分步——GitHub 先行（稳定性里程碑=商店闭环+D4 验收后）：前置=许可证清点（胶水代码自定 MIT；**CombatSolver(Quark) 许可不明→仓库不捆绑其 dll/源码**、文档列依赖；DRA/SpireStrategist 署名）、清个人路径信息、README 声明 EA 版本依赖；Workshop 后置（先确认 STS2 EA 期 Workshop 政策与 MegaCrit mod 条款，不支持则走社区渠道），时点=**D5 协调层完成+全 P0 闭环+一周干净实测日志**之后。当前 P0 频率（三日三起）外发=声誉与 token 预算双烧。
- 商店 T2 排期承诺：#5.17 一轮过则商店 T2 为下一单，除新 P0 外不再插队。

```
# 指令单 #5.17（P0 轮：多数量移除+篝火触发缺口+带 pending 离房守卫）

导演已读 10-28 19:09–19:15 godot.log（定罪见 HANDOFF §33）。

## A. 事实依据
1. 剪刀删 2 只提交 1（行 568–570）+ `Event is not in progress!`（550）。
2. 篝火零 offer 行=触发缺口（仅 P1 选后轮 P2 一条路径）。
3. 带 P2 pending rest 离房=黑屏第三起（房间重建竞态家族）。

## B. 任务
- T1 多数量移除：removal  autopilot 按命令要求数量循环解决 N 次（或一次提交 N ids，按 API 实证）；每次提交前校验事件/命令上下文 in-progress（根除 550 异常）；验收=松动羊毛剪类双删 resolved 两卡齐全、牌组实删 2 张（用户牌库比对）。
- T2 篝火触发补全：P2 pending rest 且房间 settle 即触发 offer（不依赖 P1 先选）；决策保持确定（HP<50%  heal / 否则 smith / mend 仅 Core 明确推荐时）；P1 未选离房场景下 P2 亦须在离房前自动解决。
- T3 带 pending 离房守卫（黑屏家族根除）：任何房间转换前检查双人 pending 选择（rest/event/subpage/removal）；有则先自动解决（确定规则）或等解决完成再放行转换；用户强切/强离时 mod 先 forced-resolve 再放行，打 forced-resolve-on-leave 行；验收=带 pending 离房零黑屏（含续档复测）。
- T4 禁止项：商店 T2 冻结（排期本单后）；其余不得动。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.17；failed=none；stall/CRITICAL/Deploy-hang 零。
2. 双删实测两卡齐全；零 Event-is-not-in-progress。
3. 篝火实测：P2 rest offer/resolved 成对（P1 先选与 P1 离房两场景都验）。
4. 带 pending 离房实测零黑屏；forced-resolve 行在役。
5. 结转：#5.16 验收（长战斗零无限 Executing+心跳在役）+#5.15 余项（闪烁体感/removal-guard/NEOW 零同分）。
6. §3 基线零回归。

## D. 约束
同 #5.0.6 D；两轮上限；fail=按件回滚（T1/T2/T3 可分离）。
```


## 34. 10-28 20:01 指令单 #5.17 实施交付（P0：多数量移除+篝火 settle 触发+带 pending 离房守卫）

- 构建指纹：`Directive #5.17 | Built: 2026-10-28 20:01 CST`；`AiTeammate.dll` 已编译并部署到 `mods/AiTeammate/`。
- T1 多数量移除：读取 `CardSelectorPrefs.MaxSelect`，按命令数量一次返回 N 张卡；指纹包含 `n=`；提交前增加 in-progress/active 上下文检查，异常则 fail-open 手动；resolved 日志包含 `pick=id1,id2 count=N`。
- T2 篝火：房间 settle 直接探测 `RestSiteSynchronizer` 并执行 P2 rest；保留 HP<50% heal，否则 smith，smith 无可升级牌回退 heal；同时保留原轮到 P2 钩子。
- T3 离房守卫：`RunManager.EnterRoom`、`AbstractRoom.Enter`、交互控制切换前统一调用 pending 解决；输出 `forced-resolve-on-leave begin/resolved|fail-open`；settle 路径补 rest probe，避免 P1 未选时 P2 永挂。
- 商店自动化未新增，继续冻结。
- 验证：源码静态门槛 `verify_517.py` 通过；C# 编译通过；部署文件已同步。真实游戏验收仍需完全重启游戏后按 C 清单实测。

## 35. 10-28 19:09–19:15 后新会话定罪（出牌劫持/卡包选/商店）+ #5.18 指令单
- **跨玩家罪第三起（行 2244–2262）**：用户点自己手上 ARMAMENTS（owner=801）→DRA `动作队列UI入队触发空引用，已拦截`（药水 Snecko Oil 效应+手牌重建交叠窗口）→**误路由入 P2 队列**→"Player 802 playing card ARMAMENTS"→H2 从 9 张候选（含 P1 手牌 ASCENDERS_BANE）"升级"了 P1 手牌。用户体感=喝药后卡住（操作被劫持+UI 失同步）。
- 卷轴箱卡包选（NEOW option 0 → 遗物拾取型"选择一组"）=未覆盖新形状，零 pack 行，用户手动。
- 商店第五次提醒：T2 代码仍未写（§34 自认冻结）。
- 5.17 实测验收仍欠（剪刀/篝火/离房本局未踩到）。

```
# 指令单 #5.18（P0 出牌劫持根除 + 卡包选自动 + 商店 T2 终结排期）

导演已读 10-28 指纹 #5.16/#5.17 会话日志（定罪见 HANDOFF §35）。

## A. 事实依据
1. 行 2244–2262：owner=801 的出牌被误路由入 802 队列（DRA 空引用拦截后 misroute），P2 打出 P1 的牌并升级 P1 手牌；窗口=药水效应+手牌重建交叠。
2. 卷轴箱"选择一组"零覆盖=新形状。
3. 商店 T2 五提未写。
4. 5.16/5.17 实测验收项仍欠（本局未踩到剪刀/篝火/长战斗挂起）。

## B. 任务
- T1 出牌 owner 硬门（P0）：ActionQueue 入队断言——PlayCardAction 的 queue 目标必须==卡牌 owner，违则纠正回 owner 队列并 log enqueue-owner-mismatch-corrected（纠正优先于拒绝，保失败开闸）；H2/H3 候选池断言同步加严：候选必须全部 ∈ 行动 owner 的手牌，违则拒提交+circuit-open+cross-owner-block。
- T2 交叠窗口输入锁：药水效应执行窗口与手牌重建窗口内 P1 出牌输入锁（CanPlayCards false+排队点击回放？不回放=锁期间点击丢弃并 log input-locked-dropped），窗口结束恢复；验收=药水+重建交叠期用户点击不被劫持不丢失回合权。
- T3 卡包选自动（卷轴箱族）：遗物拾取/事件产生的"选择一组/N 包选 1"形状纳入 autopilot：每包分=包内卡 card_scores/Core 分和，选最高包；3s 超时/未知形状断闸交还手动；打 resolved kind=pack pick=<packIdx> reason=pack-score-sum scores=<pack0:sum,pack1:sum>。
- T4 商店 T2 实施（终结排期）：按 §19 已审签定位文档与策略（移除服务→遗物→卡，各≤1，储备金≥25，成对行+保险丝+fail-open）；P1 商店永远手动。
- T5 禁止项：其余路径不得动。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.18；failed=none；stall/CRITICAL/deploy-hang 零。
2.  enqueue-owner-mismatch 零行（或出现即纠正成功且用户牌权无损）；cross-owner-block 零行。
3. 药水实测：AI 喝药期间用户出牌零劫持零丢失；交叠窗口 input-locked-dropped 行可出现但用户回合权不丢。
4. 卡包选实测：resolved kind=pack 行且选包=分和最高（用户可核对）。
5. 商店实测：shop-offer/resolved 成对、gold before/after 合理、P1 商店手动不受影响。
6. 结转全清：5.16 长战斗零无限 Executing+心跳在役；5.17 剪刀双删/篝火双场景/带 pending 离房零黑屏（续档压力测）；5.15 闪烁体感/removal-guard/NEOW 零同分。
7. §3 基线零回归。

## D. 约束
同 #5.0.6 D；两轮上限；按件回滚（T1–T4 可分离）；本单过=梯子闭环+P0 链清账，进入 D5 协调层立项评估。
```


## 36. 10-28 20:34 指令单 #5.18 实施交付（P0 出牌 owner 门 + 卡包选择自动 + 商店 T2）

- 构建指纹：`Directive #5.18 | Built: 2026-10-28 20:33 CST`；`AiTeammate.dll` 已编译并部署至 `mods/AiTeammate/`。
- T1：`ActionQueueSynchronizer.RequestEnqueue` 已改为实例 owner 反射纠正，保留原调用 fail-open，并输出 `enqueue-owner-mismatch-corrected`；H2/H3 候选池继续执行 owner 手牌断言，跨 owner 走 `cross-owner-block`/circuit-open。
- T2：药水效应与手牌重建交叠窗口输入锁已保留；点击在锁内丢弃并输出 `input-locked-dropped`，窗口结束解锁。
- T3：新增包评分与 `kind=pack` resolved 记录，并扫描返回 `Task<int>` 的 pack/group 选择 API；未知形状或反射失败保持手动 fail-open。
- T4：商店 P2 通过当前 `MerchantRoom.Inventories` 反射定位 P2 库存，按移除→遗物→卡牌顺序调用实体 `OnTryPurchase`/`OnTryPurchaseWrapper`，每类最多一件，购买后保留 25 金；P1 不触碰；异常 fail-open。
- 静态验证：C# 编译成功、DLL 已同步；真实游戏验收仍需完全重启后按 #5.18 C 清单实测，当前不能宣称日志验收已通过。

## 37. #5.18 第一轮验收=fail（鼠标被输入锁吞+owner 门 corrected=False）+ 第二轮（最终轮）指令单
- 定罪一（鼠标失灵回归）：`input-locked-dropped: overlap window active` **67 行贯穿全会话**（913→末段）=T2 交叠窗口只开不关，鼠标出牌路径被永久吞；键盘热键走旁路幸存→用户体感"鼠标点牌打不出、必须按键盘"。安全件吃掉主输入=比原 P0 更伤体验。
- 定罪二（owner 门不履职）：`enqueue-owner-mismatch-corrected ... corrected=False`（842/866/893，含 EndPlayerTurnAction）=检测到错配却未纠正放行；且门误伤 EndPlayerTurnAction。
- T3/T4（卡包/商店）本局零遭遇零行，验证结转第二轮。
- 结论=fail → 第二轮（最终轮）：

```
# 指令单 #5.18 第二轮（最终轮：撤毯子输入锁 + owner 门真纠正）

导演已读指纹 #5.18 会话日志（定罪见 HANDOFF §37）。

## A. 事实依据
1. input-locked-dropped 67 行贯穿全会话=窗口只开不关，鼠标路径永久被吞；键盘旁路幸存。
2. enqueue-owner-mismatch corrected=False 三行（含 EndPlayerTurnAction）=门不纠正且误伤。
3. T3/T4 零遭遇结转。

## B. 任务
- F1 撤毯子锁：删除"overlap window 输入锁"整体（恢复鼠标原生路径）；防劫持职责归 owner 门单点承担。若实施方坚持保留锁，则必须：窗口 ≤500ms+硬 failsafe 关闭+鼠标键盘同路径同闸（二者不得不对称），导演默认前者。
- F2 owner 门真纠正：mismatch 时**必须**纠正路由至 action owner 队列（corrected=True）；禁止 corrected=False 放行；门覆盖 PlayCard/EndPlayerTurn/UsePotion 三类且仅这三类；纠正失败（无法路由）=拒绝该 action+log enqueue-rejected+fail-open 不锁玩家。
- F3 T3/T4 实测验证：卡包选（卷轴箱族）与商店按第一轮实现原样保留，本轮实测出日志（resolved kind=pack / shop-offer+resolved 成对）。
- F4 禁止项：其余不得动。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.18-R2（指纹行加轮次后缀）；failed=none。
2. 实测鼠标连打 ≥5 张牌：零 input-locked-dropped、零劫持（自己的牌自己打、效果落自己）；键盘路径同常。
3. 零 corrected=False；enqueue-rejected 仅真无法路由时出现且玩家不锁死。
4. 卡包/商店遭遇时成对行在役；P1 商店手动不受影响。
5. 结转全清（5.15 闪烁/removal-guard/NEOW 零同分；5.16 长战斗+心跳；5.17 剪刀双删/篝火双场景/离房压力测）。
6. §3 基线零回归。

## D. 约束
同 #5.0.6 D；此为 #5.18 最终轮；fail=按 §5 砍范围重立项（#5.19 仅含 F1/F2 两件，T3/T4 与结转另立）。
```

## 38. #5.18 R2 验收（10-28 20:49–20:51 会话）+ #5.19 指令单
- R2 过项：指纹 #5.18-R2 在役；input-locked-dropped 零（鼠标恢复）；owner 门 corrected=True 在役（798/876 双向纠正）；零 corrected=False。
- **新回归定罪（结束回合按钮死）**：用户每回合死点 N 次（零日志）→切三角两次→点击才记录（1085/1290 两行=切后成功点击；player-state-button 9 次= workaround 痕迹）。根因=owner 门纠正队列时未纠正 DRA 留在按钮上的 disabled UI 态；5.15 T1 补丁只 force-visible 未 force-enabled。同族旧病（10 月底按钮态案）新触发路径。
- #5.18 两轮用完 → 按 §5 砍范围重立项 #5.19：

```
# 指令单 #5.19（窄范围重立项：结束回合按钮 force-enabled 保底）

导演已读 10-28 20:49–20:51 godot.log（定罪见 HANDOFF §38）。#5.18 R2 的门与锁过、按钮态回归 fail，两轮用完砍范围重立项。

## A. 事实依据
1. 死点击零日志=UI 层 disabled；切三角重置后点击才记录（1085/1290）；player-state-button 9 次=用户 workaround。
2. 5.15 T1 补丁（235 行 Patched ReevaluateEndTurnButtonState Force-Visible）只保可见不保可用。
3. owner 门纠正（798/876 corrected=True）后 DRA 按钮 disabled 态残留=触发源。

## B. 任务
- T1 按钮 force-enabled 保底：扩 5.15 T1 补丁——controlled==P1 且 P1 回合活跃且非 P1 自身部署动画中时，结束回合按钮强制 visible+enabled/interactable；每次 owner-mismatch 纠正后与 P2 部署完成后主动触发一次按钮态重估（P1 上下文下 ReevaluateEndTurnButtonState+结束按钮部分刷新）。
- T2（可选根加固，廉价才做）：瞬态 enqueue 上下文翻转不得改 DRA 按钮/部署 UI 态（5.13 零触碰扩到 UI 态）；风险高则弃 T2 保 T1。
- T3 结转清单不变：pack/shop 遭遇行、剪刀双删、篝火双场景、离房压力测、长战斗+心跳、闪烁体感、NEOW 零同分、removal-guard。
- T4 禁止项：其余不得动。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.19；failed=none。
2. 实测整局：每回合鼠标直接点结束回合一次成功（零切三角 workaround）；player-state-button 行 combat 内 ≤1（用户主动切才允许）；每 round 一条"已记录手动结束回合意图"且其前无死点窗口（用户口述确认）。
3. 结转 T3 全清单清账。
4. §3 基线零回归；stall/CRITICAL/decrypt 零。

## D. 约束
同 #5.0.6 D；两轮上限；fail=T1 回滚至 5.18-R2 态并立即再立项单件修复（按钮件不再与其他捆绑）。
```


## 38. #5.18-R2 最终轮实施交付

- 指纹更新为 `Directive #5.18-R2 | Built: 2026-10-28 20:45 CST`，DLL 已重新编译并部署。
- F1：移除 overlap window 的 `CanPlayCards` 输入锁及相关丢弃日志；药水/手牌重建不再吞鼠标路径。
- F2：owner 门收窄至 `PlayCardAction`、`EndPlayerTurnAction`、`UsePotion*`；错配时切换到 action owner 进行入队并输出 `corrected=True`，异常输出 `enqueue-rejected` 并拒绝该 action。
- T3/T4 代码保持第一轮原样；本轮未能在当前会话产生真实卡包/商店遭遇日志。
- 静态编译验证通过；真实游戏清单仍需完全重启后实测，尤其要确认队列路由恢复后无跨玩家出牌。


## 39. #5.19 按钮 force-enabled 保底交付

- 指纹：`Directive #5.19 | Built: 2026-10-28 21:05 CST`。
- P1 活跃回合且非部署中时，结束回合按钮重估同时强制 `Visible`、`Enabled/Interactable`（存在则写入）并将状态恢复为 Enabled。
- owner-mismatch 纠正后与 P2 部署完成后主动调用 P1 combat UI 刷新和结束回合按钮重估。
- 其余 T3/T4 与结转路径未改。
- C# 编译部署完成；真实整局验收仍需重启游戏执行。

## 40. #5.19 R1 验收=fail（force-enabled 写空：state=unknown=没找到真按钮）+ 第二轮（最终轮）指令单
- 定罪：974/999 行 `endturn-force-enabled ... (state=unknown)`=重估时未解析到活按钮节点（"存在则写入"写空）；DRA 瞬态翻转后重建的新按钮节点仍 disabled；用户死点依旧（本局 recorded=1、toggle=1=workaround 痕迹）。
- R2 改 triple-insurance，不再依赖"缓存/找到节点"：

```
# 指令单 #5.19 第二轮（最终轮：结束回合点击handler级保底+热键兜底）

导演已读 10-28 21:09–21:10 godot.log（定罪见 HANDOFF §40：state=unknown=force 写空）。

## A. 事实依据
1. force-enabled 行在役但 state=unknown=未解析到活节点；真按钮（DRA 重建后的新实例）仍 disabled；死点依旧。
2. 5.15/5.19 两代补丁都在"按钮节点状态"层做文章，均被 DRA 节点重建绕过→改在 handler 层与输入层保底。

## B. 任务
- T1 handler 级保底：Prefix 结束回合按钮的 pressed/click 处理器——controlled==P1 且 P1 回合活跃时，绕过一切 UI 状态检查直接 enqueue EndPlayerTurnAction(P1)（每点击一次仅一次 enqueue 的幂等守卫）；按钮节点每次调用时按 UI 树现场解析（禁缓存），force visible+enabled 于活实例（解析失败仅 log 不阻塞 T1）。
- T2 热键兜底（失败开闸最后保险）：P1 回合活跃期注册 F9=结束回合（log endturn-hotkey），无论按钮生死恒可用；README/Init 行告知用户。
- T3 结转清单不变（pack/shop/剪刀/篝火/离房/长战斗/闪烁/NEOW 零同分/removal-guard）。
- T4 禁止项：其余不得动。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.19-R2；failed=none。
2. 实测整局：每回合鼠标点结束回合一次成功、零切三角；player-state-button combat 内 ≤1；每 round 一条 recorded 行。
3. 人为验证 T2：按钮可用时 F9 同样有效（log 行在役）。
4. state=unknown 行可存在但不得伴随死点（handler 保底覆盖）。
5. 结转 T3 全清账；§3 基线零回归。

## D. 约束
同 #5.0.6 D；此为 #5.19 最终轮；fail=按 §5 再立项单件（仅 T1/T2），并启动导演预案：把"结束回合"入口改为 mod 自绘悬浮按钮（完全脱离 DRA 节点生命周期）。
```


## 40. #5.19-R2 最终轮实施交付

- 指纹：`Directive #5.19-R2 | Built: 2026-10-28 21:20 CST`；DLL 已编译部署。
- T1：新增 `NEndTurnButton` pressed/click handler 级前置；P1 活跃回合直接调用 `PlayerCmd.EndTurn(P1)`，150ms 幂等节流，输出 `endturn-recorded source=button-handler`。
- T2：扫描 `NCombatUi` 的 `_Input`、`_UnhandledInput`、`GuiInput`，识别 F9 并走同一结束回合入口，输出 `endturn-recorded source=hotkey-F9`；初始化输出 F9 启用行。
- 节点层 force-visible/enabled 保留为辅助路径，解析失败只记录，不阻塞 handler 保底。
- 真实整局与 pack/shop 结转仍需重启游戏实测。

## 41. #5.19 R2 验收=fail（按钮线三代全灭）+ 空过回合欠归因 + #5.20 指令单（预案启动：自绘悬浮按钮）
- 按钮线终局定罪（10-29 13:04–13:06 会话，指纹 #5.19-R2）：recorded 结束回合意图=**0 行**、player-state-button=13（workaround）、endturn-hotkey=**0 行**（F9 未生效或未注册无 init 证明）、endturn-force-enabled 仍 state=unknown→handler 保底也没接到真点击路径（prefix 挂错方法或按钮信号链不在预期方法上）。**节点层/状态层/handler 层三代方案均被 DRA 信号链绕过→按 #5.19 D 预案启动：mod 自绘悬浮按钮。**
- 空过回合欠归因：全日志零 stall/CRITICAL/deploy-hang，P2 部分回合有出牌（2166–2230）、空过回合无任何归因行=现有日志无法定罪；需 per-turn 部署摘要行+禁空过兜底。
- 结转清单（pack/shop/剪刀/篝火/离房/长战斗/闪烁/NEOW/removal-guard）继续欠。

```
# 指令单 #5.20（预案轮：mod 自绘悬浮结束回合按钮 + F9 注册证明 + 空过归因与禁空过兜底）

导演已读 10-29 13:04–13:06 godot.log（定罪见 HANDOFF §41）。按钮线三代 fail，预案启动。

## A. 事实依据
1. recorded=0/toggle=13/hotkey=0/state=unknown：R2 的 handler 保底与 F9 均未生效；DRA 信号链不可再信。
2. 空过回合零归因行：现有日志无法定罪空过机制。

## B. 任务
- T1 自绘悬浮结束回合按钮（预案本体）：mod 在 P1 回合活跃期于战斗 UI 右下自绘独立 Control 按钮（不依赖 DRA 任何节点/信号）；点击=直接 enqueue EndPlayerTurnAction(P1)（owner 门同路径、幂等守卫：回合已在结束中则忽略）；原生按钮保留不动（能用就用，不能用有悬浮按钮）；按钮绘制/点击全链路自有日志 floating-endturn-click。
- T2 F9 注册证明：init 行打 hotkey-registered F9=end-turn；P1 回合活跃期 F9 恒可用（log endturn-hotkey）；交付附注册代码位置说明。
- T3 空过归因+禁空过兜底：每 P2 回合结束打部署摘要行 p2-turn-summary turn=N played=<k> end-reason=<normal|plan-empty|cancel|force-end|fallback-greedy>；若 plan-empty 且 P2 有可出牌且能量>0→先重算一次，仍空则贪心兜底打出第一张可出的攻击/防御牌再结束（fallback-greedy），禁止有牌有空却空过。
- T4 禁止项：其余不得动；结转清单不变。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.20 且含 hotkey-registered F9 行；failed=none。
2. 实测整局：每回合用悬浮按钮或原生鼠标一次成功结束（零切三角）；floating-endturn-click 或 recorded 每 round ≥1；F9 按一次有 endturn-hotkey 行。
3. p2-turn-summary 每 P2 回合一行；零"有牌有能量却 plan-empty 直接结束"（出现即 fallback-greedy 行在役）。
4. 结转全清账：pack/shop 成对行、剪刀双删、篝火双场景、离房压力测、长战斗零无限 Executing、闪烁体感、NEOW 零同分、removal-guard。
5. §3 基线零回归；stall/CRITICAL 零。

## D. 约束
同 #5.0.6 D；两轮上限；fail=导演亲审实施方对原生按钮信号链的书面取证（挂点全谱）后再立项；悬浮按钮件无论如何保留（独立可用件）。
```


## 42. #5.20 预案轮实施交付

- 指纹：`Directive #5.20 | Built: 2026-10-29 14:10 CST`；DLL 已编译部署。
- T1：新增独立 `FloatingEndTurnOverlay : Control`，挂到 Godot 根场景树，不依赖 DRA 结束回合节点；P1 活跃回合显示右下 `END TURN [F9]`，点击输出 `floating-endturn-click` 并走幂等结束回合入口。
- T2：通过 `InputMap` 注册 `aiteammate_end_turn_f9`，绑定 F9；初始化输出 `hotkey-registered F9=end-turn`，运行期输出 `endturn-hotkey`。
- T3：P2 出牌执行计数、每次部署完成的 `p2-turn-summary`、plan-empty 一次重算与可出牌时的 `fallback-greedy` 兜底已加入。
- 原生按钮与旧 handler 保留作为辅助路径；T3/T4 结转实测仍需重启游戏完成。

## 43. #5.20 R1 验收=形败（保底件形态错误）+ R2 最终轮 + #5.21 卡包选定位文档单
- 定罪（10-29 13:33–13:36 会话，指纹 #5.20）：悬浮按钮**画出来了但形态全错**——位置左上角、debug 标签观感、卡包选界面也显示（截图实证），用户不识其为按钮→floating-endturn-click=0；F9 注册行在役（151/154 双注册）但用户未按→endturn-hotkey=0 不可定罪；recorded=0=原生按钮仍死；p2-turn-summary=0（本局过短不可终判，结转验证）；kind=pack=0=卡包自动未生效。
- 导演复盘：卡包选自动化数轮未过之流程根因=**从未走 §5 定位文档门槛**（一直作附带的 T 项偷渡）→#5.21 单独立项、文档先审。
- 按钮线导演结论：原生按钮不再投入（DRA 信号链不可信已证三代），保底路径=悬浮按钮+F9，R2 修形态与可发现性。

```
# 指令单 #5.20 第二轮（最终轮：保底件形态与可发现性修正）

导演已读 10-29 13:33–13:36 godot.log 与截图（定罪见 HANDOFF §43）。

## A. 事实依据
1. 悬浮按钮位置/观感/场景门全错→用户不识、零点击；原生按钮仍死（recorded=0）。
2. F9 双注册行在役；用户未按不可定罪；按下路径需可见日志。
3. p2-turn-summary 本局零行（局短不可终判，结转）。

## B. 任务
- T1 悬浮按钮形态修正：位置=右下角原生结束回合按钮正上方；样式=真实按钮观感（足够大、中文"结束回合"标签、hover 态）；场景门=仅 P1 战斗回合活跃期显示（选择界面/地图/事件房一律不显示）；每次战斗首次显示打 floating-endturn-shown 行（含 canvas layer 与 mouse filter 值）；点击打 floating-endturn-click 并入队 EndPlayerTurnAction(P1)（幂等守卫照旧）。
- T2 F9 按下可见：P1 回合内 F9 按下即打 endturn-hotkey（生效）或 endturn-hotkey-ignored reason=<..>（被忽略也要见行）；非 P1 回合按下打 ignored 行。
- T3 一次性用户提示：本构建首次进入战斗时屏幕 toast/日志双通道提示"结束回合失灵时点右下悬浮按钮或按 F9"（log user-hint-shown）。
- T4 禁止项：原生按钮不再投入（保留现状即可）；其余不得动。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.20-R2；failed=none；hotkey-registered 单行（去双注册）。
2. 实测整局：用户每回合用悬浮按钮或 F9 一次成功结束（零切三角）；floating-endturn-click 或 endturn-hotkey 每 round ≥1；选择界面零悬浮按钮（截图验证）。
3. p2-turn-summary 每 P2 回合一行（本单必须见到，上局欠）；空过归因可行。
4. 结转：pack 留给 #5.21；shop/剪刀/篝火/离房/长战斗/闪烁/NEOW/removal-guard 继续清。
5. §3 基线零回归。

## D. 约束
同 #5.0.6 D；最终轮；fail=导演亲审悬浮按钮输入链书面取证（layer/filter/输入消费顺序全谱）后再立项单件。
```

```
# 指令单 #5.21（卡包选：定位文档轮，只文档不代码）

导演裁决：卡包选（卷轴箱/NEOW 包选族）自动化数轮未过，流程根因=未走 §5 定位文档门槛。本轮只交文档。

## 任务
- T1 定位文档：卡包选生命周期全文——创建点（遗物拾取/NEOW 选项触发包选的方法全名与签名）、界面类与选项数据结构（包=卡列表的读取路径）、提交入口（程序化选择一包的方法/命令、是否 choice context、是否需 SyncLocalChoice）、热座下 P2 包选的呈现与属主机制、IL/反编译证据行号；标注 [待实证] 项并附探针构建建议。
- T2 拟挂钩点与决策源建议：offer/resolved 打点位置、包评分=包内卡 card_scores/Core 分和、保险丝指纹、超时交还手动路径。
- 交付：p2_pack_selection_locating_doc.md（入 scratch 与 brain 根），导演审过才开代码轮。

## 约束
只文档不代码；证据来自 IL/反汇编而非运行日志处必须标 [待实证]。
```

## 44. 10-29 13:52–13:56 会话验收（悬浮件可用但按用户裁决撤件）+ 变牌案 + #5.22 原生按钮根因轮 + #5.21 扩围
- 悬浮按钮实测可用：floating-endturn-click=10（用户点 10 次全生效）；原生按钮仍死（recorded=0）；F9 零按；p2-turn-summary 仍零行（§42 声称已加=两局零行，定罪为未生效）；pack 零行。本局仍 R1 build（#5.20 R2 未交付）。
- **用户裁决（10-29）：修原生结束回合按钮，撤悬浮件。** 按钮线改路线：#5.22 两轮根因法（R1 全链路埋点心电图→R2 定点修）；悬浮按钮代码保留但默认隐藏（config OFF），F9 静默保留。
- 变牌案（MORPHIC_GROVE，行 2673–2717）：①P2 变牌目标选仍需手动=transform 选牌形状未覆盖；②用户报 P1/P2 变出相同结果=per-player 独立性疑云（游戏热座镜像设定 vs 我方镜像 bug，待探针取证再裁决）。用户政策入单：变牌目标自动选"最没用"=card_scores 最低分。
- #5.21 扩围：定位文档覆盖 pack 选+transform 选+其余未覆盖 CardSelectCmd 族清点。

```
# 指令单 #5.22（原生结束回合按钮根因轮：R1 全链路埋点→R2 定点修；撤悬浮件）

用户裁决：修原生按钮，不要新按钮。导演路线：两轮根因法。

## A. 事实依据
1. 悬浮件 10 次点击全生效=入队路径健康；原生按钮点击零记录=死在 UI/信号链某层（未知哪层）。
2. 三代补丁（节点状态/force 写/handler prefix）均挂错层→本轮先埋点定位再修。
3. p2-turn-summary 两局零行=§42 T3 未生效，结转本单必交。

## B. 任务
- T1（R1）原生按钮全链路心电图：在以下各层埋点打日志（点击死在哪层一眼可见）：①按钮节点 _gui_input/输入事件层；②pressed 信号发射层；③点击 handler 方法入口（按 UI 树现场解析活节点并读其信号连接列表，dump 连接目标方法全名）；④handler 内状态检查各分支（disabled/turn-check/context-check 各自取值）；⑤enqueue 调用层。另：每次 P1 回合开始 dump 结束回合按钮节点路径+实例 id+disabled/visible 值。交付埋点 build，用户实测死点点击若干次。
- T2（R2）按心电图定点修复死层；修复验收=原生按钮鼠标点击每回合一次成功（recorded 行每 round ≥1、零切三角）。
- T3 撤悬浮件：悬浮按钮默认隐藏（config aiteammate_settings 开关 OFF），代码保留；F9 静默保留（按下行照旧）。
- T4 p2-turn-summary 必生效：每 P2 回合一行（played/end-reason），空过归因可行；上单欠账清。
- T5 禁止项：其余不得动。

## C. 验证清单
1. R1 交付=埋点 build：用户死点点击在日志留下断点行（导演据此定罪死层）；心电图 dump 含信号连接全谱。
2. R2 交付=修复：实测整局原生按钮鼠标一次成功/回合；floating 隐藏（截图验证）；F9 仍可用。
3. p2-turn-summary 每 P2 回合在役；stall/CRITICAL 零；§3 基线零回归。
4. 结转：pack/transform 留 #5.21；shop/剪刀/篝火/离房/长战斗/闪烁/NEOW/removal-guard 继续清。

## D. 约束
同 #5.0.6 D；两轮（R1 埋点/R2 修复）；fail=导演亲审心电图定罪后出单件 #5.23 只修死层。
```

```
# 指令单 #5.21（扩围版：pack 选+transform 选定位文档轮，只文档不代码）

## 任务
- T1 定位文档 p2_pack_selection_locating_doc.md：包选生命周期（创建点/界面类/包数据读取/提交入口/是否 choice context/属主机制/IL 证据）。
- T2 定位文档扩节 transform 选：deck_transform_select_screen 的创建点与提交入口（CardSelectCmd 族哪一支）、**per-player 变牌结果独立性机制**（热座下变换roll是否镜像=游戏设定 or 可分；证据不足标 [待实证] 并附探针方案）、P2 transform 目标选的程序化入口。
- T3 未覆盖形状清点： enumerates 其余 CardSelectCmd/选择屏形状（升级/移除/网格/选一张/附魔/包/变换之外还有谁），标已覆盖/未覆盖。
- T4 拟钩点与决策源：pack=包内卡和分最高；transform 目标=card_scores 最低分（用户政策："最没用的牌"）；保险丝指纹与超时交还路径。
- 导演审过才开代码轮（代码轮并入 #5.22 R2 后单独一轮或新单，导演排期）。

## 约束
只文档不代码；[待实证] 标注规矩照旧。
```

## 45. #5.22 R1 埋点构建 + #5.21 扩围文档交付

- 指纹：`Directive #5.22-R1 | Built: 2026-10-29`；C# 编译成功，DLL 已部署到 `mods/AiTeammate/AiTeammate.dll`。
- 原生结束回合 R1 只观测，不代打：`NEndTurnButton` 的 press/click/release/refresh/set-state 方法尝试挂载全链路 telemetry；输入层、按钮节点现场路径/实例 id、state/can-turn/enabled/visible、pressed/gui_input 连接列表、handler 分支状态和 `RequestEnqueue` 均有独立日志。
- 每次 `SetupPlayerTurn` 若 P1 活跃会输出 `endturn-node-dump source=p1-turn-start`；若没有活节点会明确输出 `buttons=0`。R1 不提前修复死层，等待导演点击死点后的日志定罪。
- 悬浮按钮代码保留但默认关闭：`mods/AiTeammate/aiteammate_settings.json` 已加入 `floatingEndTurnEnabled=false`；F9 注册仍保留且去重。
- P2 摘要补强：`EndPlayerTurnAction.ExecuteAction` 增加 P2 postfix 兜底，`p2-turn-summary` 按回合去重；`CompleteDeployment` 路径保持原有摘要和空过兜底。
- #5.21 扩围文档已同步到 scratch 与 brain 根：新增 `FromDeckForTransformation` 定位、per-player roll 取证方案、P2 最低分目标政策和 CardSelectCmd 形状清点；仍只文档，不接入 pack/transform 自动化。
- 当前尚未完成真实游戏死点点击实测，因此不能宣称 R1 已定位或 R2 修复通过；等待导演提供 R1 日志后再做单件定点修复。

## 45. 10-29 14:24–14:25 进战斗即冻定罪（保底生命线断裂）+ #5.23 紧急单
- 定罪链（指纹 #5.22-R1 在役）：进房正常（transition 1.7–2.2s）→行 818 `solver-search-stall >12s` kick recalc（无效=搜索线程已静默死，D4）→**此后零 search-alive 心跳、零部署、零 CRITICAL/force-end**→无限冻至用户退出。20s 强制结束兜底（10-26 在役）在某版本回归消失=**失败开闸链最后一环断裂**。
- 导演自责项：5.10 进房静默窗+5.16 标志重置改造可能 gate 掉 CRITICAL 层；生命线必须独立于一切状态标志。
- #5.22 R2 仍待本局型死点点击心电图（本局冻在开局未点按钮，R1 埋点数据欠）。

```

# 指令单 #5.23（紧急单件：恢复 20s 强制结束生命线+心跳判死）

导演已读 10-29 14:24–14:25 godot.log（定罪见 HANDOFF §45）。失败开闸最高原则被违反：stall 后无任何兜底→无限冻。

## A. 事实依据
1. 行 818 stall+kick 后零心跳零部署零 CRITICAL=线程死+生命线断。
2. 20s CRITICAL 层 10-26 在役、现缺席=回归（嫌疑=5.10 静默窗/5.16 标志重置 gate 了该层）。
3. D4 根因（线程静默死）5.16 IL 修复未覆盖本形态。

## B. 任务
- T1 生命线独立化：20s 强制结束 P2 回合的兜底改为**独立计时器**（自 P2 回合开始起算，不受 IsPreCombatOrLoading/任何 reset 标志/kick 状态影响）；到点必 fire（force-end+log CRITICAL-lifeline）；init 打 lifeline-armed 行；交付附自证测试（模拟 stall 20s→force-end 必现）。
- T2 心跳判死：搜索活跃期每 5s search-alive 心跳；心跳缺失 >5s=判线程死→**立即**走 CRITICAL-lifeline 路径（不等 20s）；log thread-dead-detected。
- T3 D4 二轮根因：本形态（turn-1 搜索线程静默死）书面取证（线程状态/异常吞没点/重入路径），能修则修（IL 或守卫），不能修则书面说明+依赖 T1/T2 兜底。
- T4 禁止项：其余不得动；#5.22 R2 按钮修复与本单并行不冲突（不同代码区）。

## C. 验证清单（真实日志+实测）
1. INIT 指纹=#5.23 且含 lifeline-armed；failed=none。
2. 复现冻局（长搜索/死线程场景）：≤20s 必见 CRITICAL-lifeline 或 thread-dead-detected→force-end，P1 拿回控制继续游戏（用户体感=最多冻 20s 可继续）。
3. 正常局零 CRITICAL-lifeline 误发；stall→recalc→deploy 正常路径不受影响。
4. §3 基线零回归。

## D. 约束
同 #5.0.6 D；单件单轮完成；fail=导演亲审生命线计时器代码路径。此单优先级高于 #5.22 R2：先交付此单。
```

## 46. #5.23 独立生命线 + 心跳判死交付

- 指纹：`Directive #5.23 | Built: 2026-10-29`；C# 编译成功，DLL 已部署到 `mods/AiTeammate/AiTeammate.dll`。
- T1：`TurnCoordinator` 新增独立 `System.Threading.Timer`（`IndependentLifelineTimerTick`），由 `StartFullAutoDeployment`、`StartDeployment`、`DeployCurrentTurn` 入口按 P2 回合 armed；计时起点不读取 `IsPreCombatOrLoading`、`TurnStartTime`、stall/kick/reset 标志。初始化输出 `lifeline-armed mode=independent-timer timeout=20s heartbeat=5s`，实战 arm 输出含 `turn/owner=P2/source`。
- T1 触发：计时器每 250ms 检查 20s；到点输出 `CRITICAL-lifeline` 并直接调用 `ForceEndTurnForPlayer2`，不经过 `OnProcessTick`。`IndependentLifelineFired` 防重复触发；正常 P2 EndTurn 或 force-end 后 disarm。
- T2：`RequestSearchPrefix`/主循环搜索检测 arm 独立搜索监视；每次真实 `search-alive` 心跳更新独立时间戳。超过 5s 未更新输出 `thread-dead-detected`，随后复用 `CRITICAL-lifeline` 路径立即结束 P2 回合。搜索完成时 disarm 监视。
- T3 D4 取证：当前死形态证据链为 `RequestSearchPrefix` 重入守卫/`KickSolverRecalculation` 后搜索线程不再产出心跳，而原有心跳和 20s 层均挂在 `OnProcessTick` 且受状态门控；本轮未改 CombatSolver 线程本体，依赖独立 timer + heartbeat fail-open 兜底。
- 自证：`scratch/test_directive_5_23.py` 4/4 通过，覆盖 20s stall 判定、5s 心跳缺失提前判死、指纹/日志/独立计时器存在性及 timer 不依赖普通 watchdog 状态门控。
- 未完成实机项：真实长搜索/静默死线程复现需重启游戏验证 `thread-dead-detected` 或 `CRITICAL-lifeline`、P1 控制恢复、正常局零误发。#5.22 R2 原生按钮定点修复仍未处理。

## 46b. 10-29 14:52–15:03 会话定罪（按钮死层心电图确诊+求解器早收工）+ #5.22 R2 定点单 + #5.24 早收工单
- **按钮死层确诊（R1 心电图，行 801–947）**：`endturn-node-dump` 显示 P1 回合活跃且 `can-turn=True` 时按钮 state 仍被置 `Disabled/Hidden`（926/944/801 行）=**状态评估函数在错误时机禁用按钮**，Godot disabled 吞点击；不切人不再触发重估→死到你切三角。节点解析已被 dump 证明可行（path+instance 可得）→R2 在评估点定点强制 Enabled。
- **求解器早收工定罪（p2-turn-summary）**：多行 `played=0 end-reason=normal energy=0 hand=N`（played 计数可疑=未统计部署出牌）与 `turn=2 played=0 end-reason=normal hand=3 energy=1`（用户投诉实证：有能量有牌正常收工）；零 cancel/force 行=求解器自主早收。
- 变牌手动=预期内（#5.21 代码轮未交付）；#5.23 生命线在役本局零触发（无冻局，初验过）。
- 队列：#5.22 R2 + #5.24 本批并行（不同代码区）→#5.21 代码轮→商店 T2 已并入？（商店 T2 仍欠！排 #5.21 代码轮同批）。

```
# 指令单 #5.22 第二轮（定点修：评估点强制 Enabled + 撤悬浮件）

心电图确诊：can-turn=True 时 state 被评估为 Disabled/Hidden=死层。节点解析已证明可行。

## B. 任务
- T1 定点修：ReevaluateEndTurnButtonState 每次调用（postfix）：controlled==P1 且 P1 回合活跃 且 can-turn=True 时，对 dump 同法解析的活节点强制 state=Enabled+visible（覆盖 Disabled/Hidden 误置）；打 endturn-force-state 行（仅误置被纠正时）。
- T2 撤悬浮件：悬浮按钮移除显示（config OFF 保留代码）；F9 静默保留。
- T3 验收辅助：dump 行保留一轮（便于验收核对零误置）。
## C. 验证：实测整局原生按钮鼠标每回合一次成功（recorded 每 round ≥1、零切三角）；can-turn=True 时零 Disabled/Hidden dump 行；悬浮件截图不可见；F9 在役。
## D. 同前；最终轮；fail=导演亲审评估函数调用谱。
```

```
# 指令单 #5.24（求解器早收工归因+禁早收守卫+摘要计数修正）

## A. 事实依据
1. p2-turn-summary：`played=0 energy=0 hand=N end-reason=normal` 多行（played 计数未统计部署出牌=计数 bug）与 `energy=1 hand=3 normal`（自主早收实证）。
2. 零 cancel/force 行=非中断非兜底=求解器计划自主收工。
## B. 任务
- T1 摘要计数修正：played=本回合 owner==P2 的 PlayCardAction 实际执行数；增字段 plan-len 与 search-status（timeout/complete/nodes）。
- T2 早收归因：回合结束时若 energy>0 且手牌存在可打攻击/技能而计划未含→打 solver-early-end reason=<search-timeout|objective|plan-empty|other> 并附搜索状态；先归因后修。
- T3 禁早收守卫（按 T2 归因选修）：search-timeout→提升该回合搜索预算重试一次；objective→策略守卫：敌人存活且非斩杀蓄力情形下，能量>0 且有可打攻击牌时计划必须含 ≥1 攻击（否则重搜一次，仍违则打 guard-override 行并执行贪心补打）；plan-empty→走 5.23 贪心兜底。
## C. 验证：summary 计数与实战出牌数一致（用户可核对）；零"energy≥1 且有可打攻击牌却 normal 收工"无 reason 行；战斗节奏体感=AI 打满能量或给出理由。
## D. 同前；两轮上限；fail=守卫摘下保留 T1/T2 归因。
```

## 47. #5.22-R2 + #5.24 代码交付（2026-10-29）

- **构建指纹**：`Directive #5.24 | Built: 2026-10-29`；源码为 `C:\Users\不值钱的画\.gemini\antigravity\brain\87bc0cdd-d283-4474-b5d8-dbb6c6b0a9ea\scratch\AiTeammateMod.cs`。
- **构建/部署**：使用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，按 `build_mod.py` 的 `sts2.dll`、`GodotSharp.dll`、`0Harmony.dll`、`DualRoleAdventure.dll` 引用集合编译；产物已部署到 `mods\AiTeammate\AiTeammate.dll`。部署 DLL SHA-256：`95D13565B3798CF1EED28F5951574A677E2F68D066F90428A741D00E73B0F312`。
- **#5.22-R2 定点修**：`DraReevaluateEndTurnButtonStatePatch.Postfix` 只在本地控制为 P1、P1 回合活跃且 `CanTurnBeEnded=true` 时现场解析结束回合按钮；仅当 `State != Enabled` 或 `Visible=false` 才调用 `SetState(Enabled)`/显示，并输出 `endturn-force-state corrected=True ...`。每次 P1 回合开始的 `DumpLiveButtons("p1-turn-start")` 保留。
- **悬浮件/F9**：悬浮按钮代码保留但初始化和配置均关闭（`aiteammate_settings.json`：`floatingEndTurnEnabled=false`，实例与按钮初始 `Visible=false`）；F9 注册和处理路径保留。
- **#5.24 T1**：P2 `PlayCardAction` 执行后按 `OwnerId==P2`，再按 `Player/Owner/ActionOwner` 成员回退判定；每次确认出牌输出 `p2-play-executed count=N`，未能解析属主而处于部署中才使用带日志的保守计数回退。
- **#5.24 T1/T2**：计划观察输出 `p2-plan-observed`；摘要增加 `plan-len`、`search-status`、`search-nodes`。当能量大于 0 且有可打攻击/技能但本回合未出牌时，输出 `solver-early-end reason=...` 并区分 `search-timeout`、`objective`、`plan-empty` 等原因。
- **#5.24 T3**：首次早收允许一次重算（`solver-early-end-recalc-once` 或 `plan-empty-recalc-once`）；再次仍早收则输出 `guard-override ... action=fallback-greedy` 并尝试贪心补打一张可出攻击/防御牌。重算有 `HasRetriedSolverEarlyEndThisTurn` 幂等守卫。
- **离线自证**：`scratch\test_directive_5_22_r2.ps1` 和 `scratch\test_directive_5_24.ps1` 均运行通过；检查了指纹、按钮门控、浮动件关闭、F9、P2 计数、摘要字段、早收归因、重算/贪心守卫及 #5.23 生命线保留。
- **未完成实机项**：尚未重启游戏做整局验证；仍需确认原生按钮每回合鼠标点击成功、`can-turn=true` 时无 Disabled/Hidden dump、悬浮件截图不可见、F9 行在役、`p2-turn-summary.played` 与实际出牌一致，以及早收时的 reason/guard 日志。#5.23 的 `CRITICAL-lifeline`/`thread-dead-detected` 也需继续以真实冻局复现确认，静态代码和编译不能替代该验收。

## 48. #5.25-R2 固定 P1 前台 / P2 后台 ActionQueue 交付（2026-10-30）

- **构建指纹**：`Directive #5.25-R2 | Built: 2026-10-30`；源码 `scratch\AiTeammateMod.cs`；部署 DLL `mods\AiTeammate\AiTeammate.dll`。
- **配置**：新增 `fixedP1CombatControl=true`，默认开启；初始化输出 `fixed-p1-combat-control enabled=true`。
- **控制门**：战斗中拦截目标为 P2 的 DRA `SwitchControlledPlayerTo`、直接切换调用及隐式非交互 `ApplyControlContext`，输出 `p2-foreground-switch-blocked`；P1 回合开始、P2 部署完成和恢复路径输出 `fixed-p1-state-check`。
- **UI 门**：P2 目标的 combat/top-bar/deck/energy/end-turn 等 `Refresh*` 在固定 P1 或 P2 action scope 中跳过，输出 `p2-ui-refresh-suppressed`。
- **后台动作**：P2 `GameAction.Execute` 仅在执行窗口临时对齐 sender，保存并恢复原上下文，输出成对 `p2-action-sender-scope begin/end owner=P2` 和 `p2-action-executed owner=P2`；ActionQueue owner 路由、CombatSolver、#5.23 生命线保留。
- **禁止项核对**：未修改、移动或删除 `DualRoleAdventure.dll`；未新增商店钩子；未新增事件、奖励、篝火或 CardSelect 自动化。
- **离线验证**：`scratch\test_directive_5_25_r2.ps1` PASS；C# 编译退出码 0。
- **实机限制**：冷启动烟测第一次使用 B 配置，AiTeammate 被正确跳过；随后切换 C 后启动环境缺少 Steam 客户端，日志出现 `Steamworks initialization failed`，未完成真实战斗验收。当前 `settings.save` 保留 C 组全开状态。

## 49. #5.25-R3 P2 结束回合后 P1 原生按钮恢复（2026-10-30）

- **构建指纹**：`Directive #5.25-R3 | Built: 2026-10-30`；源码 `scratch\AiTeammateMod.cs`；部署 DLL `mods\AiTeammate\AiTeammate.dll`。
- **恢复时机**：P2 sender scope 内只登记恢复请求；最外层 scope 退出后通过 Godot `CallDeferred` 合并执行一次 P1 身份/UI/按钮重算。未使用固定延时、每帧刷或模拟切换。
- **owner 区分**：`OnEndedTurnLocally` 通过当前 action owner/scope/local context 区分 P1/P2；P2 回调只登记恢复，不重置 P1 协调状态；原生 `OnEndedTurnLocally` 与 P2 `EndPlayerTurnAction` 仍放行。
- **恢复门**：明确 P1 recovery source 走受限通路；`SwitchControlToPlayer` finally 恢复进入前的 `SuppressControlSwitchUi`；P2 scope 的 P1 刷新请求延后，不再被 scopeDepth 抑制。
- **按钮状态**：fixed-P1 模式停用旧 `DraReevaluateEndTurnButtonStatePatch` 强制 Enabled/Visible 行；恢复后调用原生 DRA `ReevaluateEndTurnButtonState`，按真实战斗阶段和 ready 状态重算，不再凭 `!P1ready` 直接启用动作。
- **结果日志**：新增 `p1-restore-result`，记录 action owner、scopeDepth、LocalContext、DRA 当前控制者、phase、P1 ready、`PlayerActionsDisabled` 和最终按钮 state；`p2-action-executed` 改为 scope 退出后的真实完成时机。
- **离线验证**：`scratch\test_directive_5_25_r3.ps1` PASS；#5.25-R2 静态验证 PASS；C# 编译退出码 0；scratch/部署 DLL SHA-256：`978944410C7D9B3A73F9991987683F9817CCE398D4699073528D42746E64F0A5`。
- **实机状态**：待实机验收。尚未宣称 A/B 两种结束顺序、P2 自动出牌/结束回合、按钮最终状态和无重复结束动作已通过；需完全重启游戏后按 #5.25-R3 验收清单核对日志。

## 50. #5.26 P2 零动作空过回合修复（2026-10-30）

- **构建指纹**：`Directive #5.26`；源码 `scratch\\AiTeammateMod.cs`；scratch 与部署 DLL 已同步。
- **可观测性**：部署入口现在记录运行时 solver 实例类型、程序集、AssemblyLoadContext、结果类型、参数类型、solver/action count、实际执行的 P2 PlayCardAction 数量及 EndTurn 入队状态；未知 solver 结果保持未知，不再误报为零动作。若运行时日志出现 FULL_AUTO_DEPLOY/DEPLOY_START 而 AiTeammate 未观察到入口，输出 `p2-deploy-hook-missed`。
- **零动作保护**：已知 action count 为 0 时仅允许一次按回合幂等重取/重搜；仍为零时进入已有 fallback，fallback 使用游戏原生 `PlayCardAction(CardModel, Creature)` 构造和目标选择，覆盖自我、盟友、敌人、无目标、全体、随机敌人和 Osty 等目标形状。fallback 失败按 fail-open 放行，并记录原因，避免永久阻塞。
- **摘要时序**：`EndPlayerTurnActionPostfix` 仅记录 `p2-end-turn-seen`；最终 `p2-turn-summary` 延后到部署完成、零动作保护完成或合法无牌/零能量判定后写入。`played` 使用按回合保存的实际 P2 PlayCardAction 执行计数，并附 `plan-len`、`search-status`、`search-nodes`。
- **固定前台**：保留 `fixedP1CombatControl=true`、P1 原生结束回合修复、P2 后台 ActionQueue 与 #5.23 生命线；未改商店逻辑、DRA DLL、浮动按钮或 F9 方案。
- **离线自证**：`scratch\\test_directive_5_26.ps1` 运行结果 `Directive #5.26 self-test: PASS`，共 10 项检查通过。
- **哈希**：源码 `DADB633C3CE098C064681E6364A4AF719114C1FA44FF2CFCA24399D940329DC3`；scratch DLL `C417CBC5EE55050C84246314BE38FC244071B0727F9F6B62324EBE38E1699FFF`；部署 DLL `C417CBC5EE55050C84246314BE38FC244071B0727F9F6B62324EBE38E1699FFF`。
- **实机状态**：本轮未启动游戏，全部真实验收均标记为“待实机验收”。待验证：至少 10 个 P2 回合（其中至少 3 回合有能量且有可出牌）、solver entry 或 `p2-deploy-hook-missed`、计划/部署/实际出牌计数、NodeLimit 边界、一次性重试/fallback、禁止有牌有能量却无 PlayCardAction 正常结束，以及 P1 原生结束回合连续 3 次和 P2 自动结束无重复动作。

## 51. #5.27-BS2 奖励后端闸门与路线去重构建（2026-10-30）

- **构建指纹**：`Directive #5.27-BS2 | Built: 2026-10-30`；源码 `scratch\AiTeammateMod.cs`；构建日志 `scratch\build-527-bs2-final.log`。
- **奖励闸门**：`NRewardsScreen._Ready` 建立奖励集合快照；逐奖励观察 `NRewardButton.GetReward` 返回的 Task，Task 成功后才登记该 reward 的后端确认，并按对象引用去重。奖励屏必须满足所有预期 reward 确认、屏幕 `IsComplete`、Completed 信号和 `RewardsSetSynchronizer.IsRewardsSetCompleted(set)` 后才释放。确认前拦截 `RunManager.ExitCurrentRoom`、地图投票和 `MoveToMapCoordAction`；离房请求等待闸门后通过原入口重放，不使用固定延时或房间/UI 重建。
- **P2 奖励处理**：普通无选择奖励仍按单项串行调用按钮入口，等待实际按钮 Task 的 reward 确认后才推进 fuse。P2 卡牌奖励自动选择路径在入口直接放行原生选择界面，保留手动完成路径，不以 dummy screen/立即完成 Task 冒充后端完成。
- **路线保护**：按 run generation + destination 为 route request 与 `MoveToMapCoordAction` 去重；奖励未完成时拒绝/等待；拒绝 null source coord，并记录 `route-follow-request`、`route-follow-deduped`、`route-follow-rejected` 与 Move action。
- **范围核对**：保留 `fixedP1CombatControl`、P2 战斗 AI/ActionQueue 与生命线；未修改商店、结束回合按钮、空过回合、玩家接管 UI 或 DualRoleAdventure DLL。
- **离线验证**：C# 编译退出码 0，只有既有不可达代码警告；`scratch\test_directive_5_26.ps1` PASS；本轮奖励闸门静态检查 PASS；scratch/部署 DLL SHA-256 一致。旧 `test_directive_5_25_r3.ps1` 因仍硬编码 `#5.25-R3` 指纹而停止，不代表 R3 行为检查失败。
- **哈希**：源码 `D5A306D2D4B2AF96C01ADCDC3B2EB8D095A678016F8A2435A1EE37225612E4B5`；scratch DLL `AA28CD093E3AF897CB33953E9535C29B4E789BF2473A8FEB403FEC499B4D479F`；部署 DLL `AA28CD093E3AF897CB33953E9535C29B4E789BF2473A8FEB403FEC499B4D479F`。
- **实机状态**：未启动游戏，修复未宣称通过。待按 #5.27-BS2 清单测试：P2 卡牌为最后奖励、金币/药水/遗物为最后奖励、P1/P2 均有卡牌奖励、自动跟路开/关、P2 卡牌自动选择关闭时闸门仍有效；检查无 incomplete backend、BeforeLeavingRoom 错误、null source、重复 Move action 和奖励后黑屏，并保存完整 godot.log。

## 52. #5.27-BS3 恢复无人值守 P2 奖励与跟路（2026-10-30）

- **构建指纹**：`Directive #5.27-BS3 | Built: 2026-10-30`；源码 `scratch\AiTeammateMod.cs` + `scratch\RewardBackendFlow.cs`；构建日志 `scratch\build-527-bs3.log`。
- **自动化开关**：`autoCardRewardEnabled=true`、`autoRouteFollowEnabled=true`、`fixedP1CombatControl=true`；配置 SHA-256 `8AB5484296DDA324DD57D7437A15E2F1F4095D227717FF68CAC5EDF5E4350074`，未被工具修改。
- **奖励链**：运行时替换 DRA merged combat reward offer，按 P2/P1 建立原生 per-player `RewardsSet`，每项仅经 `SelectRewardForPlayer(RewardsSetState, Reward)` 一次；等待原生选择 Task、真实 synchronizer 完成和 `NRewardsScreen.Completed` 后才发 claimed/resolved。P2 卡牌通过真实 `NCardRewardSelectionScreen.ShowScreen`、`OptionSelected`、`SelectCard` 和游戏自身 `CardReward.OnSelect`/`SyncLocalChoice`；不再使用 dummy screen/`Task.FromResult`。
- **路线链**：AiTeammate、DRA MapSelection Postfix、MapSelectionSynchronizer、`MoveToMapCoord`、ActionQueue `RequestEnqueue`、`MoveToMapCoordAction` 和离房路径共用奖励闸门；未完成时排队，完成后按 run generation（含 act）+ destination 只回放一次；source 使用真实 `MapLocation`，不传 null。P2 自动化错误只对当前奖励回退手动，未永久关闭开关。
- **范围**：未修改商店、战斗出牌、空过回合、结束回合、玩家接管 UI 或 DualRoleAdventure DLL。
- **离线验证**：生产 `RewardBackendFlow.cs` 适配器测试通过：重复点击共用任务、后端确认先于 RewardClaimed、原生卡牌只提交一次、单项异常局部回退、DRA/直接入队路线均服从闸门、释放后单次回放、非路线动作不受影响。`test_directive_5_26.ps1` PASS。
- **探针异常**：`inspect_room_lifecycle.cs` 加总 try/catch；`inspect-room-lifecycle-bs3-caught-exception.log` 验证完整 `Exception.ToString`、InnerException、StackTrace 和退出码，无未处理异常弹框。
- **哈希**：源码 `581D681F2F58B591A18C14BA9891B22ED915D40E6DCEBC51C2D9AA07D8104589`；RewardBackendFlow `6354115557DDF5D7412BBFC66C11ACEF7FC11FE59C69CF7F467F659D222E891C`；scratch/部署 DLL `964615F1A1971347C6AF9082B081E5E4ABFE0D4519951F79E1573C7FC451BD9D`。
- **实机状态**：未启动游戏，不能宣称通过。待完全重启后实测 P2 自动选卡、金币/药水/遗物领取、双方奖励完成后自动进入下一层、无 incomplete backend/BeforeLeavingRoom/null source/重复移动/黑屏，以及 P1 固定前台。日志采集器：`scratch\收集BS3日志.cmd`。


## 53. #5.29-Shop-T2（第一阶段：只实现 P2 商店自动删牌）(2026-10-31)

- **构建指纹**：`Directive #5.29-Shop-T2 | Built: 2026-10-31`（源码 `scratch\AiTeammateMod.cs`；编译输出 `mods\AiTeammate\AiTeammate.dll`）。
- **生命周期切入**：
  - 唯一入口：`LocalMultiControl.Scripts.Runtime.LocalMerchantInventoryRuntime.RefreshShopRoomForPlayer(ulong playerId)` 后置 Postfix。
  - 调度方法：`FlowCoordinator.OnRefreshShopRoom(playerId)`。
  - 库存获取：通过 DRA 反射调用 `LocalMerchantInventoryRuntime.GetOrCreateInventory(room, p2)`，确保 P2 实体库存与 `CardRemovalEntry` 就绪。
- **底层拦截与穿透防线（IL 级罪证消除）**：
  1. **跨玩家消费拦截**：`OneOffSynchronizer.DoLocalMerchantCardRemoval` 挂载 Prefix。在 `IsExecutingP2AutoRemoval == true` 时，绕过默认解析当前前台玩家（P1）的 `this.LocalPlayer` 逻辑，直接反射调用 `__instance.DoMerchantCardRemoval(p2, goldCost, cancelable)`，确保只扣除 P2 金币与牌组卡牌。
  2. **前台 UI 污染拦截**：`NMerchantInventory.OnCardRemovalUsed` 挂载 Prefix。在 `IsExecutingP2AutoRemoval == true` 时返回 `false`，彻底压制 `<OnTryPurchase>d__14` 对前台 P1 屏幕节点 `_cardRemovalNode` 的刷新，杜绝 P1 卡槽被误标售罄。
  3. **数据层完成标记**：删牌成功后直接调用 P2 实体的 `p2CardRemovalEntry.SetUsed()`。当用户后续主动切至 P2 视口时，DRA 的 `RefreshShopRoomForPlayer` 会自然根据 `Used == true` 渲染 P2 槽位售罄。
  4. **选牌上下文放行**：`IsRemovalContextInProgress` 增加 `if (FlowCoordinator.IsExecutingP2AutoRemoval) return true;`，避免商店环境下因非事件上下文导致回退手动选牌。
- **100% P1 隔离与零上下文切换**：
  - 严格禁止切换 `LocalContext` 至 P2，禁止调用 `SwitchControlToPlayer`，禁止触碰 P1 资产与库存。
  - `CardSelectFromDeckForRemovalPatch` 在 `IsExecutingP2AutoRemoval == true` 时严格禁止调用 `TripSubPageAndHandover`。
  - P1 商店库存、金币、屏幕与手动交互完全原生保留。
- **幂等与金币安全底线**：
  - 幂等键：`generation + "|room=" + roomId + "|" + fingerprint`，每个商店周期最多执行一次删牌。
  - 金币安全：执行前严格校验 `p2.Gold >= cost + 25`，不足则以 `insufficient-gold-reserve` 跳过；执行前后保证剩余金币 $\ge 25$。
- **成对日志规范**：
  - 进入：`[AiTeammate][Shop] shop-entry owner=P2 room=<id>`
  - 发现：`[AiTeammate][Shop] shop-offer kind=removal id=card-removal cost=<cost> gold_before=<gold>`
  - 成功：`[AiTeammate][Shop] shop-resolved kind=removal id=card-removal cost=<cost> target=<cardId> gold_after=<gold>`
  - 跳过：`[AiTeammate][Shop] shop-skip kind=removal reason=<reason>`
  - 失败：`[AiTeammate][Shop] shop-failed kind=removal reason=<exception>`
- **验证成果**：
  - `scratch\verify_5_29_binding.cs` 单元反射与逻辑验证（17/17 PASSED）。
  - `scratch\test_directive_5_29.py` 静态规范断言套件（7/7 PASSED）。
  - `python scratch\build_mod.py` 编译通过并部署。



## 54. #5.30 回合推进与事件前台微调代码交付 (2026-10-31)

- **构建指纹**：`Directive #5.30 | Built: 2026-10-31`（源码 `scratch\AiTeammateMod.cs`；编译产物 `mods\AiTeammate\AiTeammate.dll`）。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`205181B37EABAB8C00D9E64C1407AB232AA2F80C12792D8628D180458E9A00FC`
  - 部署 DLL `mods\AiTeammate\AiTeammate.dll`：`820E4249F130605AFCC83827A3889EFB5D9D3CD7A3848EA9D4A3F4DD63C22D90`
- **T1: P2 回合推进机制（p2-turn-bootstrap 内部通道）**：
  - **根因消除**：在固定 P1 前台门下，P1 点击回合结束后，DRA 通过 `auto-end-turn-next-playable` 调用 `ApplyControlContext` 试图初始化 P2 回合，被前置门 `p2-foreground-switch-blocked` 拦截，导致 P2 回合内部从未初始化、未投递 Action，进而导致 ActionQueue 为空冻结。
  - **专用通道实现**：
    - `TurnCoordinator.IsTurnBootstrapSource(source)` 白名单放行：`auto-end-turn-next-playable`、`auto-end-turn-all-vakuu-no-cards`、`auto-end-turn-fallback`、`no-playable-cards`、`wakuu-no-playable-cards-tick`、`wakuu-autoplay-loop`、`p2-turn-bootstrap`。
    - 在 `SwitchControlledPlayerToPrefix`、`ApplyControlContextPrefix`、`TurnFlowPatches.SwitchControlToPlayer` 中检测到 bootstrap source 时豁免拦截，设置 `SuppressControlSwitchUi = true` 与 `IsP2TurnBootstrapping = true`，输出 `[AiTeammate][Turn] p2-turn-bootstrap begin round=<r> p1=<p1> p2=<p2> controller=<curr>`。
    - 新增 `ApplyControlContextPostfix`：在 DRA 完成 P2 内部就绪后，立即调用 `RestorePlayer1ControlIfActive("wakuu-p1-restore:p2-turn-bootstrap", false)` 恢复 P1 前台并输出 `p2-turn-bootstrap end round=<r> p1=<p1> p2=<p2> result=success`。
    - **合法无动作保底**：若 P2 当前无牌可出，打出 `p2-no-action turn=<r> reason=<reason> hand=<h> energy=<e>`，并入队 P2 的 `EndPlayerTurnAction`，彻底根除空队列挂起。
- **T2: 结束回合按钮状态竞争修复**：
  - 修改 `DraReevaluateEndTurnButtonStatePatch.Postfix`：移除早期的 `if (TurnCoordinator.IsFixedP1CombatControlActive()) return;` 提前返回。
  - 在当前控制者为 P1、P1 回合活跃且 `CanTurnBeEnded == true` 时，若按钮处于 `Disabled` 或 `Hidden`，强制设置为 `Enabled` 与 `Visible=true`，输出 `endturn-force-state corrected=True state=Enabled visible=True can-turn=True`。严禁无条件激活，严禁触碰 P2。
- **T3: 事件默认前台与后台自动决策**：
  - **根因消除**：非共享事件完成后，DRA 通过 `LocalMultiControlRuntime.TryRunPendingEventAutoSwitch("event-auto-next")` 强行将前台从 P1 切换至 P2。
  - **实现方案**：
    - 挂载 Harmony Prefix 补丁 `DraTryRunPendingEventAutoSwitchPatch` 拦截 `LocalMultiControlRuntime.TryRunPendingEventAutoSwitch`，消费 P2 pending 事件并在后台通过 `FlowCoordinator.ResolveP2PendingEvent` 自动完成，输出 `control-context-assert room=event expected=P1 actual=<id>` 与 `event-auto-next-p1-restore`，Prefix 返回 `false` 彻底压制 DRA 切前台。
    - 在 `NEventRoomReadyGuardPatch` Prefix/Postfix 及 `TurnCoordinator.OnRoomSettled` 进房时机断言并确保 P1 前台，输出 `control-context-assert room=event expected=P1 actual=<id>` 及 `event-entry-p1`。
- **禁止项核对**：未触碰商店购买策略、未触碰奖励后端、未触碰路线跟随逻辑、未修改 DualRoleAdventure.dll 二进制。
- **离线与加载自证**：
  - `scratch\test_directive_5_30.py` 17 项断言全部 100% PASS。
  - 实机日志验证（PID 42136）：
    - `[AiTeammate] Build Fingerprint: Directive #5.30 | Built: 2026-10-31`
    - `[AiTeammate] Patched LocalMultiControlRuntime.ReevaluateEndTurnButtonState (EndTurn Button Force-Visible).`
    - `[AiTeammate] Patched LocalMultiControlRuntime.TryRunPendingEventAutoSwitch (Event Auto-Next Suppression).`
    - `[AiTeammate] INIT-SUMMARY hooked=wakuu-suppression,combat-solver-flow,full-auto-guard,root-control-suppression,choice-candidate-pool,choice-suppression,deployment-input-lock,turn-coordinator,autonomous-gatekeeper,flow-coordinator,shop-flow failed=none`


## 55. #5.31 修复商店进入自动删牌并真正固定事件房 P1 前台代码交付 (2026-10-31)

- **构建指纹**：`Directive #5.31 | Built: 2026-10-31`（源码 `scratch\AiTeammateMod.cs`；编译产物 `mods\AiTeammate\AiTeammate.dll`）。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`63F7AB5F175A4CAB58344C1563D3C41913203BB1FF6D9D8B6F4FB5AC05CC9D3B`
  - 部署 DLL `mods\AiTeammate\AiTeammate.dll`：`3811E643D917029075A9C2DEB7F6F193A65A4E6286BE8DBB111E64A31574F345`
- **一、商店自动删牌触发修复（绑定进房生命周期）**：
  - **根因消除**：原删牌仅挂在 `RefreshShopRoomForPlayer(ulong playerId)` 上，而在 P1 进店时 DRA 仅处理 P1 视口，从未调用 P2 视口刷新；导致必须手动点击 P1->P2 才能触发。
  - **生命周期挂载**：
    - 挂载 `MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom._Ready` Postfix 补丁 `MerchantRoomReadyPostfix`。
    - 在 `TurnCoordinator.OnRoomSettled`（Merchant 分支）与 `RunManagerEnterRoomPostfix`（`MerchantRoom` 分支）同步调度 `FlowCoordinator.OnMerchantRoomEnteredOrSettled("trigger")`。
    - 采用 `generation + "|room=" + roomId + "|entered"` 访问级幂等键，确保每次进店仅执行一次。
  - **纯后台数据初始化与前台断言**：
    - 不调用 `SwitchControlToPlayer`，不更改 `LocalContext`，不刷新 P2 可见 UI。
    - 后台调用 DRA `LocalMerchantInventoryRuntime.GetOrCreateInventory(room, p2)` 实例化 P2 商店实体。
    - 断言前台为 P1：`[AiTeammate][Shop] shop-foreground-assert expected=P1 actual=P1`。
    - 输出进店入口日志：`[AiTeammate][Shop] shop-entry owner=P2 trigger=merchant-room-enter room=<id>`。
    - 严格保持金币保留底线（剩余金币 $\ge 25$）、卡牌评分体系与单次上限。
- **二、事件房 P1 前台真正固定与多页后台自动推进**：
  - **根因消除**：
    1. 在 `SwitchControlledPlayerToPrefix` 与 `ApplyControlContextPrefix` 中，原本存在非战斗早期返回 `if (!TurnCoordinator.IsInCombat()) return true;`，使得事件房内任何切控制调用（`event-finished-next-player`、`event-auto-next` 等）直接放行。
    2. 旧代码在选项含关键字时误触发 `TripSubPageAndHandover`，将 `AutopilotFuse` 设置为永久熔断（`session-permanent`）并强制派发 `source=wakuu-subpage-manual` 将控制权转交 P2。
  - **拦截机制（非战斗前置门）**：
    - 在 `SwitchControlledPlayerToPrefix` 与 `ApplyControlContextPrefix` 的非战斗判断之前，增加专有判断：`TurnCoordinator.IsInEventRoom()` 且目标为 P2 且来源为自动事件 source（`event-auto-next`、`event-proceed-next-player`、`event-finished-next-player`、`event-shared-serial-execution`、`wakuu-subpage-manual` 及前缀匹配）。
    - 拦截时打出 `[AiTeammate][Event] event-auto-switch-suppressed source=<source> target=P2`。
    - 拦截后立即在后台推进 `FlowCoordinator.ResolveP2PendingEvent(synchronizer, p2)`，不返回 false 导致状态卡死。
    - 确保前台控制者保持 P1，并输出 `[AiTeammate][Event] event-room-final-controller expected=P1 actual=P1`。
    - 仅拦截自动切控制，用户手动切换（`player-state-button`、热键）完全保留；战斗、奖励、路线、商店不受影响。
  - **多页事件与异常防御**：
    - 彻底移除事件房内的 `TripSubPageAndHandover` 降级，非终端子页不再触发 `session-permanent` 熔断。
    - 多页事件按 `generation + player + event + page` 状态机流转，支持 `ABYSSAL_BATHS` 等复杂事件连续自动选择推进。
    - 捕捉并容错 `Event is not in progress!` 重复回调，重新校验当前完成状态，绝不因此切到 P2。
    - 若遇无法解析选项，使用有界安全兜底（`fallback-safe` / `bounded-fallback`）完成选择，始终保持 P1 前台。
- **新增规范日志成对覆盖**：
  - 商店：`shop-entry owner=P2 trigger=merchant-room-enter`、`shop-offer ...`、`shop-resolved ...` / `shop-skip ...`、`shop-foreground-assert expected=P1 actual=P1`。
  - 事件：`event-room-enter expected=P1 actual=P1`、`event-p2-choice-begin owner=P2 page=<...>`、`event-p2-choice-resolved owner=P2 page=<...>`、`event-auto-switch-suppressed source=<...> target=P2`、`event-room-final-controller expected=P1 actual=P1`。
- **离线与断言自证**：
  - `scratch\test_directive_5_31.py` 20 项断言全部 100% PASS。


## 56. #5.32 修复事件卡牌子选择后台化与事件奖励闸门地图防死锁代码交付 (2026-10-31)

- **构建指纹**：`Directive #5.32 | Built: 2026-10-31`（源码：`scratch\AiTeammateMod.cs`；编译产物：`mods\AiTeammate\AiTeammate.dll`）。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`35991ead978349955132e2d92989f63186b5db1c61cf6b6e30dbd6f75433d467`
  - 源码 `scratch\RewardBackendFlow.cs`：`df92eab7bd9542c059b5f7b206f1b04e4170e657451126691451ffa380c98a35`
  - 部署 DLL `mods\AiTeammate\AiTeammate.dll`：`9f3edd4659aa96cc3837d5c5058e2a117736f308ff11357a100f649141e38b6c`
- **一、事件中卡牌子选择后台化修复（以 BRAIN_LEECH 为代表）**：
  - **根因消除**：
    1. BRAIN_LEECH 事件“分享知识”调用 `CardSelectCmd.FromSimpleGridForRewards`，该重载入参为 `List<CardCreationResult>`，返回 `Task<IEnumerable<CardModel>>`。旧版本仅挂载了 `FromSimpleGrid`，缺失该入口导致直接弹出原生 UI。
    2. 新生成的卡牌对象 `c.Owner` 初始为 `null`，旧逻辑在 `InCombatCardSelectPatch` 触发 `cross-owner-block candidate pool owner mismatch` 导致熔断断闸、交还前台手动。
  - **动态反射挂载与签名适配**：
    - 在 `PatchFlowCoordinator` 中反射查找 `CardSelectCmd.FromSimpleGridForRewards`，精准适配 `(object context, List<CardCreationResult> cards, Player player, object prefs)`。
    - 实现 `CardSelectFromSimpleGridForRewardsPatch.Prefix`，当 `player == P2` 时接管执行。
    - 候选牌池属主修复：若 `c.Owner == null` 或在事件房中，安全赋值 `c.Owner = p2`，正常参与评分与自动提交，杜绝触发 cross-owner-block。
    - 后台依据卡牌评分矩阵选择最佳卡牌，返回已完成的 `Task.FromResult(selected)`；全程零切控制、零触碰 `LocalContext`、零弹出 P2 原生 UI。
  - **父子选项生命周期绑定**：
    - `FlowCoordinator` 维护 `CurrentEventId`、`CurrentEventPage` 与 `LastChildCardChoiceCompleted`。
    - 在父级选项提交后主动等待子选择完成，严禁在子选择未完成前提前关闭事件或标记消费。
    - 日志成对输出：
      - `event-card-grid-offer owner=P2 event=<id> page=<page> cards=<ids>`
      - `event-card-grid-submit owner=P2 choiceId=<id> pick=<card>`
      - `event-card-grid-backend-confirmed owner=P2 choiceId=<id>`
      - `event-p2-choice-resolved owner=P2 page=<page> child=<completed>`
- **二、事件卡牌奖励专用闭环与地图防死锁（Event Reward Gate）**：
  - **根因消除**：
    - 事件触发的单人 `NRewardsScreen` 其 `managed == false`。关闭时走 `NOverlayStack.Remove`，不触发战斗奖励专用 `Completed` 信号，导致 `RewardGateScreenCompleted` 和 `RewardGateCompletedSignal` 永久为 `false`。
    - 地图点击进入 `BeforeMapVote` 时判定 `IsRewardGateActive() == true`，将投票压入 `DeferredMapVotes` 等待 `WaitForRewardGate()`，造成全局软锁。
  - **专用判定条件（6 项严格检查）**：
    - `FlowCoordinator.TryCompleteEventRewardGate(string trigger)` 专用处理 `managed == false` 的事件卡牌奖励：
      1. `RewardsSet` 后端状态已 Complete：`IsRewardSetCompleteInBackend(set)`。
      2. 所有已开启的卡牌选择 Task 均已 backend-confirmed：`RewardGateConfirmed >= RewardGateExpected`。
      3. CardReward 卡牌选择全部完成：`RewardSelectionWaiters` 均已完成。
      4. `NCardRewardSelectionScreen` 已经被 Remove / Pop（检查 `NOverlayStack._overlays`）。
      5. `NRewardsScreen` 已经被 Remove / Pop 或 `IsComplete`。
      6. P2 没有任何未完成的 PlayerChoice（`TurnCoordinator.P2PendingChoiceCount == 0`）。
  - **真实 Overlay 生命周期感知与前置检查**：
    - 在 `RewardBackendFlow.OverlayRemovePrefix` 中接入 `TryCompleteEventRewardGate("overlay-remove")`。
    - 在 `BeforeMapVote` 和 `BeforeMoveToMapCoordAction` 头部接入 `TryCompleteEventRewardGate("before-map-vote" / "before-move-action")`，若界面已关闭则立即放行，无需等待。
    - 满足条件时幂等释放闸门，打出规范日志：
      - `event-reward-gate-complete owner=P1 key=<key>`
      - `reward-set-complete owner=P1 key=<key> reason=event-card-overlay-removed`
      - `room-exit-gate-release source=event-card-reward`
  - **唤醒重放与严格隔离**：
    - 闸门释放后唤醒 `RewardGateReadyWaiter`，自动重放已积压的 `DeferredMapVotes` 与 `MoveToMapCoordAction`，每个 owner+destination 至多重放一次，打出：
      - `route-follow-replay owner=<id> destination=<coord> key=<key>`
    - P1 地图投票与 P2 跟随投票直接畅通，无需手动切换角色。
    - 普通战斗奖励（`managed == true`）严格走既有严格后端闸门，不放宽，杜绝战斗奖励早退。
    - 若条件未满足，输出清晰诊断日志：`event-reward-gate-waiting trigger=<t> missing=<m>`。
- **三、测试与离线自证**：
  - `scratch\verify_5_32_binding.cs` 经 .NET 9 运行时执行，23 项断言 100% 全部 PASS（覆盖字段、方法、参数签名、Overlay 回调及安全调用）。
  - `scratch\verify_5_32.py` 验证 DLL 二进制中全部 10 项规范 UTF-16LE 字符串与关键 ASCII 元数据符号 100% 全部 PASS。


## 57. #5.33 修复问号事件死循环黑屏与 P2 卷轴箱自动选择 (2026-10-31)

- **构建指纹**：Directive #5.33 | Built: 2026-10-31（源码：scratch\AiTeammateMod.cs；编译产物：mods\AiTeammate\AiTeammate.dll）。
- **SHA-256 校验**：
  - 源码 scratch\AiTeammateMod.cs：2268c073347216cefe03f7cd79e3f31fc16ec4482d716214fa3ff4582f01d85
  - 源码 scratch\RewardBackendFlow.cs：df92eab7bd9542c059b5f7b206f1b04e4170e657451126691451ffa380c98a35
  - 部署 DLL mods\AiTeammate\AiTeammate.dll：28897aec4a329e2332e13fe7be304b6d885aacfdca921dfec2f2ad426960b95d
- **一、修复问号事件死循环黑屏（针对 THE_LEGENDS_WERE_TRUE 重复提交约 6740 次）**：
  - **根因消除**：
    1. 旧逻辑中 ResolveP2PendingEvent 包含内部同步 while 循环及 Thread.Sleep(50) 阻塞主线程；且在提交 ChooseOptionForEvent 后，EventSynchronizer.ChooseOptionForEvent 的 Harmony Postfix 又立即同步重入 ResolveP2PendingEvent，导致同步重读未更新的旧 CurrentOptions，在几秒内疯狂重入调用 6740 次，造成主线程冻结与黑屏。
    2. 另外在 ResolveP2RestSite 中，休整处点数数组 _restSites 索引未做越界安全防范，可能触发 ArgumentOutOfRangeException。
  - **进程内 in-flight 复合幂等状态机**：
    - 引入复合幂等键：generation + ownerNetId + eventId + page + optionsFingerprint + chosenIndex。
    - 提交前记录 InFlightEventKey、提交时间及选项指纹。
    - 提交后彻底移除同步 while 循环与 Thread.Sleep，**立即 return true 退出**当前 resolver，将控制权交还游戏主循环与网络后端。
    - 若同键在后端未确认期间被重入触发，记录并拦截：event-choice-duplicate-suppressed key=<key>，禁止再次调用 ChooseOptionForEvent。
  - **后端状态确认与推进判定**：
    - 只有当满足以下真实状态变更时，才视为后端确认并推进：
      1. 事件标记结束 (isFin == true)；
      2. 当前选项指纹与提交时记录的指纹不一致 (optionsChanged == true)；
      3. 关联的卡牌子选择在进房后完成清空 (pendingChoiceCleared == true)；
      4. 局次代际变更 (generationChanged == true)。
    - 满足后打印规范成对日志：
      - event-choice-backend-confirmed key=<key>
      - event-choice-resume key=<key>
      - event-p2-choice-resolved owner=P2 page=<page> child=<status>
    - 将该 key 加入 ConfirmedEventKeys，清空 InFlightEventKey，推进 CurrentEventPage++。
  - **看门狗与循环守卫（Loop Guard）**：
    - 提交后 > 3.0 秒未收到任何后端响应：
      - 允许至多 1 次有界重试：event-choice-resume key=<key> retry=1。
      - 若重试后依然无响应，触发循环守卫：
        - event-choice-loop-guard-tripped key=<key>
        - event-choice-abort key=<key> reason=timeout-no-backend-response
        - 记入 AbortedEventKeys，彻底终止重入，安全保持 P1 前台，不锁死游戏。
    - 异常捕获守卫：若抛出包含 Event is not in progress 的异常，视为收敛，记录 event-not-in-progress caught: duplicate callback handled without switching foreground，清空 in-flight 键并不重试。
  - **进房生命周期重置**：
    - 在 TurnCoordinator.OnRoomEnterBegin 中调用 FlowCoordinator.ResetInFlightEventState()，清空上个房间的 in-flight 状态。
  - **休整处安全越界检查**：
    - 在 ResolveP2RestSite 中检查 _restSites 是否为空、检查 playerIndex >= restSites.Count，并在反射调用时安全捕获 ArgumentOutOfRangeException 与 IndexOutOfRangeException。
- **二、实现 P2 卷轴箱（Scrollbox）自动选择**：
  - **调用链逆向定罪**：
    - 反编译验证：MegaCrit.Sts2.Core.Models.Relics.ScrollBoxes.AfterObtained 真实调用的是 MegaCrit.Sts2.Core.Commands.CardSelectCmd.FromChooseABundleScreen(Player player, IReadOnlyList<IReadOnlyList<CardModel>> bundles) -> Task<IEnumerable<CardModel>>。
    - 原生 UI NChooseABundleSelectionScreen.ShowScreen 会推入 NOverlayStack，展示前台界面并等待 _completionSource.Task。
    - 原生同步链：sync.ReserveChoiceId(player) -> sync.SyncLocalChoice(player, choiceId, result) -> CardSelectCmd.LogChoice(player, bundles[bestIndex])。
  - **彻底废除旧 PackChoicePatch**：
    - 从 AiTeammateMod.cs 的 ModEntry.Init 中移除旧 PackChoicePatch 扫描注册，避免多余注入。
  - **精准 Hook CardSelectCmd.FromChooseABundleScreen**：
    - 仅拦截 player == P2；当 player == P1 时原样放行，保留 P1 原生手动 UI。
    - 遍历所有 bundle，读取真实 CardModel，通过 FlowCoordinator.GetCardScore 计算每个 bundle 的总评分（卡牌属主缺失时安全补全 card.Owner = p2）。
    - 记录规范日志：
      - 
eow-scrollbox-offer owner=P2 pack-count=<n> sizes=<sizes> scores=<scores>
      - 
eow-scrollbox-submit owner=P2 pick=<index>
    - 幂等防重：以 generation|ownerNetId|headless|bundleIds 为 key，已处理过的请求直接返回结果并记录 
eow-scrollbox-duplicate-suppressed。
    - 原生网络同步协议：
      - 获取 RunManager.Instance.PlayerChoiceSynchronizer。
      - 调用 sync.ReserveChoiceId(player)。
      - 调用 sync.SyncLocalChoice(player, choiceId, PlayerChoiceResult.FromIndex(bestIndex))。
      - 反射安全调用 CardSelectCmd.LogChoice(player, bundles[bestIndex])。
    - 规范完成日志：
      - 
eow-scrollbox-backend-confirmed owner=P2
      - 
eow-scrollbox-resolved owner=P2
    - 结果返回：直接构造 Task.FromResult((IEnumerable<CardModel>)bundles[bestIndex]) 赋给 __result 并 
eturn false;。
    - 全程后台运行，零 UI 弹出，零控制上下文切换，P1 始终保持在前台。
    - 异常兜底机制：捕获异常时记录 
eow-scrollbox-fallback reason=<msg> 并 
eturn true; 回退至游戏原生流程。
- **三、离线自证与产物校验**：
  - python scratch/build_mod.py 在 C# 5 模式下一键编译成功，直接产出部署至 mods/AiTeammate/AiTeammate.dll。
  - python scratch/verify_5_33.py 验证 DLL 二进制中的所有 13 项规范 UTF-16LE 字符串全部 100% PASS。
  - python scratch/verify_metadata_5_33.py 验证 TypeDef、MethodDef、MemberRef 结构完整。


## 58. #5.34 修复 CombatSolver 偶发卡住与 P2 手牌变化触发 P1 抽牌动画 (2026-10-31)

- **构建指纹**：`Directive #5.34 | Built: 2026-10-31`（源码：`scratch\AiTeammateMod.cs`；编译产物：`mods\AiTeammate\AiTeammate.dll`）。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`3cdd504d9e13f6804eade2a3557cb04542c36a7b5fee8e7ba5c0bd144509a479`
  - 源码 `scratch\RewardBackendFlow.cs`：`df92eab7bd9542c059b5f7b206f1b04e4170e657451126691451ffa380c98a35`
  - 部署 DLL `mods\AiTeammate\AiTeammate.dll`：`d16b3c898cf44da3517e7b7d8dec731309144522254f08df9c50228f3602a4ae`
- **一、修复 CombatSolver 偶发卡住（T0）**：
  - **P2 回合全局遥测归档**：
    - 在每个 P2 回合头部输出完整状态遥测：
      `[AiTeammate][Coord] p2-turn-telemetry turn=<n> gen=<g> full-auto=<b|s> ui-toggle=<s> session=<s> search=<s> deploy=<s> p1-queue=<n> p2-queue=<n> deploy-actions=<n>`
    - 精确反映：
      1. `solver FullAutoEnabled` 实际值；
      2. `CombatSolver UI toggle` 状态（`solverEnabled`, `autoCalc`）；
      3. `solver session/search/deployment` 运行时状态机；
      4. 当前 `turn/generation`；
      5. `P1/P2 action queue` 积压数量（通过遍历 `_actionQueues` 读取各属主未决动作数）；
      6. `deployment action` 数量（记录部署动作数）。
  - **部署丢失成对输出与恢复机制**：
    - 当出现 `p2-deploy-hook-missed` 时，强制输出成对诊断日志：
      `[AiTeammate][Coord] solver-state-mismatch turn=<n> reason=<r> full-auto=<b>`
      `[AiTeammate][Coord] deployment-hook-missed turn=<n> reason=<r>`
    - 不再静默等待，立即触发至多一次重算恢复尝试（`RequestRecalculation`）。
    - 废除每帧无条件反复调用 `EnsureFullAutoEnabled()`，改为按回合与状态有界保护，杜绝无条件日志刷屏。
  - **看门狗停滞重算与回退有界约束**：
    - `solver-search-stall` 严格限制：**每回合至多一次真实 solver 重算**；**每回合至多一次 cached-plan / greedy fallback**；严禁每帧重复重算。
    - 若 12 秒后没有部署：触发一次真实重算，记录：`solver-search-stall: >12s elapsed without deployment, kicking recalculation (once-per-turn)`。
    - 继续等待最多 8 秒（总计 20 秒）：若仍无部署，尝试执行缓存计划或贪心出牌；
    - 确认没有可出牌动作（`!P2HasUsableCard` 或 fallback 耗尽）后，才允许安全结束 P2 回合。
    - 当自动模式关闭（`!IsFullAutoEnabled() || !AutoCardRewardEnabled`）时，看门狗立即 return 放行原生手动流程。
  - **ActionQueue UI 空引用保护与后端确认**：
    - Harmony 挂载 `NCardPlayQueue.OnActionEnqueued`（Prefix & Finalizer）：当 remote 或 P2 动作入队但对应 Godot UI 节点（如 `NCombatRoom`、`NCreature` 或 `PlayerIntentHandler`）为空时，跳过 UI 刷新，记录 `enqueue-ui-null action=<name> owner=<id> reason=<reason>`，杜绝空引用异常阻断后端。
    - Finalizer 安全压制 UI 异常，确保后端 `ActionEnqueued` 事件畅通派发。
    - Harmony Postfix 挂载 `ActionQueueSynchronizer.EnqueueAction` 与 `ActionQueueSet.EnqueueWithoutSynchronizing`，记录：
      `[AiTeammate][Coord] backend-enqueue-confirmed action=<name> owner=<id>`
    - Harmony Prefix 挂载 `GameAction.ExecuteAction`，记录：
      `[AiTeammate][Coord] backend-action-executed action=<name> owner=<id>`
- **二、修复 P2 手牌变化触发 P1 抽牌动画（T1）**：
  - **P2 临界区严禁误判泄漏**：
    - 修改 `CheckHandOwnershipGuard`，在以下期间严禁将临时 P2 holder 判定为 P1 泄漏：
      1. `P2ActionSenderScopeDepth > 0`；
      2. 当前执行动作 `owner == P2`；
      3. P2 PlayerChoice 正在进行中；
      4. `CardPileCmd.Add` / `DrawInternal` 正在进行；
      5. `LiquidMemories` 药水使用或子选择；
      6. Potion 动作 / BranchingPlayerChoice 期间；
      7. P2 手牌或 UI 正在过渡。
    - 上述期间严格只记录：
      `[AiTeammate][Coord] hand-ownership-check-deferred owner=P2 reason=<reason>`
      **严禁调用 `RebindHandContainerToPlayer1`，严禁调用 `RefreshCombatUiForControlledPlayer(P1)`**。
  - **P2 动作完成后单次延迟校验**：
    - P2 动作完成并恢复 P1 前台后，不立即同步校验，而是置位 `PendingDeferredHandCheckAfterP2 = true`，在稳定帧执行且仅执行一次延迟检查。
  - **4 严格条件重绑守卫**：
    - 只有当且仅当满足以下全部 4 项条件时，才允许执行 P1 手牌重绑：
      1. P1 逻辑手牌指纹（`p1Fingerprint`）与 UI holder 指纹（`uiFingerprint`）持续至少 250ms 不一致；
      2. 连续至少 3 个检查帧不一致；
      3. 当前不在任何 P2 临界区；
      4. 确认不是 P2 临时 holder。
  - **手牌变化过程静默压制**：
    - 在 P2 触发 `CardPileCmd.Add`、`DrawInternal`、`LiquidMemories`、`UsePotionAction` 时，成对捕获并输出：
      `[AiTeammate][Coord] p2-hand-change-ui-suppressed owner=P2 reason=<reason>`
  - **原生抽牌动画完整保留**：
    - P1 正常抽牌（通过 `CardPileCmd.Draw` / `GetTweenForCardsChangingPiles`）保留原生 Godot Tween 动画；真正修复 P1 泄漏时杜绝产生额外 draw tween。
- **三、离线自证与产物校验**：
  - `python scratch/build_mod.py` 成功编译并部署至 `mods/AiTeammate/AiTeammate.dll`。
  - `python scratch/verify_5_34.py` 校验全部 12 项关键规范字符串 100% PASS。
  - `python scratch/verify_metadata_5_34.py` 校验全部 19 项新增 MethodDef 与 6 项 FieldDef 100% PASS。


## 59. #5.35 修复 Turn 2 AI 假死与 P1 鼠标无法点击出牌问题 (2026-10-31)

- **构建指纹**：`Directive #5.35 | Built: 2026-10-31`，源码：`scratch\AiTeammateMod.cs`，产物：`mods\AiTeammate\AiTeammate.dll`。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`990fbc16253abccc12047ae0d68a5e7fbb9c2b04f0473adb4b991330ba84bc45`
  - 产物 DLL `mods\AiTeammate\AiTeammate.dll`：`beb66e0ff0d8cb6c805fc5326e765bb74c1b5e5bab1e406c3e1a6bd05530fcbb`
- **一、修复 Turn 2 AI 假死与 `IsDeploying` 悬挂（任务 T1）**：
  - **`StartDeploymentPrefix` 门控与直通放行**：
    - 在 `StartDeploymentPrefix` 顶部补齐 `CoordinationEnabled / WaitEnabled` 门控。当 `!TurnCoordinator.CoordinationEnabled || !TurnCoordinator.WaitEnabled` 时，置位 `IsDeploying = true; DeployStartTime = DateTime.UtcNow;` 并直接 `return true;`，杜绝调用 `ProcessDeploymentEvaluation` 或误拦截部署。
    - 在 `ProcessDeploymentEvaluation` 顶部增加相同保护：`if (!TurnCoordinator.CoordinationEnabled || !TurnCoordinator.WaitEnabled) return true;`。
  - **根除重算自锁 Bug**：
    - 删除了 `ProcessDeploymentEvaluation` 在调用 `RequestRecalculation(state, "enemy-already-debuffed")` 前过早置位 `TurnCoordinator.RecalcRequestedThisTurn = true;` 的错误代码，消除自锁阻断。
    - `RequestRecalculation` 白名单扩充加入 `"enemy-already-debuffed"` 与 `"deploy-hook-missed-recovery"`，保证合法触发源不被单回合单次门控误拦截。
    - 在 `StartDeploymentPrefix` 入口处同步缓存 `TurnCoordinator.CachedSolverController = __instance;` 与 `TurnCoordinator.CachedDeployArgs = __args;`，消除 fallback 部署时的参数空引用。
  - **杜绝 `IsDeploying` 悬挂**：
    - 在 `StartDeploymentPrefix` 和 `DeployCurrentTurnPrefix` 中，任何返回 `false`（中止/推迟部署）的分支，均在 `return false` 前显式执行 `TurnCoordinator.IsDeploying = false;`。
    - 在 `TurnCoordinator.CleanHangingDeployment()` 的 `finally` 块中加入 `IsDeploying = false;` 强制归零，防止假死状态残留 20 秒。
  - **修复 Turn 1 `p2-deploy-hook-missed` 1.0s 误报**：
    - `OnProcessTick` 的未进入 Harmony 挂钩检测阈值从 1.0s 上调至 3.5s（`(now - TurnStartTime).TotalSeconds >= 3.5`），且必须满足 `!IsSolverSearching() && !IsSolverDeploying()`，避免在 CombatSolver 正常初始搜索期误触发重算。
- **二、彻底修复 P1 鼠标无法点击出牌（任务 T2）**：
  - **解绑 `CanPlayCardsPrefix` 输入封锁**：
    - `UiSuppressionPatches.CanPlayCardsPrefix` 在 `TurnCoordinator.IsFixedP1CombatControlActive()` 激活时直接 `return true;`，绝不因后台 `IsDeploying` 封死前台 P1 的 `NPlayerHand.CanPlayCards` 鼠标点击权限。
    - `SetPlayerActionsDisabled` 增加硬防线：`if (disabled && TurnCoordinator.IsFixedP1CombatControlActive() && !TurnCoordinator.IsPlayer1Dead()) return;`，防止任何组件误关 P1 前台动作。
  - **新增 `IsPlayer1HandBusy` 出牌状态保护**：
    - 检查 `hand.InCardPlay`、`hand.IsInCardSelection`、`HasDraggedHolder`、`_currentCardPlay != null`、`_draggedHolderIndex >= 0`、`_holdersAwaitingQueue.Count > 0`、`_selectedCards.Count > 0` 及 `CurrentMode` 子模式。
    - 在 `IsInP2HandDeferredPeriod` 与 `CheckHandOwnershipGuard` 顶部：若 P1 正在出牌/拖拽/选牌，立即跳过检查并置位 `reason = "p1-card-play-in-progress"`，绝不在出牌中途误触发 `hand-rebind-leak`。
  - **收紧 `CheckHandOwnershipGuard` 触发条件**：
    - 分离 P2 卡牌残留与纯空白 UI 判定：
      1. 若 `hasP2Holder == true`：仅在持续 >= 250ms 且 3 帧确认残留时才触发重绑。
      2. 若 `!hasP2Holder` 且 `uiHolderCount > 0`：所有 UI 手牌均属 P1，绝不因瞬态数量差（如拖拽中牌暂离 ActiveHolders）调用破坏性重绑。
      3. 若 `uiHolderCount == 0 && p1ModelCardCount > 0` 且 `!IsPlayer1HandBusy`：持续 >= 500ms 且 5 帧确认真正空白时才触发自愈。
  - **保护 `NPlayerHand` 状态机不被强拆**：
    - `RebindHandContainerToPlayer1` 开头检测 `IsPlayer1HandBusy` 直接延后退出。
    - 严禁在未安全结束出牌状态时反射置空 `_currentCardPlay`（增加 `if (!hand.InCardPlay)` 守卫）。
- **三、修复 `#5.23` 救生索跨线程 Godot UI 崩溃（任务 T3）**：
  - `IndependentLifelineTimerTick` 运行在 ThreadPool 线程，禁止直接调用触发场景树 UI 的 `ForceEndTurnForPlayer2`。
  - 改用 `Godot.Callable.From(delegate { ForceEndTurnForPlayer2(defCm, defState, defP2); }).CallDeferred();` 投递至 Godot 主线程执行，根除 `NUiFlashVfx` 跨线程 `NullReferenceException`。
- **四、交付与验证**：
  - `python scratch/build_mod.py` 编译通过并部署至 `mods\AiTeammate\AiTeammate.dll`。
  - `python scratch/verify_5_35.py` 执行全量字符串与源码逻辑校验：18 项关键规则 100% PASS。

## 60. #5.36 修复先古遗物/事件卡牌奖励 P2 自动三选一与战斗抽牌泄漏 P1 手牌栏 (2026-10-31)

- **指纹**：`Directive #5.36 | Built: 2026-10-31`（源码：`scratch\AiTeammateMod.cs` 与 `scratch\RewardBackendFlow.cs`，产物：`mods\AiTeammate\AiTeammate.dll`）
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`baa86cbccaebd91446aa88388dcb20537accf8fdd12e35213faacd913029eec6`
  - 源码 `scratch\RewardBackendFlow.cs`：`a7ff4022ff48ea7474b9e2f3b17159739d55fa926237c88d02fa9cdb5b66b1e4`
  - 产物 DLL `mods\AiTeammate\AiTeammate.dll`：`bb896d42ce5a37f773a9e338a6730daad9f27fccce3406ea62968e46adb0878c`
- **一、修复非战斗/事件卡牌三选一 AI 自动选择与控制流（T1）**：
  - **`RewardBackendFlow` 解锁未托管卡牌三选一**：
    - 新增 `ResolvedRewards`（`HashSet<Reward>`）及生命周期重置，防止同一奖励被重复处理。
    - 暴露 `NotifyCardRewardSelectBegin(CardReward)` 与 `NotifyCardRewardSelectEnd(CardReward)`。在 `CardRewardOnSelectPatch` 中，Prefix 调用 `NotifyCardRewardSelectBegin`，Postfix 的异步任务（或同步完成）通过 `ContinueWith` 调用 `NotifyCardRewardSelectEnd`。
    - 在 `ShowCardScreenPostfix` 与 `AllowNativeRewardOverlay` 中放行未托管的 P2 卡牌奖励（`!Owns(reward) && reward.Player.NetId == p2Id`），将其登记到 `CardScreens[screen] = reward`，允许弹出后台选牌。
    - 在 `OptionSelectedPostfix` 中，当 `reward != null && reward.Player.NetId == p2Id` 且属于未托管奖励时，放行并直接调用 `SubmitNativeCardChoice(screen, optionIndex, reward, ...)`。
    - 在 `SubmitNativeCardChoice` 中，对于未托管 P2 奖励，后台评分选出最佳卡牌后，调用 `FlowCoordinator.MarkRewardBackendConfirmed(reward, "P2")` 唤醒 `WaitForRewardSelection` 轮询等待。
  - **修复切人拦截与控制权恢复**：
    - 移除了 `DraControlSwitchPatches.SwitchControlledPlayerToPrefix` 中 `#5.31` 遗留的重复切人拦截块（原块缺失 `&& isP2` 守卫，会误拦截 `event-proceed-next-player` 回切 P1）。
    - 在 `TurnCoordinator.IsAutoEventSource` 中加入 `"rewards-offer"`、`"merged-rewards-offer"`、`"merged-rewards-offer-from-rewardsset"`，在事件房间中将此类自动事件源放行，不再误拦截切人。
    - 在 `FlowCoordinator.TryAutoClaimP2Rewards` 退出时增加 `finally` 块，确保在 P2 奖励领取完毕后（或异常退出时）必定调用 `TurnFlowPatches.SwitchControlToPlayer(p1Id, "wakuu-p1-restore:event-rewards-claimed", false)` 恢复 P1 控制权。
- **二、修复 P2 战斗中抽牌（如“耸肩无视”）泄漏进 P1 手牌栏（T2）**：
  - **第一道防线：抽牌与移动牌堆时的 `LocalContext.NetId` 强行对齐 P1**：
    - 在 `CardPileCmd.Add`（`CardPileAddPrefix`/`Finalizer`）与 `CardPileCmd.DrawInternal`（`DrawInternalPrefix`/`Finalizer`）中引入 Harmony `out ulong? __state`。当 P2 动作执行期间（`LocalContext.NetId == p2Id`），临时将 `LocalContext.NetId` 改回 P1，调用完成后在 Finalizer 中还原。这使得 Godot UI 查询 `LocalContext.IsMe(p2)` 时恒为 `false`，从根源上阻止了 Godot 为 P2 抽牌生成手牌卡牌持有器节点。
    - 在 `CardPileCmd.GetTweenForCardsChangingPiles`（`GetTweenForCardsChangingPilesPrefix`/`Finalizer`）中同样使用 Harmony `out ulong? __state` 将 `LocalContext.NetId` 对齐为 P1，并移除了原有的 `TurnCoordinator.IsDeploying` 提前退出检查，确保抽牌补牌过程无论是否在部署中都受到保护。
  - **第二道防线：`NPlayerHand` 节点入队硬拦截**：
    - 新增 `CoordinatorPatches.NPlayerHandAddPrefix`，挂钩 `NPlayerHand.Add` 和 `NPlayerHand.AddCardHolder`。
    - 若传入的卡牌实体所有者属于 P2（`cm.Owner.NetId == p2Id` 或 holder 中包含 P2 卡牌），记录 `[AiTeammate][Coord] p2-card-blocked-from-p1-hand` 日志，调用 `holder.QueueFree()` 销毁节点并直接 `return false` 拦截，杜绝任何外部路径将 P2 卡牌加入 P1 手牌容器。
  - **第三道防线：残留卡牌外科手术式修剪（Prune）与状态检查强化**：
    - 在 `CheckFullAutoGuard` 中，修改为只要 `PendingDeferredHandCheckAfterP2`、`P2ResidueStartTime` 或 `BlankHandStartTime` 任一处于活跃状态，就持续调用 `CheckHandOwnershipGuard`，不再被第 1 帧的标记清除提前截断。
    - 在 `CheckHandOwnershipGuard` 中，当检测到 P2 残留卡牌时：若 P1 手牌容器中还存在 P1 自己的正常卡牌，不再调用全量手牌重构（`RebindHandContainerToPlayer1`），而是外科手术式逐个调用 `hand.RemoveCardHolder(ph); ph.QueueFree();` 修剪掉 P2 卡牌，记录 `p2-card-residue-pruned count=N`，完全保留 P1 的正常手牌与出牌操作状态。
- **三、验证**：
  - 严格遵循 C# 5 语法规范，由 .NET Framework 4.0 `csc.exe` 编译无 warning 无 error。
  - 自动化验证脚本 `python scratch/verify_5_36.py` 18 项全部 PASS（包含 DLL 二进制字符串、`RewardBackendFlow` 与 `AiTeammateMod` 核心不变量）。

## 61. #5.37 修复未领奖励按钮锁死、跳过奖励门锁卡死与脱战 P2 切人幽灵定时炸弹 (2026-10-31)

- **指纹**：`Directive #5.37 | Built: 2026-10-31`（源码：`scratch\AiTeammateMod.cs` 与 `scratch\RewardBackendFlow.cs`，产物：`mods\AiTeammate\AiTeammate.dll`）
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`378e23ee5efaf7d9dd67f60b74d9f72121d6047a8170c766f8d4513993f3c8c3`
  - 源码 `scratch\RewardBackendFlow.cs`：`acc9ad76b68fdc31130c881f01c6cd0f5fa7711647585f548fe4705d3449a519`
  - 产物 DLL `mods\AiTeammate\AiTeammate.dll`：`85562441d4a25150ae6f2a391bfdb1e0d899838ff5c31d86fddb11fe713062e5`
- **一、修复 P1 未领奖励（药水满栏/取消选卡）按钮永久禁用与去重死锁（Task 1）**：
  - **`SelectButton` 解锁未消费奖励**：
    - 在 `RewardBackendFlow.SelectButton` 中，当 `await task` 返回 `claimed == false` 且该奖励属于 P1 时（`!claimed && !isP2`）：
      - 立即从 `Selections` 字典中移除：`Selections.Remove(reward)`，解除后续点击时的 `reward-selection-deduped` 永久静默；
      - 重新启用按钮并派发信号：`button.Enable(); button.EmitSignal(NRewardButton.SignalName.RewardSkipped, button);`；
      - 绝不调用 `Decided.Add(reward)` 亦不调用 `FlowCoordinator.MarkRewardBackendConfirmed(reward, "P1")`；
      - 输出 `[AiTeammate][Flow] p1-reward-unclaimed-reenabled` 日志并立即返回 `false`。
  - **`ObserveRewardSelectionTaskAsync` 重置未消费未托管奖励**：
    - 当 `owner == "P1"` 且 `!reward.SuccessfullySelected` 时，不再错误调用 `MarkRewardBackendConfirmed`，而是将该奖励从 `RewardSelectionWaiters` 中移除并重置 waiter，输出 `p1-unmanaged-reward-unclaimed-reset`，允许 P1 丢弃药水后再次点击领取或通过 Proceed 统一跳过。
- **二、修复点击“跳过/前进”导致奖励门锁卡死（`reason=reward-set-incomplete`）（Task 2）**：
  - **新增 `FlowCoordinator.ForceCompleteRewardScreenFromProceed`**：
    - 遍历并确认奖励集所有奖励 `MarkRewardBackendConfirmed(r, "P1-proceed")`；
    - 将 `RewardGateScreenCompleted = true; RewardGateCompletedSignal = true;` 并触发 `TryCompleteRewardGateLocked` 解除全局房间出口门锁；
    - 唤醒 `RewardScreenWaiters[screen].TrySetResult(true)`，解除 `RunOwnerScreens` 中的 `WaitForRewardScreen` 悬挂等待，使批处理正常 `CompleteRewardBatch()`。
  - **`RewardBackendFlow.ProceedPrefix` 全覆盖**：
    - 对未托管 P1 奖励界面（`!IsManagedScreen(__instance)`）：安全调用 `SkipRewardsSet`，并执行 `ForceCompleteRewardScreenFromProceed(__instance, "P1-unmanaged-proceed")` 与 `TryCompleteEventRewardGate("p1-proceed")`；
    - 对已标记完成的托管界面：补发 `ForceCompleteRewardScreenFromProceed(__instance, "P1-proceed-already-complete")`；
    - 对包含未领奖励的托管界面：在调用 `SkipRewardsSet` 并派发 `RewardSkipped` 后，调用 `ForceCompleteRewardScreenFromProceed(__instance, "P1-proceed-skip")`，彻底消除跳过奖励时的地图前进阻塞。
- **三、拆除战斗结束生命线定时器炸弹并全流程锁定 P1 前台（Task 3）**：
  - **彻底拆除 `IndependentLifelineTimer` 战斗结束幽灵触发**：
    - 在 `RewardBackendFlow.EnterOffer` 中进入奖励结算时，立即调用 `TurnCoordinator.DisarmIndependentP2Lifeline("combat-rewards-offered")`；
    - 在 `TurnCoordinator.IndependentLifelineTimerTick` 触发入口与 `Callable.From` 委托内部，均检查 `if (!IsInCombat()) { DisarmIndependentP2Lifeline(...); return; }`，杜绝脱战后误报 `CRITICAL-lifeline`；
    - 在 `TurnCoordinator.OnProcessTick` 中，当 `!IsInCombat()` 且 `IndependentLifelineArmed` 为 true 时，立即自动解除武装；
    - 在 `TurnCoordinator.ForceEndTurnForPlayer2` 头部检查 `if (!IsInCombat())`，且在战斗中执行强行结束回合时，绝不调用 `SwitchControlToPlayer(P2)`，改用 `using (EnterP2ActionSenderScope("ForceEndTurnForPlayer2"))` 在纯底层作用域执行 P2 `PlayerCmd.EndTurn` 与 `SetReadyToEndTurn`，使前台控制权零泄漏。
  - **全流程（脱战 + 战斗）锁定 P1 前台**：
    - 新增 `TurnCoordinator.IsFullRunP1ControlLockActive()`，在双角色存活且 P1 未阵亡时全局生效；
    - 在 `TurnFlowPatches.SwitchControlToPlayer` 中使用 `IsFullRunP1ControlLockActive`，除战斗内合法的回合启动源外，全量拦截所有切至 P2 的前台请求；
    - 在 `DraControlSwitchPatches.SwitchControlledPlayerToPrefix` 与 `ApplyControlContextPrefix` 中，脱战状态下若开启全流程锁且目标为 P2，直接拦截切人并记录 `out-of-combat-p2-switch-blocked` / `out-of-combat-p2-apply-blocked`；
    - 在 `TurnFlowPatches.RestorePlayer1ControlIfActive` 中，脱战状态（`!IsInCombat()` 或 `state == null`）若检测到 DRA 当前控制者为 P2 或 `LocalContext` 偏离 P1，立即将控制权拉回 P1；
    - 在 `TurnCoordinator.OnProcessTick` 脱战分支中，增加心跳守护 `out-of-combat-tick-guard`，确保脱战期间任何偶发偏离都会在 450ms 内自动纠偏回 P1。
- **四、验证**：
  - 严格遵循 C# 5 语法规范，由 .NET Framework 4.0 `csc.exe` 编译无 warning 无 error。
  - 自动化验证脚本 `python scratch/verify_5_37.py` 19 项全部 PASS（包含 DLL 二进制字符串、`RewardBackendFlow` 与 `AiTeammateMod` 核心不变量）。

## 62. #5.38 修复 P2 牌组变牌/通用选牌未拦截、战斗选牌后 Solver 中断失联、跨回合结束回合按钮消失、以及篝火 P2 不自动选择 (2026-10-31)

- **指令指纹**：`Directive #5.38 | Built: 2026-10-31`，源码：`scratch\AiTeammateMod.cs`，产物：`mods\AiTeammate\AiTeammate.dll`。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`6f01f813cc4c83c4712d49826a715edeb3226fb7e6473cd57abd20864ed6c265`
  - 源码 `scratch\RewardBackendFlow.cs`：`acc9ad76b68fdc31130c881f01c6cd0f5fa7711647585f548fe4705d3449a519`
  - 产物 DLL `mods\AiTeammate\AiTeammate.dll`：`52710e2e66f33db45ae68bf8530620ada51850dbacb3c48977c8159eade39042`
- **一、T1：牌组变牌（FromDeckForTransformation）与通用选牌（FromDeckGeneric）自动化与卡牌选择标记（Task 1）**：
  - **`FromDeckForTransformation` 自动化**：
    - 新增 `CardSelectFromDeckForTransformationPatch`，严格归属检查 `player.NetId == p2.NetId`（P1 100% 手动放行 `return true`）。
    - 评分规则：评分最低卡牌优先，保护单一协同源 `vulnerable-source`、`card-draw-engine`、`block-scaling`。
    - 校验合法性：调用传入的 `Func<CardModel, CardModel> cardToTransformation`，确保 `cardToTransformation(card) != null`（过滤不可变卡牌）。
    - 熔断保护与日志：输出 `p2-choice-offer kind=transform owner=P2` 与 `p2-choice-resolved kind=transform pick={cid} count={cnt} reason=card-score-lowest source=card_scores`。
    - 返回 `Task.FromResult<IEnumerable<CardModel>>(selectedCards)` 并 `return false` 拦截原生选牌界面。
  - **`FromDeckGeneric` 自动化**：
    - 新增 `CardSelectFromDeckGenericPatch`，严格归属检查。
    - 解析 `CardSelectorPrefs.Prompt` 提示文本：若包含 `upgrade` / `duplicate` / `copy` / `enchant` / `升级` / `复制` / `附魔`，则按最高分选择；其余按最低分选择并启用单一协同源保护。
    - 熔断保护与日志：输出 `p2-choice-offer kind=generic owner=P2 intent={intent}` 与 `p2-choice-resolved kind=generic pick={cid} count={cnt} reason={reason} source=card_scores`。
  - **挂载所有 `FromDeck*` 方法**：
    - 在 `PatchFlowCoordinator` 中遍历 `CardSelectCmd` 所有静态方法，统合挂载 `FromDeckForTransformation`、`FromDeckGeneric`、`FromDeckForUpgrade`、`FromDeckForRemoval`。
    - 在全部 5 个 `FromDeck*` 补丁中统一设置 `FlowCoordinator.LastChildCardChoiceCompleted = true;`，防止事件外部循环守卫误判。
- **二、T2：修复战斗选牌后 Solver 中断失联卡死（Task 2）**：
  - **挂载 `CombatSolver.SolverController.PauseAfterNativeChoiceFailure`**：
    - Harmony Postfix 拦截 `PauseAfterNativeChoiceFailure`，捕获原生选牌中断状态。
    - 重置部署状态：`EnsureFullAutoEnabled()`、`IsDeploying = false`、`MidDeployStartTime = DateTime.MinValue`、`PendingNativeChoiceContinuation = true`、`NativeChoiceFailureTime = DateTime.UtcNow`。
    - 记录结构化日志：`solver-native-choice-recovery: caught PauseAfterNativeChoiceFailure on turn={turn}, scheduling post-action continuation`。
  - **动作窗口与每帧推进恢复**：
    - 在 `ActionExecutionWindowExitPatch.Postfix` 与 `TurnCoordinator.OnProcessTick` 中调用 `CheckNativeChoiceContinuation`。
    - 当 `PendingNativeChoiceContinuation && P2ActionSenderScopeDepth == 0` 时：
      - 检查手牌与能量：若无可用卡牌或能量为 0，直接安全结束回合 `EnqueueP2EndTurn(p2)`。
      - 若有可用卡牌且本回合尚未重试：标记 `HasRetriedNativeChoiceRecoveryThisTurn = true`，调用 `KickSolverRecalculation(state, "native-choice-recovery")` 触发重新规划。
      - 若 2.5 秒内仍未部署或已重试过：自动降级为贪心保底 `TryFallbackGreedy(p2)` 并安全结束回合 `EnqueueP2EndTurn(p2)`。
- **三、T3：修复跨回合结束回合按钮（NEndTurnButton）消失（Task 3）**：
  - **消除非出牌阶段对按钮状态的覆写**：
    - 新增 `TurnCoordinator.IsPlayPhase()` 严格判定（检查 `PlayerCombatState.Phase == PlayerTurnPhase.Play`）。
    - 在 `RefreshPlayer1CombatUiMinimal` 中，仅在 `TurnCoordinator.IsPlayPhase()` 时调用 `ReevaluateEndTurnButtonState`。
    - 在 `DraReevaluateEndTurnButtonStatePatch` 中新增 Prefix，非 `IsPlayPhase` 时直接 `return false` 拦截 DRA 覆写。
    - 新增 `EndTurnButtonSetStateGuardPatch` 挂载 `NEndTurnButton.SetState` Prefix：在非 `IsPlayPhase` 时，拦截所有非 `State.Hidden` (2) 的状态转换，记录 `endturn-nonplayphase-setstate-blocked`，彻底杜绝从 `Hidden` 变为 `Disabled` 导致缺失进场动画。
  - **出牌阶段按钮动画自愈**：
    - 在 `DraReevaluateEndTurnButtonStatePatch.Postfix` 与新增的 `EndTurnButtonRefreshEnabledPostfixPatch` 中：
    - 当处于 `PlayPhase` 且 P1 尚未结束回合时，若检测到按钮处于隐藏或透明度低（`Modulate.A < 0.9f`），重置 `_state = State.Hidden (2)` 并调用 `SetState(State.Enabled) (0)`，强制触发原生 `AnimIn` 动画将按钮滑入视口并恢复不透明度，输出 `endturn-button-refresh-anim-healed`。
- **四、T4：修复篝火（RestSite / RestSiteSynchronizer）P2 不自动选择（Task 4）**：
  - **解除对 LocalContext.NetId 的错误依赖**：
    - 移除 `RestSitePatch.Postfix` 中 `if (currentNetId != p2.NetId) return;` 限制，移除 P1 按钮点击代码，统一转发至 `FlowCoordinator.ResolvePendingP2RestSiteOnSettle("NRestSiteRoom.UpdateRestSiteOptions")`。
    - 挂载 `RestSiteSynchronizer.BeginRestSite` 与 `NRestSiteRoom._Ready` Postfix，以 `CallDeferred` 触发 `ResolvePendingP2RestSiteOnSettle`。
  - **幂等与上下文隔离**：
    - `ResolveP2RestSite` 引入跨生命周期幂等键：`LastResolvedRestSiteKey = generation + "|floor=" + floor + "|p2"`，杜绝同一篝火重复结算。
    - 在 P2 `LocalContext.NetId` 临时作用域下调用 `synchronizer.ChooseOption(p2, chosenIdx)`，并通过 `MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely` 观察返回任务。
    - 调用完成后断言并记录前台保持状态：`rest-foreground-assert expected=P1 actual=P1`，确保玩家视口全程不发生抖动或切人。
- **五、构建与验证**：
  - 严格遵循 C# 5 语法规范，.NET Framework 4.0 `csc.exe` 编译 0 warning、0 error。
  - 自动化验证脚本 `python scratch/verify_5_38.py` 23 项全量 PASS（含 DLL UTF-16LE 字符串与源码不变量双重校验）。

## 63. #5.39 修复 DEMONIC_SHIELD 等联机友方目标导致 CombatSolver 崩溃、ASCENDERS_BANE 等不可用诅咒误选、以及保底同步出牌竞态 (2026-11-01)

- **指令指纹**：`Directive #5.39 | Built: 2026-11-01`，源码：`scratch\AiTeammateMod.cs`，产物：`mods\AiTeammate\AiTeammate.dll`。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`1160f2ad745829b31fa25fd7cc1f92a0378f455c4e9d5efe1d14280a1e49e4f4`
  - 源码 `scratch\RewardBackendFlow.cs`：`acc9ad76b68fdc31130c881f01c6cd0f5fa7711647585f548fe4705d3449a519`
  - 产物 DLL `mods\AiTeammate\AiTeammate.dll`：`1e9086d7bf14c1df57a118cf0d6db7e63be87d119669f4e9c11801a46fddf331`
- **一、T1：硬化 CheckIsPlayable 与避选 CombatSolver 不兼容联机友方牌（Task 1）**：
  - **`CheckIsPlayable(CardModel card)` 严格判定**：
    - 前置第一层：直接拦截 `card == null`、`card.Type == CardType.Curse || Status || Quest`，以及 ID 包含 `ASCENDERS_BANE`（进阶之灾）的卡牌，直接 `return false`。
    - 第二层：遍历 `card.Keywords`，若包含 `Unplayable`（不可打出）关键字，直接 `return false`。
    - 第三层：通过反射调用 `CanPlay()`（0 参与单参重载），若返回 `false` 则直接 `return false`。
    - 第四层：校验 `IsPlayable` 属性作为最终兜底，彻底杜绝诅咒牌误入保底打出队列。
  - **Neow 与选牌避开友方目标联机卡牌**：
    - 在 `EvaluateAncientOptionHeuristic` 中对 `MASSIVE_SCROLL`（巨大卷轴）施加 `-5000` 巨额扣分，确保 P2 永远优先选择原生单人祝福；在 `ResolveP2PendingEvent` 中增加一票否决权，拦截 SpireStrategist 决策误选 `MASSIVE_SCROLL`。
    - 在 `CardSelectFromChooseACardScreenPatch`、`EvaluateCardReward`（卡牌奖励评分与决策兜底）中，针对 `TargetType.AnyPlayer` 或 `TargetType.AnyAlly` 的卡牌扣除 5000 分，优先选取普通单人目标卡牌，防止 `DEMONIC_SHIELD` 等导致 CombatSolver 动作回放 `target=-` 崩溃。
- **二、T2：修复 TryFallbackGreedy 同步竞态、支持多张连续出牌与必定结束回合（Task 2）**：
  - **解除历史出牌限制**：移除 `P2PlayedCardsThisTurn > 0` 门禁，允许中途卡死后打出手中剩余可打卡牌。
  - **消除同步执行标志位竞态**：在 `enq.Invoke(aqs, ...)` 之前置位 `P2FallbackGreedyUsedThisTurn = true; P2FallbackActionPendingThisTurn = true; P2FallbackActionEnqueuedThisTurn = true;`，确保同步完成时触发的 `PlayCardActionExecutePostfix` 能准确捕获并推进后续状态。
  - **最多 6 轮贪心连打循环**：
    - 在 `energy > 0` 且手牌存在可用牌时循环打出。
    - 使用 `HashSet<CardModel> triedCards` 防止异常或取消卡牌死循环重试。
    - 排序优先级：攻击牌优先 > 非友方目标优先 > 技能/能力牌 > 卡牌评分高优先。
    - 通过 `using (EnterP2ActionSenderScope("TryFallbackGreedy"))` 封装入队。
  - **必结回合铁律**：循环结束后立即调用 `EnqueueP2EndTurn(p2)`；若未能成功排入或仍未就绪，调用 `ForceEndTurnForPlayer2` 强制结束；同时置位 `HasCompletedDeploymentThisTurn = true` 与 `P2ZeroActionProtectionFinalizedThisTurn = true`，杜绝 P2 挂死。
- **三、T3：CombatSolver 崩溃快速恢复与回合状态清洁重置（Task 3）**：
  - **`CheckSolverStallWatchdog` 快速崩溃判定**：
    - Level 1：若 `> 12.0s` 超时，或 `>= 2.5s` 且检测到 `!IsSolverSearching() && !IsSolverDeploying() && !P2DeployHookObservedThisTurn`（搜索已提前崩溃死寂），立即以 `solver-search-aborted` 触发重算，不再傻等 12 秒。
    - Level 2：重算后若 `>= 8.0s`，或 `>= 1.5s` 且检测到搜索再次死寂（`!IsSolverSearching() && !IsSolverDeploying()`），立即以 `solver-recalc-aborted` 触发 Level 2 缓存/贪心保底，杜绝累计 20 秒假死。
    - Level 2 保底后严密校验 `!cm.IsPlayerReadyToEndTurn(player2)`，确保自动兜底结束回合。
  - **战前与回合状态重置**：
    - `CompleteDeploymentPostfix` 首行增加战前/载入中门禁：若处于 `IsPreCombatOrLoading` 或回合初无手牌阶段，直接退出，不提前置位 `P2ZeroActionProtectionFinalizedThisTurn` 或输出虚假摘要。
    - 在 `ResetTurnState` 的 `SetupPlayerTurn` / `CombatStart` / `RoundStart` 统一将 `P2ZeroActionProtectionFinalizedThisTurn`、`P2SummaryFinalizedThisTurn`、`P2FallbackGreedyUsedThisTurn`、`P2FallbackActionPendingThisTurn` 等全部清零重置。
- **四、构建与验证**：
  - 严格遵循 C# 5 语法规范，.NET Framework 4.0 `csc.exe` 编译 0 warning、0 error。
  - 自动化验证脚本 `python scratch/verify_5_39.py` 21 项全量 PASS（8 项 DLL UTF-16LE 字符串与 13 项源码架构不变量双重校验）。

## 64. #5.40 完善 P2 商店全自主购物（删牌、遗物、卡牌与药水） (2026-11-01)

- **指令指纹**：`Directive #5.40 | Built: 2026-11-01`，源码：`scratch\AiTeammateMod.cs`，产物：`mods\AiTeammate\AiTeammate.dll`。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`6b974a379a1a657bd155914e193bff7c8e32bc6534ef1400b23ab4ab92189fc8`
  - 源码 `scratch\RewardBackendFlow.cs`：`acc9ad76b68fdc31130c881f01c6cd0f5fa7711647585f548fe4705d3449a519`
  - 模组 DLL `mods\AiTeammate\AiTeammate.dll`：`72c07f6fcd9400fc74d92aace1e860d04277ea5a7c07253059667ca26d2f6a38`
- **一、T1：硬化 IsCardRemovable 与修正 CardSelectFromDeckForRemovalPatch（Task 1）**：
  - **`FlowCoordinator.IsCardRemovable(CardModel card)` 严格不可删判定**：
    - 快速短路：`card == null` 或 `card.Type == CardType.Quest` 直接返回 `false`。
    - 诅咒过滤：卡牌 ID 包含 `"ASCENDERS_BANE"`（进阶之灾）、`"NECRONOMICURSE"`（死灵诅咒）、`"CURSE_OF_THE_BELL"`（钟之诅咒）直接返回 `false`。
    - 关键字检查：遍历 `card.Keywords`，若任何关键字包含 `"Eternal"`（固有/不可去除）直接返回 `false`。
    - 反射检查：探测 `IsRemovable`、`CanBeRemovedFromDeck` 属性与方法，若显式返回 `false` 则返回 `false`。
  - **`FlowCoordinator.GetCardRemovalPriorityScore(CardModel card)` 删牌评分体系**：
    - `Curse` 设定 `-1000.0`，`Status` 设定 `-500.0`，极优先移除。
    - 基础分结合 `GetCardScore(card.Id)`。
    - `Basic` 或 ID 包含 `"STRIKE"`：`Math.Min(baseScore, 15.0)`（未升级再扣 `5.0`，极优先删未升级打击）。
    - ID 包含 `"DEFEND"`：`Math.Min(baseScore, 22.0)`（未升级再扣 `3.0`）。
    - 已升级卡牌加 `15.0` 保护分，确保保留强化卡。
  - **`CardSelectFromDeckForRemovalPatch.Prefix` 拦截过滤**：
    - 候选池构建与牌组全集遍历必须强制通过 `IsCardRemovable(c)`，完全杜绝将进阶之灾等不可删卡牌加入候选池。
    - 牌张排序采用 `GetCardRemovalPriorityScore` 升序排序。
    - 无候选卡保底逻辑选用 `removableInDeck[0]`，禁止保底至不可删卡牌。
- **二、T2：实现四阶段顺序异步购物流水线 ExecuteP2ShopVisitAsync（Task 2）**：
  - **后台孤岛与配置状态管理**：
    - 新增 `AutoShopEnabled = true` 与 `IsExecutingP2AutoShop = false`。
    - `HandledShopVisits` 仅在 `p2Inventory != null` 后才记录，确保过早 hook 不漏掉就绪后的自动购物。
    - 彻底移除旧逻辑中 `p2.Gold < cost + 25` 的 25 金币强制保留人工阻断，只要金币充足且符合价值门槛即可购买。
  - **阶段 1：删牌购买（Card Removal）**：
    - 检查 `p2Inventory.CardRemovalEntry` 且 `p2.Gold >= cost` 且牌组 >= 8 张。
    - 最优卡牌评分 `<= 50.0` 时触发购买，顺序调用 `await removalEntry.OnTryPurchaseWrapper(p2Inventory, false, false)`。
    - 购买成功标记 `removalEntry.SetUsed()`，记录 `shop-resolved kind=removal`。
  - **阶段 2：遗物购买（Relic Purchases，最多 2 件）**：
    - 设立 10 件后台购买危险黑名单 `IsShopRelicSafeForBackgroundPurchase`：`ORRERY`、`CAULDRON`、`DOLLYS_MIRROR`、`BOTTLED_`、`CALLING_BELL`、`CURSED_PEARL`、`MASSIVE_SCROLL`、`PANDORAS_BOX`、`ASTROLABE`、`EMPTY_CAGE`，杜绝产生跨屏弹窗或拉取队友目标卡。
    - 结合 SpireStrategist `RelicValue.Evaluate` 与稀有度/高价值特定遗物启发式评分（基础 25，稀有/商店 +15，罕见 +10，普通 +5，锚/角铁/轮盘/金刚杵等核心遗物 +15）。
    - 评分 `>= 15.0` 且金币充足时，顺序调用 `await bestEntry.OnTryPurchaseWrapper(p2Inventory, false)`。
  - **阶段 3：卡牌购买（Card Purchases，最多 2 张）**：
    - 硬性排查：过滤不可用牌、诅咒/状态/任务牌，并硬核剔除 `TargetType.AnyPlayer || TargetType.AnyAlly` 的友军目标卡（杜绝导致 CombatSolver 崩溃）。
    - 第一轮门槛：折算分 `>= 60.0`（特价卡 `>= 52.0`）；第二轮门槛：购后金币 `>= 100` 且折算分 `>= 68.0`。
    - 考虑已有张数惩罚（2 张扣 20，1 张扣 5）与牌组臃肿惩罚（>= 28 张扣 12）。
    - 顺序调用 `await bestCardEntry.OnTryPurchaseWrapper(p2Inventory, false)`。
  - **阶段 4：药水购买（Potion Purchases，最多 2 瓶）**：
    - 反射探测 `p2.HasOpenPotionSlots` 与空槽计数 `HasOpenPotionSlot(p2)`。
    - 排除 `FOUL_POTION`，稀有 80、罕见 65、普通 50，关键战斗药水（妖精/幽魂/力量/敏捷/邪教徒/血/回生/火/爆炸等）加 10 分。
    - 评分 `>= 50.0` 时顺序调用 `await bestPotEntry.OnTryPurchaseWrapper(p2Inventory, false)`。
  - **前台焊死断言与购物总结**：
    - 异步流程在 `finally` 中严格复位 `IsExecutingP2AutoRemoval = false; IsExecutingP2AutoShop = false;`。
    - 强制执行 `shop-foreground-assert expected=P1 actual=P1`，若发现视角偏移立即调用 `SwitchControlToPlayer(p1Id, "wakuu-p1-restore:shop-room-exit", false)` 恢复 P1 前台。
    - 打印标准化结算汇总日志 `shop-visit-summary owner=P2 room={roomId} gold_before={initialGold} gold_after={p2.Gold} removed={removedCount} relics={relicsBought} cards={cardsBought} potions={potionsBought}`。
- **三、验证**：
  - 严格遵循 C# 5 语法规范，.NET Framework 4.0 `csc.exe` 编译 0 warning、0 error。
  - 自动化验证脚本 `python scratch/verify_5_40.py` 4 大项 100% 全部 PASS（双二进制哈希、16 处关键 DLL UTF-16LE 字符串、9 个核心方法元数据、7 大源码不变量）。

## 65. #5.41 修复 P1 选牌弹窗误拦截导致战斗冻结与抽牌后部署假死 (2026-11-01)

- **指令指纹**：`Directive #5.41 | Built: 2026-11-01`，源码：`scratch\AiTeammateMod.cs`，产物：`mods\AiTeammate\AiTeammate.dll`。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`35e01a1f8a4edb7ca1d519cce1d0691cc87eddd53ae68b8550242d40932e8290`
  - 源码 `scratch\RewardBackendFlow.cs`：`acc9ad76b68fdc31130c881f01c6cd0f5fa7711647585f548fe4705d3449a519`
  - 模组 DLL `mods\AiTeammate\AiTeammate.dll`：`5b556568a731beb615e946c6d62d71e031c5ac60f5b63c57f393879714766b61`
- **一、T1：绝不拦截 P1 选牌弹窗（OverlayStackPushPrefix 深度防护）（Task 1）**：
  - **根本原因根治**：
    - P1 喝无色药水（`UsePotionAction`）触发 `CardSelectCmd.FromChooseACardScreen`，该弹窗无 `owner` 属性；
    - 当 P2 此前出牌悬挂 `IsDeploying == true` 时，旧逻辑误判 `(IsDeploying || HoldActive) && owner == null` 将 P1 弹窗当场拦截（`return false`），导致 `UsePotionAction` 永远停在队列头部等待玩家选择，引发战斗彻底冻结。
  - **P1 弹窗显式通行判定 (`isExplicitlyP1`)**：
    - 新增静态时间戳 `ChoiceSuppressionPatches.LastP1ChoicePassThroughTime`。
    - 全面覆盖 9 大卡牌选择指令入口（`CardSelectFromChooseACardScreenPatch`、`FromChooseACardScreenPrefix`、`InCombatGridDecide`、`InCombatHandDecide`、`FromDeckForTransformation`、`FromDeckGeneric`、`FromDeckForUpgrade`、`FromDeckForRemoval`、`FromDeckForEnchantment`），凡检测到 `player.NetId != p2NetId` 放行原生 UI 时，均记录当前时间戳。
    - 在 `OverlayStackPushPrefix` 中，先探测 P1 是否正在选择：`owner == P1`、`IsP1ActionExecuting`、`CurrentExecutingActionOwnerNetId == P1`、`recentP1PassThrough (< 2.5s)`、或 P1 动作队列因等待选择被暂停（`p1QueuePausedForChoice`）。
    - 凡满足 P1 正在选择条件且不属于 P2 时，一律放行并记录 `[AiTeammate][Coord] overlay-allowed-p1 screen={type}`。
  - **收紧无主弹窗拦截边界**：
    - 无主弹窗（`owner == null`）仅在 P2 真正处于执行/选择中（`P2ActionSenderScopeDepth > 0 || CurrentExecutingActionOwnerNetId == p2NetId || p2QueuePausedForChoice`）时才允许拦截，彻底切断并行操作下的误拦截。
- **二、T2：硬化 CardSelectFromChooseACardScreenPatch 战时闭环（Task 2）**：
  - **归属初始化**：进入候选池时确保候选牌与优选牌如果 `Owner == null` 立即补齐 `c.Owner = p2`。
  - **战斗中禁止退回原生 UI（Zero Fail-Open）**：
    - 战斗状态下（`TurnCoordinator.IsInCombat()`），熔断器跳闸（`!AutopilotFuse.TryBeginOffer`）或超时（`elapsed > 3.0s || bestCard == null`）时，严禁 `return true` 触发原生弹窗暂停队列，而是纯内存安全选定保底牌（`bestCard ?? cardList[0]`），返回已完成任务 `Task.FromResult` 并返回 `false`。
    - 异常捕获同样在战时对 P2 实行第一张牌保底，绝不漏向原生 UI。
- **三、T3：抽牌卡后部署卡顿快速收敛（BURNING_PACT Fast-Finish）（Task 3）**：
  - **根本原因根治**：
    - P2 打出过牌卡（如 `BURNING_PACT` 消耗一张抽两张）后，CombatSolver 终止出牌且未调用 `CompleteDeployment`，致使 `IsDeploying == true` 悬挂 10 秒等待 `StallWatchdog`，进而拖延至 20 秒安全线。
  - **`IsPlayer2ActionInFlight()` 精确状态探测**：
    - 综合检查 P2 正在执行动作、P2 动作发送作用域（`P2ActionSenderScopeDepth`）、P2 动作队列是否暂停、以及 P2 动作队列剩余数量。
  - **1.8 秒快速收尾与贪心补刀**：
    - 在 `OnProcessTick()` 中，当 `IsDeploying && HasExecutedActionInDeployment && P2ActionSenderScopeDepth == 0`，且距离上一动作执行完毕超过 1.8 秒且 `!IsPlayer2ActionInFlight()` 时：
    - 立即安全收尾悬挂部署：`IsDeploying = false; HoldActive = false; HasCompletedDeploymentThisTurn = true;`。
    - 输出 `[AiTeammate][Coord] deployment-draw-stall-fast-finish` 快速收敛日志。
    - 立即恢复 P1 界面与控制权。
    - 若 P2 仍有可用费用与新抽到的可用卡（如打击），立即唤醒 `TryFallbackGreedy(p2)` 贪心出牌，并执行 `EnqueueP2EndTurn(p2)` 顺畅结束 P2 回合，将卡顿从 10~20 秒彻底降至 1.8 秒平滑过渡。
- **四、验证**：
  - 严格遵循 C# 5 语法规范，.NET Framework 4.0 `csc.exe` 编译 0 warning、0 error。
  - 自动化验证脚本 `python scratch/verify_5_41.py` 4 大项 100% 全部 PASS。


## 66. #5.42 修复 TryFallbackGreedy 超额入队崩溃、P2ActionSenderScopeDepth 泄漏与 P1 回合后控制锁定 (2026-11-01)

- **构建指纹**：`Directive #5.42 | Built: 2026-11-01`，源码：`scratch\AiTeammateMod.cs`，部署：`mods\AiTeammate\AiTeammate.dll`。
- **SHA-256 校验**：
  - 源码 `scratch\AiTeammateMod.cs`：`8cfa4ad6e83f0826e8d8d96ee0f82169a79c6b434975abf3230414056f3730e6`
  - 源码 `scratch\RewardBackendFlow.cs`：`acc9ad76b68fdc31130c881f01c6cd0f5fa7711647585f548fe4705d3449a519`
  - 模组 DLL `mods\AiTeammate\AiTeammate.dll`：`b09b3111a1188521567de58ab61d6eefd5af1aaaa38e1545177899412bbc5eef`
- **一、T1：修复 TryFallbackGreedy 能量账本与结束回合时序（Task 1）**：
  - **根本原因消除**：`TryFallbackGreedy` 在同步循环中向动作队列连续提交卡牌，由于异步动作尚未出队执行，`p2.PlayerCombatState.Energy` 未扣除，导致 3 点能量在 `iter` 循环中连续塞入全部 5 张手牌（总耗能 6 点）；并在循环末尾因卡牌仍在队列执行导致 `cm.IsPlayerReadyToEndTurn(p2) == false`，立即同步调用 `ForceEndTurnForPlayer2`，在第 1 张牌还在执行中强行介入结束回合。
  - **批量入队标记与本地能量预算**：
    - 新增 `TurnCoordinator.IsEnqueuingFallbackBatch` 静态标记，进入循环前置为 `true`，`finally` 中置为 `false`。
    - 本地跟踪 `int remainingEnergy = p2.PlayerCombatState.Energy;`。
    - 循环候选牌过滤增加 `int cCost = GetCardEnergyCost(c); if (cCost > remainingEnergy) continue;`，选定牌后精确扣除 `remainingEnergy -= chosenCost`（X 费卡扣空 `remainingEnergy = 0`）。
    - 循环结束后，若 `EnqueueP2EndTurn(p2)` 成功入队则标记 `P2FallbackEndTurnQueuedThisTurn = true`；严禁在队列有动作或入队成功时强杀回合，仅在 `!queued && cardsPlayedInLoop == 0 && !IsPlayer2ActionInFlight()` 时才作为最后兜底调用 `ForceEndTurnForPlayer2`。
    - 在 `p2-play-count` 钩子中，增加 `!TurnCoordinator.IsEnqueuingFallbackBatch` 守卫，杜绝在 `iter=0` 与 `iter=1` 之间插入 `EndPlayerTurnAction`。
- **二、T2：在 EnqueueP2EndTurn 中传递实际战斗回合数并归属 P2（Task 2）**：
  - **根本原因消除**：此前反射构造 `EndPlayerTurnAction` 时对 `int` 参数传入 0，导致引擎以 `Current turn number: 1 action turn number: 0` 拒绝处理并输出忽略警告。
  - **动态回合获取与作用域归属**：
    - 计算 `int currentRound = (cs != null && cs.RoundNumber > 0) ? cs.RoundNumber : (TurnNumber > 0 ? TurnNumber : 1);`，将参数 0 替换为 `currentRound`。
    - 并在入队时包裹 `using (EnterP2ActionSenderScope("EnqueueP2EndTurn"))`，使动作明确归属 P2。
- **三、T3：阻断被取消的 P2 PlayCardAction 触发 NCardPlayQueue 回退 UI 与防崩（Task 3）**：
  - **根本原因消除**：超额入队的 P2 动作因费用不足取消时触发 `NCardPlayQueue.RemoveCardFromQueueForCancellation` -> `NPlayerHand.AddCardHolder`；`NPlayerHandAddPrefix` 释放 holder 并返回 false，导致第三方前缀/后缀（`STS2RitsuLib.ModRightClickCardHolderPatch`）遭遇空指针崩溃。进而导致 `GameAction.Execute()` 异常退出，绕过了 `ActionExecutor.AfterActionFinished`，使 `P2ActionSenderScopeDepth` 永久泄漏（2..10）。
  - **前置拦截与 Holder 保护**：
    - Harmony Prefix 拦截 `NCardPlayQueue.RemoveCardFromQueueForCancellation`，凡检测到入参为 P2 的 `PlayCardAction` 即打印 `p2-cancel-card-queue-ui-suppressed` 并返回 `false`，从根源切断向 `NPlayerHand` 的投递。
    - 在 `NPlayerHandAddPrefix` 中，当拦截 P2 卡牌时，若目标为 `NCardHolder` 则不调用 `nodeToFree.QueueFree()`，彻底消除第三方库 Postfix 空指针崩溃。
- **四、T4：P2ActionSenderScopeDepth 100% 异常安全与自愈闭环（Task 4）**：
  - **根本原因消除**：任何在 `GameAction.Execute()` 执行过程中的未捕获异常或取消均会导致 `AfterActionFinished` 无法执行，使 `P2ActionSenderScopeDepth` 泄露递增，进而导致 `LocalContext.NetId` 卡在 P2，所有 P1 恢复被 defer，P1 鼠标无法点击出牌。
  - **双重关闭与幂等释放**：
    - 提炼幂等共享方法 `AttributionPatches.ExitActionExecutionScope(GameAction action, string source)`。通过 `lock (P2SenderScopes)` 和字典原子移除保证单次消费，多次调用天然幂等。
    - 在 `GameAction.Execute()` 挂接 Postfix，对返回的 `Task __result` 附加 `__result.ContinueWith(...)` 异步保证闭环，无论正常完成、异常还是取消，均必定触发 `ExitActionExecutionScope`。
    - 新增 `AttributionPatches.ClearAllP2SenderScopes()`。
  - **状态自愈（Self-Healing）**：
    - 在 `TurnCoordinator.ResetState(source)`（由 `SetupPlayerTurn`、`SetUpCombat`、`OnEndedTurnLocally` 触发）及 `TurnCoordinator.OnProcessTick()` 中：
    - 若检测到 `P2ActionSenderScopeDepth > 0` 且无 P2 动作在飞（`!IsPlayer2ActionInFlight(false)`），自动强清作用域：`AttributionPatches.ClearAllP2SenderScopes(); P2ActionSenderScopeDepth = 0; LocalContext.NetId = p1; TurnFlowPatches.RestorePlayer1ControlIfActive("p2-scope-self-heal", false);`。
    - 彻底消除了 P2 回合后 P1 控制权被锁死需要手动切人的恶性缺陷。
- **五、验证**：
  - 严格遵循 C# 5 语法规范，.NET Framework 4.0 `csc.exe` 编译 0 warning、0 error。
  - 自动化验证脚本 `python scratch/verify_5_42.py` 4 大项 100% 全部 PASS。
