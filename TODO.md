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
| 13 | 集成构建与验收 | ✅ | team-lead | `docs/ACCEPTANCE.md`、`tests/ClassExland.Tests/`（0 错 / 39 测试全绿） |
| 14 | 推送到 `yiwubianjihui/classexland` | ✅ | team-lead | `main` 111 文件 / 13641 行，origin 已跟踪 |
| 15 | 实机验收（装到 ClassIsland 2.1.0.1 跑 C 节清单） | ✅ | team-lead | `docs/ACCEPTANCE.md` C 节（1.0.0.1，抓到并修复 3 个运行时缺陷） |
| 16 | 打 tag + Release 资产（cipx + SHA256SUMS） | ✅ | team-lead | `v1.0.0.0` → run `44458193363`，资产已发布并核验 SHA256 |
| 17 | 逐节课预备铃提前量（设置页表格 / CLI `--index` / 远程热生效） | ✅ | team-lead | `Features/Bell/BellSlotCatalog.cs`、`BellSettingsPage.axaml`、`Bridge/CommandDispatcher.Bell.cs`、`Remote/RemoteSettingsPage.axaml` |
| 18 | 部署 1.0.0.2 到实机 | ✅ | team-lead | `data/Plugins/yibianhui.classexland/` 已覆盖（DLL MD5 `26F0FD6D…` 与构建产物一致，manifest 1.0.0.2） |
| 19 | 远程控制设置页（面向普通用户） | ✅ | team-lead | `Remote/RemoteSettingsPage.axaml(.cs)`：状态卡、保存并测试、错误中文化、高级折叠、本机网页控制台、诊断复制 |
| 20 | 实机打开设置页验收 | 🔄 | 用户 + team-lead | 启动 ClassIsland → 设置 → 「ClassExland 远程控制」，确认页面正常渲染、状态卡与按钮可用 |

## 构建收敛记录（Release / net8.0）

| 轮次 | 错误数 | 主要问题 | 处置 |
|---|---|---|---|
| 第 1 轮 | 20 | 命名空间写错（`ClassExland.Shared.Helpers`）、缺 `using`、`ClassPlan` 命名空间 | bell-dev / duty-dev 已修 |
| 第 2 轮 | 14 | Bell 播放签名、Duty 枚举与变量撞名、Bridge 4 类 | 分派 bell/duty/cli/sys |
| 第 3 轮 | 9 | `DutySettingsPage.axaml` 的 `models:` 前缀指向错命名空间 | team-lead 改为 `migration:` |
| 第 4 轮 | 0 | — | 构建成功（9 警告） |

> CI 侧另有一处**非代码**失败：版本校验步骤的中文 `print` 在 Windows runner 上触发
> `UnicodeEncodeError`（cp1252），已由 `PYTHONIOENCODING=utf-8` + `sys.stdout.reconfigure` 修复。

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
