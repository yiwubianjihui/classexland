# 决策记录（ClassExland）

> 本文件随项目持续更新，是上下文压缩后恢复现状的第一入口。

## 2026-11-19 首轮决策（用户确认）

| # | 议题 | 决定 | 备注 |
|---|---|---|---|
| 1 | 仓库归属 | **`yiwubianjihui/classexland`** | 用户原输入 `yibianhui/classexland`，经确认改为组织名下，与现有 ClassIsland 插件一致 |
| 2 | 预备铃状态实现 | **Harmony 全局覆盖** | 用户明确选择；默认启用，保留一键关闭与回滚开关 |
| 3 | 与旧插件关系 | **替代** | 导入 DutyList / DutyRoster / 试写作业配置后停用旧插件；不修改旧插件文件 |
| 4 | 下课值日提醒 | **弹窗通知 + 主界面组件** | 下课后延迟触发，弹窗自动关闭；组件持续显示当前时段名单 |
| 5 | 特殊活动提醒 | **开始前 10 分钟提前提示，并附带值日名单** | 所有特殊活动默认启用，可逐项关闭 |
| 6 | CLI 定位 | **运维向** | 全面命令集，`--json` / `--dry-run` / `--yes` / 批量 / 审计 |
| 7 | 远程同步形态 | **快照 + 指令队列，两者都支持** | 快照用于日常配置同步；指令用于临时操控，与 CLI 共用命令层 |
| 8 | 远程同步范围 | **插件配置 + ClassIsland 档案与全局设置** | 高风险能力：必须签名校验、自动备份、失败回滚、审计日志、可一键停用 |
| 9 | 服务器地址 | **不写进代码** | 放外部配置文件；外部用户可自托管服务器 |
| 10 | 创意功能 | **开发除「AI 排班建议」「家长通知推送」外的全部条目** | 含 Web 管理页、LuckyPicker 联动、节假日、CSV、多班级档案等 |
| 11 | 远程改核心配置的安全策略 | **白名单制** | 允许 CurrentComponentConfig/Scale/Opacity/通知开关/ClassPrepareNotifySeconds/课表预备铃 AttachedObjects；禁止 Credentials.json、Management/Policy.json、Debug*、Update*、插件启用状态、主题私有字段；命中禁止键整份拒绝，不做部分应用 |
| 12 | 核心配置写入单一出口 | **只由 RemoteSync 写文件** | Layout 模块通过 `ClassIsland.Services.SettingsService` 反射改内存并 SaveSettings()，或降级为生成推荐自动化片段；不直接写 Settings.json / Automations/Default.json |

## 宿主 API 实测补充

- `ClassIsland.dll` 中确认类型 `ClassIsland.Services.SettingsService`，成员含 `Settings`、`CurrentComponentConfig`、`ComponentConfigs`、`SaveSettings()`。插件未必能直接引用主程序集，优先反射调用。

## 实施约定

- 插件 ID `yibianhui.classexland`，入口程序集 `ClassExland.dll`，API `2.0.0.0`，版本 `1.0.0.0`。
- 命名空间 `ClassExland.*`；共享契约在 `src/ClassExland/Contracts/`，由 team-lead 维护。
- 各功能只写自己的 `Features/<Name>/` 目录，跨模块只依赖共享契约，避免并行冲突。
- 功能注册扩展方法名固定：`AddBell` / `AddDuty` / `AddLayout` / `AddRemoteSync` / `AddCommandBridge`。
- 所有定时器订阅 `ILessonsService.PostMainTimerTicked`，禁止重写非虚的 `StartAsync`。
- 服务器地址/令牌/API Key 一律不写进代码与文档，只在运行时从外部配置读取；用户提供的 GitHub Token 仅在最终推送时使用，不落盘、不进日志。
- 所有事件处理器 try/catch；插件异常会导致 ClassIsland 自动禁用插件。
