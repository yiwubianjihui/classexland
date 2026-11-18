# duty-dev 进度

> 负责：值日表核心、主界面组件、下课/活动提醒、配置迁移与创意功能。
> 独占目录：`src/ClassExland/Features/Duty/`。

## 2026-11-19

### 已完成
- 读通 DESIGN/DECISIONS/Contracts/Plugin/csproj、TrialHomework 参考插件、4 份旧插件实际配置。
- 建立 `Features/Duty/{Models,Migration,Services,Reminders,Controls,Views/SettingsPages}` 目录。
- **契约**：`DutyRegistration.AddDuty(IServiceCollection)`，注册 DutySettings/ActivitySettings、DutyService 为 IDutyQuery、ActivityService、MigrationService/IDutyMigrationService、设置页、组件、提醒提供方。
- **模型**：DutyScope/RotationMode、TimeSlot、DutyItem、RotationSettings、DutyPlan、DutySettings、DutyComponentSettings、HolidayRange、ActivityGroup、SpecialActivity、ActivitySettings。
- **纯逻辑**：`Migration/RotationCalculator.cs`（跳过日不消耗第 N 天计数，支持 Day/Week/Month/Manual，含日历预览与状态描述）、`Migration/DutyPersonParser.cs`、`Migration/DutyRosterTextParser.cs`。
- **DutyService**：实现 IDutyQuery（CurrentAt/ForDate），按星期+时段+项目筛选，节假日跳过，轮换预览，值日完成勾选，历史 CSV 导出，LuckyPicker 本地随机。
- **ActivityService**：月度/周度/自定义起始轮换、周确认、名单解析、试写作业预设。
- **提醒**：`Reminders/DutyReminderProvider.cs` 订阅 IBellTimeline.MomentReached + ILessonsService.PostMainTimerTicked；ClassEnd 延迟 D 秒（上课则取消）；活动提前 N 分钟并附值日名单；每条显式 Duration、显示完 Cancel()、弹新前关旧；TTS 通过 NotificationContent.IsSpeechEnabled。
- **组件**：单行 `[时段] 项目：姓名`，超长省略，空数据「暂无安排」占位，编辑模式强制可见，不写死 FontSize。
- **设置页**：5 个 Tab（值日表 DataGrid 内联编辑/增删行/撤销/校验/自动保存、时段与显示、特殊活动、导入导出、诊断）。
- **迁移**：DutyList / DutyRoster 文本 / 试写作业 Settings.json / lrs2187 Config.json 导入；追加不覆盖；同名冲突列出差异供选 Keep/Replace/Skip；提供 CLI 反射入口 `IDutyMigrationService.MigrateAsync(from, plan)`。

### 协作与待确认
- 已消息 bell-dev：确认 `IBellTimeline` 的 DI 注册，以及是否由 BellTimeline 发 `ActivityAdvanceNotice`；若未发则由提醒提供方在主计时器上按活动配置检查并用 date+活动ID 去重（当前实现已兼容两种情况）。
- 已适配 cli-dev 使用的 `ClassExland.Features.Duty.Migration.IDutyMigrationService` 反射契约。
- 注意：CLI 直接读写 duty.json 使用 camelCase 路径（`plans`/`rotation.currentIndex`），而 ConfigureFileHelper 默认 PascalCase；若验收发现 CLI 写命令与设置页不同步，需要统一命名策略（非我独占文件，待 team-lead 决策）。
- 环境限制：Bash 与 PowerShell 启动 dotnet 子进程均触发 AccessViolationException，无法本地编译验证；已逐文件人工复核。

### 2026-11-19 CLI 契约对齐（team-lead 追加，最终版）
- `activities.json` 改为**根 JSON 数组**，序列化 camelCase 字段 `id`/`name`/`reminderTimes`/`rotationMode`/`enabled`（用 JsonStringEnumConverter，mode 输出 "Week"/"Month" 等）。`ReminderTimes` 为真值字段；`Periods` 仅保留为 JsonIgnore 兼容别名。全局 `EnableAdvanceNotice`/`AdvanceMinutes` 移到同目录 `activity-options.json`，避免污染数组。兼容旧 `{ "activities": [...] }` 对象格式。
- `duty.json` 真值字段：`plans` + `enabled` + 根级 `activePlanIndex`（不引入 rotation.currentIndex）。`RotationSettings` 内部字段 startDate/periodDays/mode/skipWeekdays 亦 camelCase。
- 新增 `Features/Duty/Activities/IActivityNotifier.cs`，命名空间 `ClassExland.Features.Activity`：`Task TestAsync(string id)` 与 `Task TestAsync(string id, CancellationToken ct)`，实现类 `ActivityNotifier` 已注册为 DI 服务；返回 `Task.FromResult(true)`（运行时 Task&lt;bool&gt;）确保反射拿到非 null Result。
- `IDutyMigrationService` 保留 2 参数重载 + 新增 3 参数重载 `Task<string> MigrateAsync(string from, string plan, CancellationToken ct)`。
- bell-dev 确认：IBellTimeline 已注册 singleton；`ActivityAdvanceNotice` 不由 Bell 发出，我在 PostMainTimerTicked 自检（已实现）；ClassEnd 的 At 已按 T_end+D 算好，我仍在 MomentReached 后按值日 D 延迟弹提醒（符合 team-lead 要求，需留意与 Bell 的 D 叠加问题）。

### 2026-11-19 下课延迟最终裁决（team-lead）
- 以 Bell 为唯一时间真相源，**不叠加**：`BellMomentKind.ClassEnd.At` 已含 T_end + D_bell，duty 收到 MomentReached 后立即弹值日提醒，已移除 `_pendingClassEndAt` 二次延迟。
- `DutySettings.ClassEndDelaySeconds` 保留为兼容旧配置字段（注释说明已由 Bell 统一控制）；设置页改为只读说明文案，不再提供输入框。
- PrepareBell/ClassOnNotice 仍关闭可能显示中的值日提醒（兜底，Bell 已做「下一节预备铃优先」裁剪）。

### 未做（按分工）
- 不改 Plugin.cs / Contracts / Infra / csproj / manifest / docs 其他文件；不提交 git；未跑完整解决方案构建。
