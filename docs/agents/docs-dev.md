# docs-dev 进度日志

> 仅记录文档与 CI/CD 相关进度；不修改其他文件。

## 2026-11-19 · 启动

- 已读：DESIGN.md / DECISIONS.md / PROGRESS.md / TODO.md / README.md / manifest.yml / ClassExland.csproj / Contracts.cs / PluginPaths.cs / Plugin.cs / CLI Program.cs。
- 关键基线：插件 ID `yibianhui.classexland`、入口 `ClassExland.dll`、API `2.0.0.0`、版本 `1.0.0.0`、MIT；仓库 `yiwubianjihui/classexland`。
- 契约确认：配置文件名来自 `Contracts.ConfigFiles`（bell/duty/activities/layout/remote/bridge/classisland-snapshot）；配置目录由 `PluginConfigFolder` 提供。
- 可见差异（待与 team-lead 核实）：DESIGN §6.1 写审计日志为 `cli-audit.log`，而 `PluginPaths.AuditLogPath` 为 `audit.log`；文档中按「配置目录下的审计日志（当前实现为 audit.log）」表述，不写死 DESIGN 的文件名。
- 待办：README / CONFIGURATION / MIGRATION / REMOTE-SYNC / CLI / CHANGELOG×2 / CONTRIBUTING / build.yml。

## 2026-11-19 · 完成

已交付（均在独占文件范围内，未触碰 DESIGN/DECISIONS/PROGRESS/TODO/源码/csproj/manifest/.sln/LICENSE）：

1. `README.md`（重写）：定位、功能列表、Release 安装 `.cipx`、首次配置、旧插件替代关系、源码构建产物、远程同步与 CLI 简介、许可、文档索引。
2. `docs/CONFIGURATION.md`：配置目录说明；`bell.json`（预备铃提前量、下课延迟、Harmony 覆盖、拖堂顺延、铃声）、`duty.json`（值日表、轮换、提醒时长、TTS 开关）、`activities.json`（提前 10 分钟）、`layout.json`（布局名/缩放/透明度）、`remote.json`、`bridge.json` 的字段说明与示例 JSON。
3. `docs/MIGRATION.md`：四类旧插件来源、备份强调、差异预览与追加语义、CLI 导入步骤、停用旧插件与回滚。
4. `docs/REMOTE-SYNC.md`：快照/指令队列 JSON 格式、版本+etag、HMAC 签名、令牌、仅 HTTPS、地址不写进代码/用户自托管、离线可用、失败回滚、可停用；含无真实地址/密钥的 Python 伪例。
5. `docs/CLI.md`：完整命令表、`--yes/--dry-run/--json`、退出码 0–4、安全模型、备份原子写审计、运维示例。
6. `CHANGELOG.md` 与 `docs/CHANGELOG.md`：1.0.0.0 初始条目（docs 版指向根目录权威版本）。
7. `docs/CONTRIBUTING.md`：构建/测试、代码约定（定时真相源、try/catch、单行组件、TryGetService 等）、安全隐私、版本 bump 与发版规则。
8. `.github/workflows/build.yml`：push main + tag v* 触发；dotnet restore/build -c Release；定位并暂存 cipx；Python 校验包内 manifest.yml version == csproj Version；tag/版本一致性提示；上传 artifact；tag 推送生成 SHA256SUMS 并用 softprops/action-gh-release@v2 发布（contents: write）。

验证：PowerShell 确认 10 个交付文件全部存在；build.yml 内容逐行复核（缩进、heredoc 终止符、条件、权限）。本机 Bash/Python 进程启动异常（AccessViolation），未能运行 YAML 解析与构建验证，建议 CI 首次运行时确认。

待 team-lead 核实：审计日志文件名 DESIGN §6.1 为 `cli-audit.log`，而 `PluginPaths.AuditLogPath` 为 `audit.log`；文档按"配置目录下的审计日志（当前实现为 audit.log）"表述，若 CLI 实现另有 `cli-audit.log` 需同步文档。
