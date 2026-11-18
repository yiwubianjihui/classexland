# cli-dev 进度日志

## 2026-11-19 · 任务 #8：全面运维 CLI + 插件侧指令桥

### 产物文件

桥接层 `src/ClassExland/Features/Bridge/`（独占目录）：
- `BridgeRegistration.cs` — `AddCommandBridge()` 扩展方法，注册端点/存储/审计/IPC 探针/dispatcher/管道服务器/Web 页
- `CommandNames.cs` — 命令名常量表 + 写操作/高危操作集合（供远程模块对齐）
- `ExitCodes.cs` — 稳定退出码 0/1/2/3/4 + `ResultEnvelope` 统一信封
- `BridgeProtocol.cs` — 本机命名管道一行一条 JSON 协议 DTO
- `BridgeEndpoint.cs` — 首次启动生成管道名与令牌到 `bridge.json`，并写 `%LOCALAPPDATA%\ClassExland\bridge-endpoint.json` 发现文件
- `FileStore.cs` — 备份 → schema 校验 → 临时文件原子替换；宿主运行时拒绝离线写；文件权限限制为当前用户
- `CommandAudit.cs` — 全命令 JSON Lines 审计，写 `IPluginPaths.AuditLogPath`，敏感参数脱敏
- `HostIpcProbe.cs` — 反射读取官方 IPC（IPublicLessonsService / IPublicProfileService），不在编译期引用 IPC 程序集
- `BridgeServer.cs` — 本机命名管道服务器（CurrentUserOnly + 令牌校验），不监听 TCP
- `LoopbackWebHost.cs` — 仅 127.0.0.1 的管理页，令牌鉴权，与 CLI 共用 dispatcher
- `CommandDispatcher.cs` + `.Query.cs` + `.Write.cs` — `ICommandDispatcher` 实现，安全策略、路由、全部命令处理器

CLI `src/ClassExland.Cli/`（独占目录，未改 csproj）：
- `CliModels.cs` — 本地 JSON 兼容 DTO（不引用插件程序集）
- `BridgeClient.cs` — 命名管道客户端 + 端点发现（--config-dir / CLASSEXLAND_CONFIG_DIR / 默认发现文件）
- `CommandLine.cs` — 参数解析、命令目录、批量行解析、帮助文本
- `Program.cs` — 主入口、--batch 批量执行、--json 输出、退出码映射

### 命令清单（点分寻址）

- 只读：`status`、`version`、`bell list/preview`、`duty show`、`activity list`、`layout current`、`config get/diff/doctor`、`backup list`、`audit list`、`remote status`、`web status`
- 写入（需 `--yes`，默认 `--dry-run`）：`bell set/import/export`、`duty edit/import/export/rotate/complete`、`activity add/edit/test`、`layout switch`、`config set`、`remote sync/poll/enable/disable/server`、`backup create/restore`、`audit export`、`migrate`、`web start/stop`、`class set`
- 高危（额外 `--confirm`）：`backup restore`、`migrate`、`remote server`、`config set`、`class set`、`web start`、`profile switch`

### 追加：多班级档案切换（ team-lead 指派 ）

- 新增 `CommandDispatcher.Profile.cs`，反射探测 `ClassExland.Features.Remote.IProfileService`（CurrentProfile / ListProfiles / SwitchProfileAsync）
- 新增命令：`profile list`、`profile current`（只读）、`profile switch --to <档案名>`（写 + 高危，需 `--yes --confirm`；兼容全局 `--profile` / `--name`）
- `CommandNames.Profile` 常量已加入，`Profile.Switch` 已加入写操作与高危集合；CLI `CommandCatalog` 与帮助文本同步

### 追加：Web 管理页新增路由（ team-lead 指派 )

- 在独占的 `LoopbackWebHost.cs` 补三条路由，复用同一 dispatcher，不新增监听/鉴权：
  - `GET /api/profiles` → profile.list（只读）
  - `POST /api/profile/switch`（query 或 JSON body 带 to/name/profile）→ profile.switch；沿用高危二次确认：confirm=yes 才 apply，否则返回 dry-run 预览
  - `POST /api/sync`（可选 dryRun=true 预览）→ remote.sync
- 新增 `ReadArgsAsync`（query+body 合并，token 不入命令参数）、`FirstNonEmpty`、`IsTruthy` 辅助方法；管理页 HTML 加对应按钮
- 已拒绝 sys-dev 那份重复的 Features/Remote/LoopbackWebHost.cs（由 team-lead 要求其删除），Bridge 版为唯一 Web 页

### 修正：字段对齐 DutyModule 真实模型（集成审查）

- duty.json：duty.rotate 读写改为根级 `activePlanIndex`（原 rotation.currentIndex 已移除）；CurrentRotationIndex() 同步；doctor 校验根级 activePlanIndex 是否在 plans 范围内；rotation 内 startDate/periodDays/mode/skipWeekdays 保持不动
- activities.json：activity.add 写入字段改为 `id`/`name`/`enabled`/`rotationMode`/`reminderTimes`（HH:mm 字符串数组）；CLI 入参名仍保留 `--periods`，新增 ParseReminderTimes（兼容 JSON 数组/逗号分隔→HH:mm）与 NormalizeHhMm；doctor 检测旧字段 periods 并提示
- bell.json：id/prepSeconds/enabled 保持不变（bell-dev 用 JsonPropertyName 对齐）
- 写回结构修复：新增 LoadRootAndArray 保留根对象（activities.json 根含 enableAdvanceNotice/advanceMinutes，bell.json 根为对象），避免把内部数组直接写回导致根字段丢失；bell set/import、activity add/edit 全部改为写回 Root
- 文档：docs/CLI.md、docs/CONFIGURATION.md 含旧字段，按 team-lead 指示不动，仅报告由 docs-dev 修改（复核后 docs-dev 已更新为正确字段）

### 对接 sys-dev 三个服务接口签名

- 重写 TryInvokeAsync：容忍尾部 CancellationToken/可选参数（签名如 Foo(string, CancellationToken = default)）；非泛型 Task 成功完成返回哨兵 true，避免把成功误判为「服务不可用」；void 方法视为成功
- layout.current → ILayoutService.CurrentLayout；layout.switch → SwitchToAsync(target, ct)
- remote.status → IRemoteSyncService.Status（RemoteSyncStatus record 结构化为 JSON）；remote.sync → SyncNowAsync(ct)；remote.poll → PollOnceAsync(ct)
- remote.enable/disable → 优先调 RemoteSyncModule.Enable()/Disable()（sys-dev 指出的方法），失败回退写 remote.json
- profile.list/current/switch → IProfileService 三成员；switch 调用改为只传 name（CancellationToken 由 TryInvokeAsync 自动补）
- 提醒：sys-dev 的 LoopbackWebHost.cs 已覆盖为空占位并从 DI 移除，Bridge 版为唯一 Web 页

### 对齐 bell-dev 的 bell.json 结构

- bell.json 确认为根 JSON 数组，真值字段 `prepSeconds`（不写 `prep` 别名）；bell set 写入已去掉 prep 别名，只写 prepSeconds + id + enabled
- bell set 的 --slot 直接用 bell-dev 稳定键 `{TimeLayoutId:D}|{StartTime}|{EndTime}`，MatchSlot 已含 "id" 键可命中（非数字 id 也支持）
- 全局参数在 bell-options.json，LoadRootAndArray 对根数组直接返回，不会丢字段
- class set：bell-dev 在 BellTimelineService 上注册了 IBellStateController（含 SetVirtualStateAsync 单/双参数重载）；Bridge 的 TryInvokeAsync 已重写支持尾部 CancellationToken，单/双重载都能命中

### 接口签名定稿（sys-dev 集成审查后）

- 三个接口去掉 CancellationToken：SwitchToAsync(string) / SyncNowAsync() / PollOnceAsync() / SwitchProfileAsync(string) 均为零/单参数。TryInvokeAsync 已容忍尾部可选参数，单/双/零参数均能命中，无需改调用参数个数。
- SyncNowAsync/PollOnceAsync 返回 Task&lt;string&gt;（JSON 摘要 {ok,version,etag,message}）：新增 TryParseJsonSummary + ReadBoolKey/ReadStringKey，摘要结构化为 JsonElement，ok=false 时映射校验失败并带消息。
- profile 切换已消除递归：sys-dev 的 ProfileSwitchService 不再回调 ICommandDispatcher，直接设宿主 CurrentProfilePath（含备份/回滚）；Bridge 只做反射调用。
- Status 字段定稿为 PascalCase（Enabled/CanSync/CoreSyncEnabled/LastVersion/LastETag/LastRunAt/Busy/LastOk/LastMessage）：remote.status 序列化改用无命名策略选项，按服务声明原样输出字段名（兼容大小写定稿）；SyncNow/PollOnce 的摘要仍是小写 {ok,version,etag,message} 并照此解析。

### 严重修复：构造期循环依赖（integrity-check 任务 #17 发现）

- 问题：CommandDispatcher 构造依赖 LoopbackWebHost，LoopbackWebHost 又构造依赖 CommandDispatcher，二者均 Singleton；宿主解析 IEnumerable&lt;IHostedService&gt; 时会抛 "circular dependency" 导致插件被禁用。
- 修法（方案 A）：从 CommandDispatcher 构造函数移除 LoopbackWebHost 参数，新增 `private LoopbackWebHost? WebHost => _sp.GetService(typeof(LoopbackWebHost)) as LoopbackWebHost;` 惰性属性；3 处引用（QueryRemoteStatusAsync / WriteWebStartAsync / WriteWebStopAsync）改用 WebHost 并加 null 保护。
- 复核全部 7 个 Bridge 类构造依赖链，确认无环；WebHost 惰性属性在运行时解析，不在构造期触发。
- 另修两处误导注释：Profile.cs 的 SwitchProfileAsync 签名说明（定稿为单参数）、Query.cs 的 Status 序列化说明（不写死大小写）。

### 编译错误修复（team-lead 集成构建反馈，11 处）

1. LoopbackWebHost.cs CS1061：BridgeEndpoint 无 Token；改为构造期取一次 `_token = endpoint.Ensure().Token`，请求比对用 `_token`（避免热路径进 lock）。
2. FileStore.cs CS1061：ZipArchive.CreateEntryFromFile 是扩展方法；文件头加 `using System.IO.Compression;`。
3. FileStore.cs CS0117：File.GetAccessControl/SetAccessControl 在 .NET 8 不存在；改用 FileInfo 的扩展方法（FileSystemAclExtensions）：new FileInfo(path).GetAccessControl()/SetAccessControl(acl)。
4. CommandDispatcher.Write.cs 7×CS0029：WriteBackupRestoreAsync 声明为 async 却用 return Task.FromResult(...)；删掉外壳直接 return 值（含 await CopyToAsync 保留）。
5. 已扫描三个 partial 所有 async 方法：仅上述一处；Query.cs 无 async 方法，Profile.cs 的 async 方法内无 Task.FromResult；非 async 的 Task<CommandResult> 方法保留 Task.FromResult 正确。

### 关键设计决策（已同步 team-lead / sys-dev）

1. `CommandRequest.Command` 用点分小写名，参数放 Args 字典（键去 `--`）；远程指令队列复用同一 dispatcher。
2. CLI 不引用插件主程序集（避免 Avalonia/SDK 依赖且不改 csproj），用 JSON 兼容 DTO 走命名管道；官方 IPC 在桥（插件进程内）侧调用，满足「在线通道优先官方 IPC」。
3. 弱依赖其他模块：通过 `IServiceProvider` / 反射探测 Bell/Duty/Layout/Activity/Remote/Migration 服务，模块未加载时返回明确错误码而非编译期强引用。
4. 离线写一律先备份、schema 校验、原子替换；检测到 ClassIsland 进程则退出码 2 拒绝。

### 待确认 / 风险

- 本机 shell 与 PowerShell 工具进程创建均抛 `AccessViolationException`（连 `pwd` 都无法执行），**无法运行 dotnet 编译验证**。已完成逐文件静态审查并修复若干逻辑缺陷（remote server --clear 分支、反射枚举异常、端点解析类型检查、版本通道判断）。请 team-lead 在集成构建阶段重点编译这两个项目。
- 各模块配置文件结构（bell.json / duty.json / activities.json）由其他 teammate 定义；当前命令采用通用 JSON 节点读写 + 尽力字段提取。若模块暴露了具体服务接口，可把对应命令切换到强类型调用（例如 bell set 的 `prepSeconds` 字段需与 bell-dev 对齐）。
- 反射探测的服务接口名是约定名（如 `IRemoteSyncService`、`IActivityNotifier`、`ILayoutService`、`IBellStateController`、`IDutyMigrationService`），需与对应模块负责人核对；不一致时相关命令会返回「模块未提供接口」而非崩溃。
