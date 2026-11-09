# 第三方开源项目与鸣谢声明 (Third-Party Notices & Acknowledgments)

`AiTeammate` 是一个为《杀戮尖塔 2》（Slay the Spire 2）开发的本地双人同屏 AI 队友模组。本项目秉持开源协同理念，在架构设计与功能实现中参考、集成或交互了社区优秀的开源模组与技术框架。

特此向以下模组项目、原作者及开发团队致以最崇高的敬意与感谢：

---

### 1. STS2_DualRoleAdventure (Local Multi-Control / 本地双人同屏)
- **项目仓库**: [https://github.com/GuyGinat/STS2_DualRoleAdventure](https://github.com/GuyGinat/STS2_DualRoleAdventure)
- **原作者**: **GuyGinat**
- **开源协议**: MIT License
- **集成与致谢说明**:
  - 本项目最初基于 `STS2_DualRoleAdventure` 的早期架构派生并进行深度重构。
  - 保留并拓展了其在单台主机上创建双角色本地联机会话的核心机制、P1 与 P2 控制上下文切换框架，以及联机状态栏（`PlayerState`）同屏同步逻辑。

---

### 2. CombatSolver (战斗路径求解器)
- **项目仓库**: [https://github.com/Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver)
- **Steam 创意工坊**: [CombatSolver Workshop Page](https://steamcommunity.com/sharedfiles/filedetails/?id=3790899961)
- **原作者**: **Torch (Torch1230)**
- **开源协议**: MIT License
- **集成与致谢说明**:
  - 本项目将 `CombatSolver` 作为 P2 战斗内回合出牌推演与药水规划的核心求解引擎。
  - **重要原则：本项目不打包、不分发、亦不修改用户磁盘上的 `CombatSolver.dll` 文件**。
  - 在游戏启动与程序运行时，`AiTeammate` 通过反射与纯内存运行时补丁注入器（`CombatSolverInMemoryPatcher`），在进程内存镜像中将原版单人求解视角安全重定向至 P2，并解除了联机环境下的运行限制。

---

### 3. StS2.RandomForeseer (随机数预测模组)
- **项目仓库**: [https://github.com/hotwords123/StS2.RandomForeseer](https://github.com/hotwords123/StS2.RandomForeseer)
- **原作者**: **hotwords123**
- **开源协议**: MIT License
- **集成与致谢说明**:
  - `CombatSolver` 的底层单回合深度搜索推演框架中，关于战斗状态仿真、虚拟牌堆抽洗牌模型、RNG 分支模拟、状态叉乘（Fork）与镜像回放（Mirror）等基础推演体系均来源于并改进自 `RandomForeseer`。
  - 在此一并向 **hotwords123** 在 StS2 模拟与预测基石领域的杰出贡献致以诚挚感谢。

---

### 4. SpireStrategist (全局战略助手)
- **项目仓库**: [https://github.com/shaokangqian4-art/SpireStrategist](https://github.com/shaokangqian4-art/SpireStrategist)
- **原作者**: **穷仔 / shaokangqian4-art**
- **开源协议**: MIT License
- **集成与致谢说明**:
  - 本项目在运行时通过反射无侵入连接用户本地安装的 `SpireStrategist`（版本 `0.6.72+`），驱动 P2 的非战斗全局策略决策（包括战后三选一选牌、涅奥初始奖励抉择、全事件最优选项评估、休息处升级与锻造抉择、商店卡牌/遗物/药水采购及删牌决策、Boss 遗物评分等）。
  - 本项目在此基础上向 `SpireStrategist` 动态注入双人协同感知上下文（P1 队友危急度保护、全队遗物去重感知、P2 专属牌组进化流派识别）。
  - **重要原则：本项目不打包、不分发 `SpireStrategist.dll`**。

---

### 5. STS2-RitsuLib (基础支持库)
- **用途说明**: 为 `CombatSolver` 与 `SpireStrategist` 提供底层辅助与基础钩子支撑。
- **获取方式**: 由用户根据上述前置模组的说明从 Steam 创意工坊自行订阅安装。

---

### 开源协议声明 (License Notice)
本项目自身采用 **MIT License** 授权许可，各依赖与引用项目的知识产权及版权归各自原作者所有。
详细许可证内容请参阅本仓库根目录下的 [LICENSE](LICENSE) 文件。
