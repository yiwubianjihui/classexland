# sys-dev 进度

## 2026-11-19 · LayoutModule（任务 #6）

- 新增 `Features/Layout/`：
  - `LayoutConfig.cs`：默认沿用现有行为（上课 Default/0.8/0.5，下课 offclass/1/0.7），含范围钳制与独立 layout.json 持久化，不碰 Automations/Default.json。
  - `HostLayoutApplier.cs`：通过 `IAppHost.TryGetService` 反射定位宿主设置服务，内存态设置 `CurrentComponentConfig`/`Scale`/`Opacity`；找不到服务时返回失败并提示使用推荐片段；不写任何文件。
  - `RecommendedAutomation.cs`：纯函数 `Generate(config)` 产出推荐自动化动作集 JSON（大小写/数值均来自配置），供设置页展示粘贴；`Description(config)` 提供说明文本。无 IO、无副作用。
  - `LayoutModule.cs`：订阅 `IBellTimeline.MomentReached` / `VirtualStateChanged`（时间源由 BellModule 驱动自 `PostMainTimerTicked`，本模块不自己起定时器、不重写 StartAsync）。PrepareBell/ClassOnNotice 即时切上课；ClassEnd 延迟 D 后切下课，到点仍 OnClass 则按 30s 顺延最多 3 次，超时放弃并记日志；新课预备铃取消待触发下课；首次启动重算；全 handler try/catch。
  - `LayoutRegistration.cs`：`AddLayout()` 注册上述服务 + `IHostedService`。

## 2026-11-19 · RemoteSync（任务 #7）

- 新增 `Features/Remote/`：
  - `RemoteConfig.cs`：server/token/hmac 全部来自外部 remote.json，默认整体关闭且不含地址；`CanSync()` 强制 https + token 齐全。
  - `RemoteModels.cs` / `RemoteStateStore.cs` / `RemoteAuditLogger.cs`：清单模型、版本/etag 去重、审计日志。
  - `SafeConfigWriter.cs`：整份备份（单文件 + 配置目录）、JSON 校验、临时文件原子替换、失败整批回滚；哈希工具。
  - `CorePaths.cs` / `CoreConfigWriter.cs`：从插件配置目录向上定位 ClassIsland data 目录；核心配置（Settings.json + Profiles）应用走备份-校验-原子写；哈希不符判冲突→保留本地并记录；失败整批回滚；档案名做路径穿越校验。
  - `RemoteSyncModule.cs`：启动时仅后台任务 + 超时拉取（无网络不阻塞、不禁用插件）；HTTPS + Bearer + 可选 HMAC-SHA256 常量时间比较；版本/etag 去重；整份备份；插件快照 + ClassIsland 核心配置 + 指令队列（经 ICommandDispatcher）；失败自动回滚；`remote.disabled` 一键停用。
  - `LoopbackWebHost.cs`：仅绑定 `http://127.0.0.1:{port}/`，Bearer 令牌鉴权，路由 /api/status、/api/profiles、/api/command（复用 ICommandDispatcher）、/api/profile/switch、/api/sync；绝不绑定 0.0.0.0；默认关闭；每请求独立 try/catch。
  - `ProfileSwitchService.cs`：列出档案并经命令层切换。
  - `RemoteRegistration.cs`：`AddRemoteSync()`。
  - 未注册任何 ICommandDispatcher（由 Bridge 负责），改用 IServiceProvider 惰性解析并在缺失时安全降级，避免与 AddCommandBridge 的注册顺序冲突。

## 待确认 / 风险

- 已向 team-lead 提问：(1) 宿主是否有明确布局设置 API（我采用反射 + 推荐片段兜底）；(2) 运行中改核心配置的默认策略（我采用备份-校验-原子写-冲突保留本地 + 默认关闭核心同步开关）。等待回复期间按安全默认推进。
- 本机无法启动 dotnet 进程（CreateProcess AccessViolationException），未能编译验证；已人工复核语法。请集成阶段（任务 #13）重点编译本目录。

## 本机真实配置兼容

- 仅读取/追加自己的配置文件（layout.json、remote.json、remote-state.json、layout.log、audit.log、Backups/、remote.disabled）。
- 绝不覆盖 `data/Config/Automations/Default.json`；用户现有 onBreakingTime(onBreakingTime→offclass/1/0.7)、onClass(Default/0.8/0.5) 原样保留。

## 2026-11-19 · 按 team-lead 契约对齐（第二轮）

- **移除重复 Web 服务**：我最初写了 `Features/Remote/LoopbackWebHost.cs`，cli-dev 已在 `Features/Bridge/LoopbackWebHost.cs` 实现。因当前环境无法执行文件删除命令，已把该文件覆盖为纯注释占位（编译为空），并从 `AddRemoteSync()` 中移除注册；不会有两个 HttpListener 抢同一回环端口。已通报 team-lead。
- **新增三个 DI 服务契约**：
  - Layout：`ILayoutService { string CurrentLayout { get; } Task SwitchToAsync(string target, CancellationToken ct = default); }` + `LayoutService`（target = class/break）；已注册。LayoutModule 改为经 ILayoutService 切换，状态单一来源。
  - Remote：`IRemoteSyncService { object Status { get; } Task SyncNowAsync(CancellationToken); Task PollOnceAsync(CancellationToken); }` + `RemoteSyncService`（状态快照含 Enabled/CanSync/CoreSyncEnabled/LastVersion/LastETag/LastRunAt/Busy/LastOk/LastMessage）；已注册。RemoteSyncModule 新增 `PollAsync()`（只查版本/etag 不应用）。
  - Profile：`IProfileService { string CurrentProfile { get; } IReadOnlyList<string> ListProfiles(); Task<bool> SwitchProfileAsync(string profileName, CancellationToken ct); }` + `ProfileSwitchService`；已注册。切换通过反射弱依赖宿主官方 `IPublicProfileService.CurrentProfilePath`（已从 dll 确认存在 setter）设置，绝不改 Settings.json 的 SelectedProfile。
- **Layout 宿主 API 修正**：按 team-lead 确认的类型 `ClassIsland.Services.SettingsService`，HostLayoutApplier 改为按全名反射定位该服务，设置 CurrentComponentConfig/Scale/Opacity（服务本身或 Settings 子对象），最后调用其 `SaveSettings()`；找不到时降级推荐自动化片段。仍不直接写任何核心文件。
- **远程核心配置安全策略**：
  - 新增 `CoreConfigPolicy.cs`，含白名单/黑名单常量表 + `Validate(manifest)`（禁止文件/键/指令命中即整份拒绝）+ `MergeAllowed(local, remote)`（只合并白名单顶层键）。已在 SyncOnceAsync 中于 HMAC 之后、去重之前执行 Validate。
  - `CoreConfigWriter` 对 Settings.json 先试 `SettingsServiceBridge`（反射 + SaveSettings），失败才回退文件原子写并记特殊审计事件 `settings-via-file-fallback`；回退时只 MergeAllowed 白名单键。
  - 默认值遵循文档：插件配置默认开（EnableSnapshotSync=true），档案/全局设置默认关（EnableCoreConfigSync=false），总开关 Enabled=false 且无地址/令牌时完全离线可用。
- 白名单/黑名单常量表已发 team-lead，由 docs-dev 写入 docs/REMOTE-SYNC.md（我未改该文档）。
- 仍未编译验证：本机 dotnet / 部分 PowerShell 命令触发 CreateProcess AccessViolationException；已人工复核。
- 已知偏差（已通报 team-lead）：档案（Profiles/*.json）目前仍走「备份→校验→原子写+冲突保留本地」，未改为 IPublicProfileService.SaveProfile——该接口只保存当前内存档案、不提供按文件加载并改写指定档案的路径，强行套用风险更高。Settings.json 已优先走 SettingsService 反射+SaveSettings。若需档案也走服务，请提供官方按名加载/保存的 API。

## 2026-11-19 · 集成审查修复（第三轮）

- **接口签名改为 Bridge 反射契约（零/单参数，去掉 CancellationToken）**：
  - `ILayoutService`：`string CurrentLayout`、`Task SwitchToAsync(string target)`（1 参数）。由 `LayoutModule` 直接实现，`AddSingleton<LayoutModule>()` + `IHostedService`/`ILayoutService` 同实例映射。删除旧 `LayoutService.cs`。
  - `IRemoteSyncService`：`object? Status`、`Task<string> SyncNowAsync()`、`Task<string> PollOnceAsync()`（零参数，返回 JSON 摘要）。由 `RemoteSyncModule` 直接实现并同实例映射。删除旧 `RemoteSyncService.cs`。
  - `IProfileService`：`string CurrentProfile`、`IReadOnlyList<string> ListProfiles()`、`Task<bool> SwitchProfileAsync(string profileName)`（1 参数）。由 `ProfileSwitchService` 实现，`AddSingleton<IProfileService, ProfileSwitchService>()`。
- **档案切换死循环已消除**：`ProfileSwitchService` 不再调用 ICommandDispatcher，改为直接通过反射设置宿主官方 `IPublicProfileService.CurrentProfilePath`；增加路径穿越校验、切换前整份备份目标档案、失败时恢复原 CurrentProfilePath 并用备份回滚档案文件、全程审计。命令层只负责调用本服务。
- **重复 Web 页已彻底删除**：`[System.IO.File]::Delete` 成功删除 `Features/Remote/LoopbackWebHost.cs`（此前 PowerShell Remove-Item 因环境异常失败），并从 RemoteRegistration 移除注册。Bridge 版为唯一 Web 页。
- Layout 宿主 API 保持「反射 + 推荐片段兜底」；核心配置默认关闭保持不变（用户已确认的安全默认值）。
- 本机仍无法启动 dotnet 进程，未编译验证；已人工复核接口签名与注册映射。

## 2026-11-19 · IAppHost 静态/实例分歧修复（第四轮，integrity-check 提示）

- 项目里 bell-dev / duty-dev 用静态型 `IAppHost.TryGetService<T>()`，我此前用实例型 `_host.TryGetService<T>()` 且反射只查 Instance 方法。若宿主真实 API 为静态类（证据：`IAppHost.Host` / `CoreVersion` 为字段、另两位同事均用静态调用），我的写法会编译失败或降级。
- 修复：在 Layout 与 Remote 各新增 `HostServiceResolver.cs`，用 `BindingFlags.Public | Static | Instance` 查找泛型 `TryGetService`，静态方法以 `null` 为 target 调用，实例方法则从 `IAppHost.Host` 静态字段取实例调用；对外暴露 `TryGet<T>()` / `TryGet(Type)`，同时兼容 out 参数与无参两种签名。
- 移除所有 `IAppHost` 类型的字段/构造参数/DI 解析：LayoutModule、HostLayoutApplier、ProfileSwitchService、SettingsServiceBridge、CoreConfigWriter 均不再注入 IAppHost；`LayoutRegistration` 改为直接 `AddSingleton<ILayoutApplier, HostLayoutApplier>()`。
- 另外：Status 字段已是 PascalCase（integrity-check 看到的是旧版本快照），已回复确认。
