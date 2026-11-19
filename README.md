# ClassExland

ClassIsland 插件：**贴着现有课表长出来的"上课自动化"整合包**——可配置的预备铃时间轴、值日表与下课后展示、特殊活动提前提醒、界面自动切换，外加可选的远程配置同步与运维 CLI。

它不另起炉灶做课表软件，也不改动你的 ClassIsland 配置与已装插件；旧的 DutyList / DutyRoster / 试写作业配置可以导入后停用。

- 插件 ID：`yibianhui.classexland`
- 适用：ClassIsland 2.1.x · .NET 8 · Windows
- 许可：[MIT](LICENSE)

## 功能一览

- **预备铃时间轴**：每节课可单独设置提前量（默认 120 秒）；支持预告提醒、正式上课提示、下课延迟；多种状态实现可选，默认启用 Harmony 全局覆盖（可一键关闭/回滚）。
- **值日表**：表格内联编辑、按日/周/月轮换、主界面单行组件持续显示当前时段名单；下课后延迟弹窗提醒并自动关闭。
- **特殊活动**：内置"试写作业"等预设，开始前 **10 分钟** 提前提示并附带当日值日名单，可逐项关闭。
- **界面自动切换**：预备铃起切紧凑布局、下课延迟后切完整布局；布局名、缩放、透明度均可配置，不覆盖你已有的自动化文件。
- **旧插件迁移**：从 DutyList、DutyRoster、试写作业插件、lrs2187.duty 导入配置（追加不覆盖），导入后停用旧插件。
- **远程配置同步（可选）**：自托管服务器上的"快照 + 指令队列"同步；仅 HTTPS、签名校验、自动备份、失败回滚、可一键停用；断网不影响本地使用。
- **运维 CLI（`cex`）**：只读优先，写命令默认 `--dry-run`、需 `--yes`，高危命令额外 `--confirm`；支持 `--json` 与稳定退出码，覆盖多档案切换与本地 Web 管理页。
- **更多**：铃声文件/音量/试听、今日时间轴诊断面板、临时调课感知、轮换日历预览、值日完成记录、TTS 人名朗读、LuckyPicker 联动、节假日跳过、值日历史导出、多班级档案切换等。

## 安装

1. 到 [Releases](https://github.com/yiwubianjihui/classexland/releases) 下载最新版本的 `ClassExland.cipx`。
2. 在 ClassIsland 的插件管理页面选择"从文件安装"，选中该 `.cipx`；或将其解压到 ClassIsland 数据目录下的 `Plugins/yibianhui.classexland/`。
3. 重启 ClassIsland，在插件列表中确认 **ClassExland** 已启用。

> 从源码构建见下方"从源码构建"。发布包同时提供 `SHA256SUMS`，建议下载后校验完整性。

## 首次配置

1. 打开 ClassExland 设置页，先执行**宿主提醒一键配置**：预览将要修改的时间线条目，确认后再写入；这会把宿主自带的"上课前/上课提醒"关闭，避免与预备铃双重响铃。可随时一键回滚。
2. 在"预备铃"页设置默认提前量（默认 120 秒）；需要**逐节课不同**时，有两个入口，写的是同一份数据：
   - 打开 ClassIsland 的**档案编辑 → 时间表**，选中某个上课时段，右侧「附加设置」里的「预备铃（ClassExland）」直接填秒数；
   - 或用插件设置页「预备铃 → 每节课提前量」表格批量勾选填写。
   两处都立即生效，不必重启。上课/下课状态覆盖与下课延迟（默认 60 秒）也在本页。
3. 在"值日表"页直接录入，或走"导入导出"页从旧插件导入（见 [MIGRATION.md](docs/MIGRATION.md)）。
4. 在"特殊活动"页确认活动与提前提醒（默认提前 10 分钟并附带值日名单）。
5. 在"界面切换"页确认布局名（默认 `Default` / `offclass`）与缩放、透明度。
6. （可选）在设置 →「ClassExland 远程控制」里填入**你自己托管的**服务器地址与令牌，点「保存并测试」即可；不启用则完全离线工作。

配置项字段与示例 JSON 见 [CONFIGURATION.md](docs/CONFIGURATION.md)。改配置后可用 `cex config doctor` 体检。

## 与旧插件的替代关系

ClassExland 定位为 **DutyList / DutyRoster / 试写作业插件的整合替代**：

- 导入这些插件（以及 `lrs2187.duty`）的现有配置后，**在 ClassIsland 插件管理中停用旧插件**，避免重复提醒；旧插件的配置文件**不会被修改或删除**。
- 导入是**追加**，冲突会列出差异由你选择；导入前请备份。
- 导入后建议用 `cex duty show` 与设置页预览核对结果，再决定是否停用旧插件。
- 命令行入口：`cex migrate --from <旧配置目录或文件>` 预览，`cex migrate --from <旧配置目录或文件> --yes --confirm` 实际执行（高危，默认 dry-run）。

详细步骤见 [MIGRATION.md](docs/MIGRATION.md)。

## 远程同步与外部服务器

远程同步是**可选**能力。约定如下：

- **两种用法**：设置页「ClassExland 远程控制」直接填地址与令牌（推荐，带「保存并测试」和中文错误提示）；或直接改 `remote.json`。
- **不想记命令**：在同一页勾选「启用本机网页控制台」，点「打开」即可在浏览器里看状态、切布局、查值日、换档案（仅本机可访问）。

- 服务器由**用户自行托管**，插件代码里**不含任何服务器地址或密钥**；地址写在外部配置 `remote.json`。
- **仅 HTTPS**；令牌与 HMAC 密钥通过环境变量或仅当前用户可读的本机文件提供，不进仓库、不硬编码。
- 没有网络时插件照常本地运行；同步失败只记日志并保留本地配置，不会把你锁在云端。
- 写入前自动备份，校验失败或中途出错会回滚；可随时一键停用，停用后不再发起任何网络请求。
- 同步范围可单独开关：插件配置、ClassIsland 档案、ClassIsland 全局设置。

部署约定、JSON 格式与安全模型见 [REMOTE-SYNC.md](docs/REMOTE-SYNC.md)。

## CLI（`cex`）

`cex` 是独立的运维命令行工具，通过 ClassIsland 官方 IPC 命名管道通信：不开对外 HTTP 端口、不注入 DLL、不需要管理员权限。只读命令直接执行；写命令默认 `--dry-run`，加 `--yes` 才落盘；高危命令还需 `--confirm`。

```bash
cex status                       # 查看当前状态、下一节课、值日概况
cex version                      # 版本信息
cex bell list                    # 按课表列出每节课的 index / id / 有效提前量
cex bell set --index 3 --prep 90            # 默认 dry-run，预览
cex bell set --index 3 --prep 90 --yes      # 实际写入（自动备份 + 审计，立即生效）
cex duty show --date 2026-11-19
cex config doctor                # 配置一致性体检
cex profile list                 # 列出本机档案
cex profile switch --to <档案名> --yes --confirm  # 高危：切换档案
```

完整命令清单（只读 / 写入 / 高危 / 多档案）、安全模型与退出码见 [CLI.md](docs/CLI.md)。

### 本地 Web 管理页

由 `Features/Bridge/LoopbackWebHost.cs` 唯一实现：**仅监听 127.0.0.1**、**所有路由需 Bearer 令牌**、**默认不自动启动**。执行 `cex web start --yes --confirm` 后可用，`cex web stop --yes` 停止，`cex web status` 查看状态。

路由：`GET /`（管理页）、`GET /api/status`、`GET /api/duty`、`GET /api/profiles`、`POST /api/profile/switch`（需 `confirm=yes`，否则返回预览）、`POST /api/sync`（默认真正同步，`dryRun=true` 预览）、`POST /api/command`（透传）。鉴权失败 403、参数缺失 400，其余按 dispatcher 的退出码。

端点发现文件：`%LOCALAPPDATA%\ClassExland\bridge-endpoint.json`；配置目录可用环境变量 `CLASSEXLAND_CONFIG_DIR` 覆盖；审计日志为配置目录下的 `audit.log`。

## 从源码构建

需要 .NET 8 SDK：

```bash
dotnet restore ClassExland.sln
dotnet build ClassExland.sln -c Release
```

构建会在 `src/ClassExland/cipx/` 下生成 `ClassExland.cipx`（同目录的 `checksums.md` 是 SDK 生成的 MD5 摘要，仅本地参考）。CI 会把该包收集为 `cipx/ClassExland.cipx` 并在打 tag 时附上 `SHA256SUMS` 发布。把该包按"安装"一节导入 ClassIsland 即可。

开发与协作规范见 [CONTRIBUTING.md](docs/CONTRIBUTING.md)。

## 文档

- [设计稿](docs/DESIGN.md) · [决策记录](docs/DECISIONS.md) · [进度](docs/PROGRESS.md) · [任务清单](TODO.md)
- [配置说明](docs/CONFIGURATION.md) · [旧插件迁移](docs/MIGRATION.md) · [远程同步](docs/REMOTE-SYNC.md) · [CLI 参考](docs/CLI.md)
- [更新日志](CHANGELOG.md) · [贡献指南](docs/CONTRIBUTING.md)
