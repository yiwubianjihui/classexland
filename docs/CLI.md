# CLI 参考（`cex`）

`cex` 是 ClassExland 的独立运维命令行工具。默认只读；写命令默认 `--dry-run`，需显式 `--yes` 才落盘；高危命令还需额外 `--confirm`。支持脚本化的 JSON 输出与稳定退出码。

## 通信与安全模型

- **不绕开宿主**：在线通道只走 ClassIsland 官方 IPC 命名管道（`IPublicLessonsService` / `IPublicProfileService`）。不开对外 HTTP 端口、不注入 DLL、不需要管理员权限。
- **令牌**：指令桥令牌在插件首次启动时生成到本机用户目录（仅当前用户可读），每次 CLI 调用携带；不落网、不进仓库。远程指令队列与 CLI 共用命令层（`ICommandDispatcher`）。
- **写保护分级**：只读命令无任何写标志；写命令默认 `--dry-run` 预览，加 `--yes` 才执行；高危命令（见下）在 `--yes` 之外还需 `--confirm`。
- **备份 + 原子写**：离线改配置文件前先备份 → schema 校验 → 临时文件 + 原子替换；检测到 ClassIsland 正在运行则**拒绝离线写**。
- **可审计**：所有写操作记入配置目录下的审计日志 `audit.log`，包含时间、命令、目标、差异摘要与结果。
- **端点发现**：CLI / Web 客户端从 `%LOCALAPPDATA%\ClassExland\bridge-endpoint.json` 发现本机指令桥端点。可用环境变量 `CLASSEXLAND_CONFIG_DIR` 覆盖配置目录（用于自定义/便携部署）。

### 命令寻址

命令层使用**点分寻址**（如 `bell.set`、`config.doctor`），CLI 中用**空格分组**书写（如 `cex bell set`、`cex config doctor`）。远程指令队列里的指令名即点分形式，与 CLI 命令一一对应，行为一致（见 [REMOTE-SYNC.md](REMOTE-SYNC.md)）。

### 全局选项

| 选项 | 说明 |
|---|---|
| `--yes` | 确认执行写操作；写命令不带则只做 `--dry-run` 预览、不落盘 |
| `--dry-run` | 预览差异摘要，不做任何写入；写命令**默认即为该模式** |
| `--confirm` | 高危命令的二次确认，必须在 `--yes` 之外额外提供 |
| `--json` | 以稳定 JSON 输出结果，便于脚本解析 |
| `--help` | 显示命令帮助 |

> 具体参数（如 `--slot` / `--prep` / `--from` / `--plan` / `--to` / `--id` 等）以各命令 `cex <命令> --help` 为准。

### 退出码

| 码 | 含义 |
|---|---|
| `0` | 成功 |
| `1` | 校验失败（参数错误、schema 不符等） |
| `2` | 配置被占用（ClassIsland 正在运行，拒绝离线写） |
| `3` | IPC 不可达（宿主未运行或指令桥未就绪） |
| `4` | 被安全策略拒绝（令牌无效、命令不在白名单、签名校验失败、高危命令缺少 `--confirm` 等） |

---

## 命令集

### 只读命令（无写保护要求）

| 命令 | 说明 |
|---|---|
| `cex status` | 当前虚拟/宿主状态、下一节课、剩余秒数、值日概况 |
| `cex version` | 插件与 CLI 版本信息 |
| `cex bell list` | 按课表列出全部上课段：`index`、`id`、起止时间、有效提前量、是否单独覆盖 |
| `cex bell preview` | 预览预备铃/下课时间轴，不修改 |
| `cex duty show` | 查看当前时段/指定日期值日名单 |
| `cex activity list` | 列出特殊活动及提前提醒设置 |
| `cex layout current` | 查看当前生效的界面配置 |
| `cex config get` | 读取指定配置项 |
| `cex config diff` | 显示配置与默认值/备份的差异 |
| `cex config doctor` | 校验时间表/自动化/插件配置一致性，输出问题清单 |
| `cex backup list` | 列出已有备份 |
| `cex audit list` | 查询审计日志 |
| `cex remote status` | 远程同步状态（连接、版本、上次结果） |
| `cex web status` | 本地 Web 管理页运行状态 |

### 写入命令（默认 `--dry-run`，需 `--yes`）

| 命令 | 说明 | 高危 |
|---|---|---|
| `cex bell set` | 设置某节课预备铃提前量（`--index <n>` 或 `--slot <id>` + `--prep <秒>`） | |
| `cex bell import` | 导入铃声配置 | |
| `cex bell export` | 导出铃声配置 | |
| `cex duty edit` | 编辑值日表条目 | |
| `cex duty import` | 导入值日表（追加） | |
| `cex duty export` | 导出值日表 | |
| `cex duty rotate` | 手动切换轮换批次 | |
| `cex duty complete` | 标记值日完成/交接记录 | |
| `cex activity add` | 新增特殊活动 | |
| `cex activity edit` | 编辑特殊活动 | |
| `cex activity test` | 立即试弹一次活动提醒 | |
| `cex layout switch` | 手动切换界面配置（`--to class\|break`） | |
| `cex config set` | 设置配置项 | **是** |
| `cex remote sync` | 立即执行一次远程同步 | |
| `cex remote poll` | 拉取一次清单，检查新版本与本地变更 | |
| `cex remote upload` | 把本机四个配置原文整体上传到服务器（冲突时返回待审提示） | |
| `cex remote enable` | 启用远程同步 | |
| `cex remote disable` | 停用远程同步 | |
| `cex remote server` | 设置远程服务器地址 | **是** |
| `cex backup create` | 创建配置备份 | |
| `cex backup restore` | 从备份恢复配置 | **是** |
| `cex audit export` | 导出审计日志 | |
| `cex migrate` | 从旧插件迁移配置 | **是** |
| `cex web start` | 启动本地 Web 管理页 | **是** |
| `cex web stop` | 停止本地 Web 管理页 | |
| `cex class set` | 设置班级信息 | **是** |

### 高危命令（在 `--yes` 之外额外需要 `--confirm`）

`backup restore`、`migrate`、`remote server`、`config set`、`class set`、`web start`。

```bash
cex backup restore --yes --confirm
cex remote server --url <https地址> --yes --confirm
```

### 多档案命令

| 命令 | 说明 | 写保护 |
|---|---|---|
| `cex profile list` | 列出本机 ClassIsland 档案 | 只读 |
| `cex profile current` | 查看当前档案 | 只读 |
| `cex profile switch --to <档案名>` | 切换当前档案 | **高危**：`--yes` + `--confirm` |

```bash
cex profile list
cex profile current
cex profile switch --to <档案名> --yes --confirm
```

---

## 本地 Web 管理页

Web 管理页由 `Features/Bridge/LoopbackWebHost.cs` **唯一实现**，与 CLI 共用指令桥：

- 仅监听 **127.0.0.1**，不对外网开放；**所有路由都需 Bearer 令牌校验**，令牌放在鉴权头中，**不作为命令参数**。
- **默认不自动启动**；需先执行 `cex web start --yes --confirm`，之后可用 `cex web status` 查看状态，用 `cex web stop --yes` 停止。

路由：

| 方法 | 路由 | 对应命令 | 说明 |
|---|---|---|---|
| GET | `/` | — | 管理页 |
| GET | `/api/status` | `status` | 查询当前状态 |
| GET | `/api/duty` | `duty.show` | 查询当前时段/指定日期值日名单 |
| GET | `/api/profiles` | `profile.list` | 列出档案（只读） |
| POST | `/api/profile/switch` | `profile.switch` | 切换档案；参数 `to` / `name` / `profile` 任一，可放 query 或 JSON body；**需 `confirm=yes` 才真正执行，否则只返回预览** |
| POST | `/api/sync` | `remote.sync` | 立即同步；**默认真正执行**，`dryRun=true` 时只预览 |
| POST | `/api/command` | 透传 | 透传至 `ICommandDispatcher`，与 CLI / 远程指令队列共用命令层 |

响应约定：

- 鉴权失败 → `403`；参数缺失 → `400`；其余情况使用 dispatcher 的 `exitCode`。

> 不要把端点或令牌暴露给局域网或公网。端点可从 `%LOCALAPPDATA%\ClassExland\bridge-endpoint.json` 发现。

---

## 运维示例

### 日常巡检

```bash
cex status                       # 当前状态 + 下一节课 + 值日概况
cex version                      # 版本信息
cex duty show                    # 当前时段值日名单
cex bell preview                 # 预览今日时间轴
cex config doctor                # 配置体检，输出问题清单
cex remote status                # 远程同步状态
cex web status                   # Web 管理页状态
```

### 改某节课的预备铃提前量

```bash
cex bell list                                          # 按课表列出每节课，记下 index
cex bell set --index 3 --prep 90                       # 默认 dry-run，预览差异
cex bell set --index 3 --prep 90 --yes                 # 实际写入（自动备份 + 审计）
cex bell set --index 3 --prep 120 --yes                # 仍写 120 即回到"默认值"
cex bell set --slot "<bell list 里的 id>" --prep 90 --yes   # 课表调整过、序号变了时用 id 更稳
```

- `--prep` 取值 0–600 秒；写入后会**立即生效**：重载内存配置 → 把每节课的值同步进档案附加设置 → 重建当天时间轴，无需重启 ClassIsland。
- 不写 `--yes` 时只预览；`--json` 可拿到 `appliedToProfile` 与备份路径，便于脚本判断。

### 导入旧插件值日表 / 执行迁移

```bash
cex duty import --from "<旧插件配置路径>" --plan "值日表"        # 预览
cex duty import --from "<旧插件配置路径>" --plan "值日表" --yes  # 写入
cex migrate --from "<旧插件配置目录或文件>" --yes --confirm          # 高危：迁移需二次确认
```

### 切换批次 / 界面 / 档案

```bash
cex duty rotate --to 3 --yes
cex layout switch --to class --yes
cex activity test --id trial-homework --yes
cex profile switch --to <档案名> --yes --confirm                 # 高危
```

### 备份与恢复

```bash
cex backup list                  # 列出备份
cex backup create --yes          # 创建备份
cex backup restore --yes --confirm                     # 高危：二次确认后恢复
```

### Web 管理页

```bash
cex web start --yes --confirm    # 高危：启动仅本机可见的管理页
cex web status                   # 查看运行状态
cex web stop --yes               # 停止
```

### 脚本化：只看退出码与 JSON

```bash
#!/usr/bin/env bash
set -euo pipefail

if cex status --json > /tmp/status.json; then
  state=$(python -c 'import json;print(json.load(open("/tmp/status.json"))["virtualState"])')
  echo "当前状态: $state"
else
  rc=$?
  case "$rc" in
    2) echo "ClassIsland 正在运行，拒绝离线写" >&2 ;;
    3) echo "IPC 不可达，宿主可能未启动" >&2 ;;
    4) echo "被安全策略拒绝（令牌/白名单/签名/缺少 --confirm）" >&2 ;;
    *) echo "命令失败，退出码 $rc" >&2 ;;
  esac
  exit "$rc"
fi
```

> 字段说明与默认配置见 [CONFIGURATION.md](CONFIGURATION.md)；旧插件迁移流程见 [MIGRATION.md](MIGRATION.md)。
