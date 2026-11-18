# ClassExland 任务清单

> 与团队任务列表同步；每完成一项在「状态」列打勾，并写明产物路径。
> 状态：⬜ 未开始 / 🔄 进行中 / ✅ 完成（含自检）

| # | 任务 | 状态 | 负责 | 产物 |
|---|---|---|---|---|
| 1 | 确认关键决策并记录 | ✅ | team-lead | `docs/DECISIONS.md`（12 条决策 + 宿主 API 实测结论） |
| 2 | 建立仓库骨架与共享契约 | ✅ | team-lead | `src/ClassExland/Contracts/Contracts.cs`、`Plugin.cs`、`manifest.yml` |
| 3 | BellModule + Harmony 状态覆盖 | ✅ | bell-dev | `Features/Bell/`（10 文件，含 `BellTimelineCalculator` 纯逻辑层） |
| 4 | 值日表核心与组件 | ✅ | duty-dev | `Features/Duty/`（Models/Services/Controls/Views） |
| 5 | 下课值日提醒 + 活动提前 10 分钟 | ✅ | duty-dev | `Features/Duty/Reminders/`、`Activities/` |
| 6 | LayoutModule 界面切换 | ✅ | sys-dev | `Features/Layout/`（含推荐自动化配置生成） |
| 7 | 远程配置同步（快照 + 指令） | ✅ | sys-dev | `Features/Remote/`（HTTPS + Bearer/HMAC + 白名单 + 备份回滚） |
| 8 | 全面运维 CLI | ✅ | cli-dev | `src/ClassExland.Cli/`（`cex.dll` 独立构建 0 错误 0 警告）、`Features/Bridge/` |
| 9 | 旧插件配置迁移 | ✅ | duty-dev | `Features/Duty/Migration/`（RotationCalculator 等 7 文件） |
| 10 | 创意功能（排除 2 项） | ✅ | 各模块分摊 | 见各 `Features/` 目录 |
| 11 | 项目文档 | ✅ | docs-dev | `docs/*.md`（6 篇）、`README.md`、`CONTRIBUTING.md` |
| 12 | CI/CD 与发版 | ✅ | docs-dev | `.github/workflows/build.yml` |
| 13 | 集成构建与验收 | 🔄 | team-lead | `docs/ACCEPTANCE.md`、`tests/ClassExland.Tests/` |
| 14 | 推送到 `yiwubianjihui/classexland` | ⬜ | team-lead | 待 #13 通过后执行 |

## 构建收敛记录（Release / net8.0）

| 轮次 | 错误数 | 主要问题 | 处置 |
|---|---|---|---|
| 第 1 轮 | 20 | 命名空间写错（`ClassExland.Shared.Helpers`）、缺 `using`、`ClassPlan` 命名空间 | bell-dev / duty-dev 已修 |
| 第 2 轮 | 14 | Bell 播放签名、Duty 枚举与变量撞名、Bridge 4 类 | 分派 bell/duty/cli/sys |
| 第 3 轮 | 待测 | — | 已补齐 `tests/ClassExland.Tests`（3 个测试文件） |

## 已知环境限制

- 本机 bash 工具对中文路径会崩（`0xC0000409`），PowerShell 也无法直接拉起原生进程
  （`CreateProcess` 触发 `AccessViolationException`）。
- **构建统一走 WMI**：`([wmiclass]'Win32_Process').Create('"C:\Program Files\dotnet\dotnet.exe" build <sln> ... -flp:logfile=<log>')`，
  再单独读 `build.log`。日志是 GBK，读出来中文会乱码，但行号与错误码可用。

## 工作纪律

- 每次提交/阶段结束追加 `docs/PROGRESS.md`，写清做了什么、验证了什么、下一步是什么。
- 不确定项先问用户，再写 `docs/DECISIONS.md`，不自行假设。
- 不碰个人敏感名单文件；不上传任何真实学生数据。
- **只有 RemoteSync 模块可以写配置文件**，其余模块一律通过它落盘。
