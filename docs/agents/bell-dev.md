# bell-dev 进度

任务：#3 实现 BellModule 与 Harmony 状态覆盖

## 2026-11-19 · 上下文核对

- 已阅读 DESIGN.md / DECISIONS.md / PROGRESS.md / Contracts.cs / Plugin.cs / ClassExland.csproj。
- 已阅读本地参考：TrialHomework 的 TrialDutyReminderProvider.cs / Plugin.cs / PluginSettings.cs / 设置页；classisland-plugin-dev SKILL.md 与 framework-constraints.md。
- 通过 ClassIsland 2.1.0.1 官方源码（raw.githubusercontent）逐个核对了 API 签名：
  - `ILessonsService`（含 `CurrentClassPlan`、`PostMainTimerTicked`、`GetClassPlanByDate`）
  - `LessonsService.ProcessLessons()`（状态计算与 `CurrentState` 赋值点，Harmony patch 目标）
  - `TimeState` 位于 `ClassIsland.Shared.Enums`，成员为 `None/OnClass/PrepareOnClass/Breaking/AfterSchool`
  - `TimeLayoutItem`（`StartTime`/`EndTime`/`TimeType`，无 Id 属性）、`TimeLayout`、`ClassPlan`
  - `AttachableSettingsObject.AttachedObjects` 为 `Dictionary<Guid, object?>`，提供 `GetAttachedObject<T>` / `WriteAttachedObject<T>`
  - `IProfileService.Profile` / `SaveProfile()` / `CurrentProfilePath`
  - `NotificationProviderBase.ShowNotification` 非虚 StartAsync；`NotificationContent.CreateTwoIconsMask` / `CreateSimpleTextContent`
  - `IExactTimeService.GetCurrentLocalDateTime()` 返回 `DateTime`
  - `IAudioService.PlayAudioAsync(string, float, CancellationToken?)`（宿主声音通道，不自带解码）
- 环境限制：本会话进程启动异常（dotnet / bash 命令均以 0xC0000005 崩溃），无法执行编译或单测；代码按已核对签名人工静态审查，未运行构建。

## 2026-11-19 · 实现完成

产出目录：`src/ClassExland/Features/Bell/`

| 文件 | 职责 |
| --- | --- |
| `BellConstants.cs` | 固定 GUID：我方附加设置 `A7E3C1B4-6D52-4F08-9C31-8B0E5A2D7F64`（代码注释已写明）、宿主内置提醒 `08f0d9c3-c770-4093-a3d0-02f3d90c24bc`、提醒提供方与 Harmony id、设置页 id |
| `BellSettings.cs` | bell.json 配置（默认 N=120、下课延迟 60、预告 60、补发窗口 1800、EnableHostStateOverride 默认 true、铃声/音量、每节课 PrepOverrides 兜底副本）+ 预览/回滚记录类型 |
| `BellTimelineCalculator.cs` | 纯逻辑时间轴计算器（不依赖宿主类型）：四类事件、预告溢出策略（裁剪到中点/跳过/保持）、连续两节课「下一节预备铃优先」、补发窗口查找 |
| `BellProfileWriter.cs` | 档案写入：BuildPreview（不落盘）→ Apply（写我方 GUID + 宿主两个布尔字段 + bell.json 兜底）→ Rollback（精确恢复对象引用），SaveProfile 持久化 |
| `BellSoundService.cs` | 经 `IAppHost.TryGetService<IAudioService>()` 播放铃声/试听，缺失时静音降级，无自带解码 |
| `BellTimelineService.cs` | `IBellTimeline` + `IHostedService`；订阅 `PostMainTimerTicked`；按当天 ClassPlan 重建时间轴；虚拟状态机；逐处理器 try/catch 的事件广播；临时调课签名（含 ClassPlan.AttachedObjects 键与 IsChangedClass）；`GetHostOverride()` 供 Harmony |
| `HarmonyPatcher.cs` | patch `LessonsService.ProcessLessons` 的 postfix，将 `CurrentState` 同步为虚拟状态；幂等、UnpatchAll 回滚、失败降级为虚拟状态 |
| `BellNotificationProvider.cs` | 预告/预备铃/上课提醒；Mask/Overlay 显式 Duration；显示完主动 Cancel、弹新前关旧；全部事件 try/catch |
| `BellRegistration.cs` | `AddBell()`：注册 BellSettings / ProfileWriter / Sound / Timeline（同时 IBellTimeline 与 IHostedService）/ HarmonyPatcher / 提醒提供方 / 设置页 |
| `BellSettingsPage.axaml(.cs)` | 设置页：全局参数、Harmony 开关与状态、铃声试听、档案预览/写入/回滚 |

## 关键实现取舍

- `TimeLayoutItem` 没有 Id，槽位稳定键使用 `{TimeLayoutId:D}|{StartTime:hh:mm:ss}|{EndTime:hh:mm:ss}`，同时作为 `BellMoment.TimeLayoutItemId`。
- Harmony 以 postfix 形式挂到 `ProcessLessons`（私有实例方法，运行时从 DI 实例 `GetType()` 反射发现），postfix 仅声明 `object __instance`，用反射读写 `CurrentState`，避免编译期依赖宿主具体类型；不改变宿主事件触发时机（界面切换由 LayoutModule 订阅我方 `VirtualStateChanged`）。
- 同一 `BellTimelineService` 实例同时暴露为 `IBellTimeline` 与 `IHostedService`，避免重复定时器。
- `AddNotificationProvider<T>` 仅登记 IHostedService；设置页不通过 `IAppHost.GetService` 解析它。
- 未改动任何共享文件（Plugin.cs / Contracts / Infra / csproj / manifest / docs 决策与进度 / TODO / 其他 Features）。

## 2026-11-19 · 与 CLI Bridge / duty-dev 对齐后的修订

- **bell.json 改为 CLI 契约格式**：根为 JSON 数组，每项 `{ id, prepSeconds, enabled, slot?, subject? }`；`prepSeconds` 为真值字段，不写 `prep` 别名（CLI 自行写）。全局参数移到同目录 `bell-options.json`，避免 CLI 覆盖。`BellSettings.GetPrep/SetPrep/RemovePrep/HasPrepOverride` 取代原 PrepOverrides 字典；兼容读取早期对象格式（slots 数组 / PrepOverrides 字典）。
- **状态强制入口**：新增 `IBellStateController`（本模块自有，不改共享 Contracts）；`BellTimelineService` 实现它并提供 `public Task SetVirtualStateAsync(VirtualTimeState state)` 与 `(state, CancellationToken ct)` 两个重载。单参数重载是必须的——Bridge 的 `TryInvokeAsync` 只传 1 个参数（`ps.Length != args.Length` 会跳过双参数方法）；双参数重载满足 team-lead 指定签名。已注册为 DI 服务。传 `None` 表示清除强制状态、恢复时间轴真实状态。
- `VirtualState` 改为带 `_forcedState` 的读属性；时间轴正常推进仍维护 `_virtualState`，强制状态优先。全部状态读写在 `_stateLock` 内。
- 与 duty-dev 对齐：IBellTimeline 已注册为 singleton；BellTimeline 不发出 `ActivityAdvanceNotice`（activities.json 归 duty-dev），其自行在 PostMainTimerTicked 做 10 分钟提前检查；ClassEnd 的 pending 取消由双方双保险。

## 待验证

- 因环境无法启动 dotnet，未做编译与单测。建议集成阶段在可构建环境执行：
  1. 单测 `BellTimelineCalculator` 六类边界；
  2. 整包构建后确认 Harmony postfix 在 2.1.0.1 上成功 patch（设置页显示「已启用」）；
  3. 档案预览/写入/回滚不与用户其他附加设置冲突。
