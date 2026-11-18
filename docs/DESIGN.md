# ClassExland 模块化设计方案（审核稿）

> 版本：draft-1 · 日期：2026-11-19 · 状态：**待审核，未实现任何代码**  
> 目标仓库：`yiwubianjihui/ClassIsland.Plugin.ClassExland`（建议名，待确认）  
> 插件 ID（建议）：`yibianhui.classexland` · 目标 API：`2.0.0.0` · 运行时：.NET 8 / Avalonia / `.cipx`

---

## 0. 一句话定位

ClassExland 不是另起炉灶的「课表软件」，而是**贴着 ClassIsland 现有配置模型长出来的一个插件**：  
用每节课可配置的预备铃时间轴驱动虚拟课堂状态，用一张可表格编辑的值日表驱动主界面组件与下课提醒，  
再配一个只读优先、写操作需显式确认的 CLI。原有配置与插件生态不动。

---

## 1. 勘察结论（本地实测）

### 1.1 宿主与插件环境

| *项*              | *实测值*                                                                                                                                                                          | *来源*                                         |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------- |
| *ClassIsland 版本* | ***2.1.0.1****（两个更新槽 `app-2.1.0.1-0` / `app-2.1.0.1-1`）*                                                                                                                       | *目录名 + `data/Settings.json: LastAppVersion`* |
| *插件体系*           | *`manifest.yml` + `entranceAssembly` + `.cipx`（zip 包），SDK `ClassIsland.PluginSdk 2.0.0.*`*                                                                                     | *已装 4 个插件的清单 + 本地 TrialHomework 仓库 csproj*   |
| *已安装插件*          | *`cn.classisland.minimax-tts`、`dutylist.jimmyxiao`、`gordon.dutyroster`、`ink.lipoly.ext.extraisland`（配置目录另有 `gordon.randompicker`、`lrs2187.duty`、`ink.lipoly.ext.lychee` 残留配置）* | *`data/Plugins/`、`data/Config/Plugins/`*     |
| *参考插件版本*         | *DutyList 1.2.0.0 / DutyRoster 1.1.0.0 / TrialHomework v1.0.0.4*                                                                                                               | *各 `manifest.yml`*                           |
| *构建工具*           | *`dotnet 8`、`python` 可用；NuGet 仅 nuget.org 源*                                                                                                                                   | *本机 `Get-Command`*                           |

> 🧠 **From Hindsight memory** — 试写作业插件归属组织 `yiwubianjihui`、版本 v1.0.0.4；已与本地仓库 `manifest.yml` / `csproj` 复核一致。

### 1.2 时间表的真实数据结构（预备铃的落点）

`data/Profiles/YBH.json` → `TimeLayouts[<id>].Layouts[]`，每条是一节课或一个课间：

```jsonc
{
  "StartTime": "07:00:00", "EndTime": "07:05:00",
  "TimeType": 0,              // 0=上课段, 1=休息段, 2=特殊点
  "DefaultClassId": "...", "BreakName": "",
  "AttachedObjects": {
    "08f0d9c3-c770-4093-a3d0-02f3d90c24bc": {   // 宿主内置的「上下课提醒」设置
      "ClassPreparingDeltaTime": 60,
      "IsClassOnPreparingNotificationEnabled": false,
      "IsClassOnNotificationEnabled": false,
      "IsClassOffNotificationEnabled": true,
      "ClassOnPreparingText": "准备上课，请回到座位并保持安静……",
      "ClassOnMaskText": "上课", "ClassOffMaskText": "课间休息"
    }
  }
}
```

全局 `data/Settings.json` 里还有 `ClassPrepareNotifySeconds = 60`、`IsClassPrepareNotificationEnabled = true`。  
**结论**：「上课前提醒」目前是**全局 60 秒**，且每条时间线可挂载自己的覆盖值。这正好给我们两个干净的扩展点：

1. 每节课的预备铃提前量 → 写进 `Layouts[i].AttachedObjects[我方 GUID]`（跟随档案、跟随课表、不影响其他档案）。
2. 宿主自带的上课前/上课提醒 → 通过同一条 `AttachedObjects` 里的内置 GUID 显式关闭，避免双重响铃。

### 1.3 界面切换已经由自动化接管

`data/Config/Automations/Default.json` 里已有两条动作集：

- `classisland.lessons.onBreakingTime` → `CurrentComponentConfig = offclass`、`Scale = 1`、`Opacity = 0.7`
- `classisland.lessons.onClass` → `CurrentComponentConfig = Default`、`Scale = 0.8`、`Opacity = 0.5`

触发器支持 `TimeSeconds` 偏移（现有例子是 `-60`）。  
**结论**：「上课小界面 / 下课完整显示」不需要我们重写 UI，只需要**把触发时机换成我们的虚拟状态**，并给下课加一个可配置延迟。方案优先复用官方 Automation 文件，不覆盖用户的自定义动作。

### 1.4 框架硬约束（来自本机经验库，需在实现前复核）

- 组件容器高度写死 `IslandContainerHeight = 40`，**组件必须单行**；超出部分不裁剪而是压到下一行，表现为「文字被遮挡」。
- 组件不要写死 `FontSize`，继承 `MainWindowBodyFontSize`。
- `IAppHost.GetService<T>()` 取不到会抛异常，一律 `TryGetService<T>()`。
- `AddNotificationProvider<T>()` 只注册为 `IHostedService`，不注册 DI 服务本身。
- `NotificationProviderBase.StartAsync/StopAsync` **非虚且空实现**，重写不生效；定时逻辑订阅 `ILessonsService.PostMainTimerTicked`，时间取 `IExactTimeService.GetCurrentLocalDateTime()`。
- `TimeState` 枚举：`None / OnClass / PrepareOnClass / Breaking / AfterSchool`（注意是 `Breaking` 不是 `BreakingTime`）。
- 插件回调抛异常会导致 ClassIsland **自动禁用插件** ⇒ 所有事件处理器 try/catch。

### 1.5 官方 IPC 可作为 CLI 的合法通道

`ClassIsland.Shared.IPC.dll` 暴露了 `IPublicLessonsService`（`CurrentState`、`CurrentTimeLayoutItem`、`NextClassTimeLayoutItem`、`IsLessonConfirmed`、`OnClassLeftTime`、`GetClassPlanByDate`、`IsTimerRunning`）与 `IPublicProfileService`（`Profile`、`SaveProfile`、`CurrentProfilePath`、`CreateTempClassPlan`）。  
底层是 `dotnetCampus.Ipc` 命名管道。**CLI 优先走这条官方通道，不开自己的监听端口。**

---

## 2. 总体架构

```
ClassIsland 宿主（2.1.0.1）
│
├─ TimeLayouts[].Layouts[].AttachedObjects[我方GUID]  ← 每节课预备铃提前量
├─ Automations/Default.json                          ← 小/完整界面切换（复用，不覆盖）
├─ 官方 IPC（IPublicLessonsService / IPublicProfileService）
│
└─ ClassExland.cipx
   ├─ Plugin.cs                       入口 / DI / 设置加载
   ├─ BellModule                      预备铃时间轴 + 虚拟状态机 + 宿主提醒抑制
   ├─ DutyModule                      值日表模型 / 轮换计算 / 表格编辑 / 主界面组件
   ├─ ActivityModule                  轮流特殊时段活动（试写作业等）
   ├─ LayoutModule                    上课/下课界面切换 + 延迟触发
   ├─ BridgeModule                    CLI 指令桥（只接受本机 IPC 管道 + 令牌）
   └─ Infra                           配置持久化 / 迁移 / 诊断 / 安全日志
```

### 模块职责与依赖

| 模块             | 输入                    | 输出                        | 依赖                       | 可独立开关 |
| -------------- | --------------------- | ------------------------- | ------------------------ | ----- |
| Infra          | 配置文件                  | `Settings` 单例、原子写、备份、审计日志 | 无                        | —     |
| BellModule     | 课表 + 每节 `PrepSeconds` | `BellTimeline`（事件流）、虚拟状态  | Infra、宿主 Lessons/Time 服务 | ✅     |
| DutyModule     | 值日表 + 日期时间            | 当前时段名单、下课后提醒              | Infra、BellModule（下课事件）   | ✅     |
| ActivityModule | 特殊活动配置                | 活动名单提醒 / 组件片段             | Infra、BellModule         | ✅     |
| LayoutModule   | 虚拟状态                  | 界面配置切换                    | BellModule、宿主设置服务        | ✅     |
| BridgeModule   | CLI 指令                | 状态查询 / 受控写操作              | 全部（只读优先）                 | ✅     |

**关键设计**：BellModule 是唯一的「时间真相源」。其他模块只订阅它的事件，彼此不互相读时间、不各自起定时器。这避免现有几个插件各自 30 秒轮询、提醒互相打架的问题。

---

## 3. 功能一：预备铃与虚拟课堂状态

### 3.1 时间轴定义

设某节课正式开始 `T`、结束 `T_end`，该节配置的预备铃提前量 `N`（秒，默认 120）：

| 时点            | 事件      | 状态 / 行为                                         |
| ------------- | ------- | ----------------------------------------------- |
| `T − N − 60s` | 预告提醒    | 原「上课前 60 秒提醒」平移到这里；纯提醒，不改状态                     |
| `T − N`       | **预备铃** | 响铃 + 遮罩「预备铃」；**虚拟状态 → OnClass**（组件切小界面、开始静音/隐藏） |
| `T`           | 正式上课    | **仅作提醒**（一行文字），不再触发状态切换                         |
| `T_end + D`   | 下课      | `D` = 可配置延迟（默认 60s）；若期间又进入上课则取消本次下课             |

边界规则（必须实现）：

- `N + 60s` 大于前一课间长度时，预告提醒改为「课间中点」或跳过（可配置）。
- 连续两节课：下一节的预备铃优先级高于上一节的下课延迟，自动取消待触发的下课。
- 无课 / 休息日 / 调课临时课表：全部按当天 `ClassPlan` 实际生效的 `TimeLayout` 计算。
- 应用启动晚于某个时点：只补发**最近一个未过**的提醒（可配置补发窗口，默认 30 分钟）。

### 3.2 两种实现路径（需你拍板）

| 路径                            | 做法                                                   | 优点                 | 代价                              |
| ----------------------------- | ---------------------------------------------------- | ------------------ | ------------------------------- |
| **A. 虚拟状态（默认推荐）**             | 不动宿主 `TimeState`；我方组件、提醒、自动化触发用 `VirtualTimeState`   | 零侵入、不与其他插件抢状态、升级安全 | 其他插件读到的仍是宿主原生状态（在 T 才变 OnClass） |
| **B. Harmony 全局覆盖（可选开关，默认关）** | Patch `ILessonsService` 的状态计算，让宿主在 `T−N` 就进入 OnClass | 全生态统一感知            | 宿主升级易失效；需要随版本回归；有崩溃代价           |

**方案默认走 A**，并提供 `EnableHostStateOverride` 实验开关（明确标注风险、默认 false）。B 只在 A 确实无法满足你的使用场景时启用。

### 3.3 宿主提醒抑制

对用户给出的「原上课前提醒改为预备铃前 60 秒」「正式上课仅作提醒」，实现为：

1. 设置页提供 **「一键配置」**：预览将修改的时间线条目与字段，确认后写入。
2. 把内置 GUID `08f0d9c3-…` 下对应条目的 `IsClassOnPreparingNotificationEnabled` 与 `IsClassOnNotificationEnabled` 置 false；`ClassOff` 保留给宿主。
3. 我方在 `T−N−60s` 与 `T` 各自发一条自有提醒。
4. 提供 **「一键回滚」**：保存修改前快照，恢复原值；不碰其他字段。

> 这条是有写操作的设计，默认只出预览、不自动落盘；CLI 同样需要 `--yes`。

---

## 4. 功能二：值日表

### 4.1 数据模型（兼容现有配置）

```
DutyPlan
├─ Id / Name / Enabled / Color
├─ Scope: Morning | Afternoon | FullDay | Custom
├─ WeekDays: bool[7]                       // 按星期配置（周一…周日）
├─ TimeSlots: [{ Start, End, Label }]      // 上午 / 下午 / 全天由预设自动展开
├─ Rotation: { StartDate, PeriodDays, SkipWeekdays[], Mode: Day|Week|Month }
└─ Items: [{ Project, Persons[], RemindEnabled, Color }]
```

轮换计算单独抽成 `RotationCalculator`，**语义对齐 DutyList 的 `RotationStartDate / RotationPeriodDays / SkipDays`**（跳过日不消耗「第 N 天」计数），这样导入后行为一致。

### 4.2 界面

- **设置页**：`DataGrid` 内联编辑（行内直接改项目/人名/颜色），支持增删行、撤销、实时校验、脏标记与自动保存；Tab 分为「值日表 / 时段与显示 / 特殊活动 / 导入导出 / 诊断」。
- **主界面组件**：严格单行 —— `[时段徽标] 项目：张三、李四 · 项目：王五`，超长省略；空数据显示「暂无安排」占位而不是整块消失；编辑模式强制可见。
- **下课展示**：订阅 BellModule 的 `ClassEnded` 事件 → 延迟 `D` 秒 → `NotificationRequest`（遮罩 5s / 正文 15s，显示完主动 `Cancel()`，先关上一条再弹新条；沿用 TrialHomework 已验证的写法）。大扫除类项目可选整屏样式（参考 DutyRoster）。
- **TTS**：走宿主现有语音引擎（当前装了 MiniMax TTS + Edge TTS）；人名朗读开关默认关。

### 4.3 导入迁移（P0）

| 来源           | 文件                                      | 迁移内容                                               |
| ------------ | --------------------------------------- | -------------------------------------------------- |
| DutyList     | `dutylist.jimmyxiao/duty.json`          | 批次 → DutyPlan，DayConfig → WeekDays/Rotation，时间段与人员 |
| DutyRoster   | `gordon.dutyroster/*.txt`（若有）           | 纯文本时间行 → Items                                     |
| 试写作业         | `yibianhui.trialhomework/Settings.json` | 名单组 → ActivityModule 的月度轮换活动                       |
| lrs2187.duty | `Config.json`                           | 可选，模板 + WorkerDictionary                           |

导入是**追加**不是覆盖；冲突时列出差异让用户选。不修改原插件文件。

### 4.4 轮流特殊时段活动

把 TrialHomework 的「周确认 / 月轮换 / 下午定时提醒」抽象为通用 `SpecialActivity`：

- 每个活动有自己的 `Periods[]`（如 16:25–17:05）、`RotationMode`（Month / Week / CustomStart）、`NamesByWeekDay[7]`。
- 触发走 BellModule 的时间轴，不再单独起计时器。
- 与值日表共用提醒通道和组件渲染器，避免两套 UI。
- 内置一个预设「试写作业」，可直接从现有配置一键导入。

---

## 5. 功能三：自动化与界面切换

1. `LayoutModule` 订阅 `VirtualTimeState`：
   - `PrepareOnClass` / `OnClass`（即 `T−N` 起）→ 应用「上课配置」：紧凑布局 `Default`、`Scale 0.8`、`Opacity 0.5`。
   - `Breaking` / `AfterSchool`（即 `T_end + D` 后）→ 应用「下课配置」：完整布局 `offclass`、`Scale 1`、`Opacity 0.7`。
2. 布局名可配置，默认沿用你现有的 `Default` / `offclass`；**不直接覆写 `Automations/Default.json`**，优先通过宿主设置服务和自动化触发接口完成。若 SDK 未提供触发注册扩展，则提供「生成推荐自动化片段」按钮，由你粘贴进现有动作集。
3. 下课延迟 `D` 默认 60 秒，范围 0–300；拖堂判定：若到了 `T_end + D` 宿主仍处于 OnClass，则顺延 30 秒再判，最多顺延 3 次，之后放弃本次下课事件并记录诊断日志。
4. 首次启动 / 配置变更 / 临时课表：强制重新计算一次，防止状态残留。

---

## 6. 功能四：CLI 工具（`cex`）

### 6.1 设计原则

- **只读优先**：默认所有命令只读；写操作必须显式 `--yes`，并有 `--dry-run` 预览差异。
- **不绕开宿主**：在线通道只用官方 IPC 命名管道；不开 HTTP 端口、不注入 DLL、不需要管理员权限。
- **可审计**：所有写操作记 `%LOCALAPPDATA%\ClassExland\cli-audit.log`（时间、命令、目标、差异摘要、结果）。
- **可脚本化**：`--json` 输出，稳定退出码（0 成功 / 1 校验失败 / 2 配置被占用 / 3 IPC 不可达 / 4 被安全策略拒绝）。
- **配置安全**：离线改文件前先备份 → schema 校验 → 临时文件 + 原子替换；检测到 ClassIsland 正在运行则拒绝离线写。

### 6.2 命令集

| 命令                                            | 说明                       | 通道     | 写保护                |
| --------------------------------------------- | ------------------------ | ------ | ------------------ |
| `cex status`                                  | 当前虚拟/宿主状态、下一节课、剩余秒数、值日概况 | IPC 只读 | —                  |
| `cex bell list`                               | 列出各节课的预备铃提前量             | 文件只读   | —                  |
| `cex bell set --slot <id> --prep 120`         | 设置某节提前量                  | 文件写    | 备份 + `--yes`       |
| `cex duty show [--date]`                      | 查看某日/当前时段值日名单            | 只读     | —                  |
| `cex duty import --from <path> --plan <name>` | 导入值日表                    | 文件写    | 差异预览 + `--yes`     |
| `cex duty rotate --to <n>`                    | 手动切换轮换批次                 | IPC/文件 | `--yes` + 审计       |
| `cex activity test --id <id>`                 | 立即试弹一次活动提醒               | IPC    | 限本机 + 令牌           |
| `cex layout switch --to class\|break`         | 手动切换界面配置                 | IPC    | `--yes`            |
| `cex config doctor`                           | 校验时间表/自动化/插件配置一致性，输出问题清单 | 只读     | —                  |
| `cex backup / restore`                        | 配置备份与恢复                  | 文件     | 恢复需 `--yes` + 二次确认 |

在线指令桥的令牌在插件首次启动时生成到本机用户目录（仅当前用户可读），每次 CLI 调用携带；不落网、不进仓库。

---

## 7. 可扩展创意功能清单


| 优先级    | 功能                   | 用户价值      | 集成影响 | 说明                                   |
| ------ | -------------------- | --------- | ---- | ------------------------------------ |
| **P0** | 现有插件配置迁移向导           | 不用重新录入值日表 | 低    | 与 §4.3 同批交付                          |
| **P0** | 铃声文件 / 音量 / 试听       | 预备铃可辨识    | 低    | 走宿主声音通道，不自带音频解码                      |
| **P0** | 诊断面板（今日时间轴可视化）       | 出问题时能自查   | 低    | 设置页 + `cex config doctor`            |
| **P1** | 临时调课 / 调时感知          | 调课后铃不跟着错  | 中    | 读 `ClassPlan.AttachedObjects`，订阅课表变更 |
| **P1** | 轮换日历预览 + 周确认         | 提前知道下周谁值日 | 中    | 复用 RotationCalculator                |
| **P1** | 值日完成勾选 / 交接记录        | 谁做了可追溯    | 中    | 新增本地 CSV/JSON 记录，不上传                 |
| **P1** | TTS 人名朗读（开关）         | 大屏更醒目     | 低    | 依赖已装 TTS 插件                          |
| **P2** | LuckyPicker 联动随机抽值日生 | 抽签与值日打通   | 中    | 需对方导出接口或本地名单文件，**禁止上传公网**            |
| **P2** | 节假日 / 校历跳过           | 放假不误响铃    | 中    | 本地校历文件，离线可用                          |
| **P2** | Loopback Web 管理页     | 手机/平板改值日表 | 高    | 需鉴权 + 仅监听 127.0.0.1；与 CLI 共用指令桥      |
| **P2** | 值日历史导出 CSV / 打印版     | 期末汇总      | 低    | 纯本地文件                                |
| **P3** | 多班级档案切换              | 一个安装管多个班  | 高    | 依赖宿主 Profile 切换 API                  |
| **P3** | AI 排班建议（负载均衡 / 避让）   | 少操心       | 高    | 纯本地推理或可选云，默认关                        |
| **P3** | 家长通知推送               | 家校同步      | 高    | 合规与隐私成本高，暂不排期                        |



---

## 8. GitHub 开源规范组织

```
ClassIsland.Plugin.ClassExland/
├── .github/workflows/build.yml       # push main + tag v* → 构建、校验 manifest、附 .cipx
├── docs/
│   ├── DESIGN.md                     # 本文件（审核后转正式设计文档）
│   ├── CONFIGURATION.md              # 字段说明与示例
│   └── MIGRATION.md                  # 从三款插件迁移
├── src/ClassExland/                  # 插件主项目
│   ├── Plugin.cs
│   ├── Features/{Bell,Duty,Activity,Layout}/
│   ├── Models/ Services/ Controls/ Views/SettingsPages/
│   ├── manifest.yml  icon.png
│   └── ClassExland.csproj
├── src/ClassExland.Cli/              # CLI 项目（独立可执行，不打包进 cipx）
├── tests/                            # 纯逻辑单测：RotationCalculator / BellTimeline / 迁移器
├── LICENSE (MIT)  README.md  CHANGELOG.md  CONTRIBUTING.md  .gitignore
└── ClassExland.sln
```

发版规则：`.csproj` 的 `<Version>` 与 `manifest.yml` 的 `version` **必须同步 bump**；tag `vX.Y.Z.W` 触发 Release 并附 `.cipx` + `SHA256SUMS`；构建后校验包内 `manifest.yml`。

---

## 9. 实施阶段与验收

| 阶段 | 内容                                      | 验收                                      |
| -- | --------------------------------------- | --------------------------------------- |
| 0  | 审核本方案、确认 §3.2 路径与 §10 问题                | 你确认                                     |
| 1  | 骨架 + Infra + BellModule + CLI 只读命令      | 编译过；时间轴单测覆盖 6 类边界；装到 ClassIsland 不被禁用   |
| 2  | DutyModule + 组件 + 导入迁移 + 下课提醒           | 现有 duty.json 导入结果一致；组件单行不遮挡；提醒只弹一次并自动消失 |
| 3  | ActivityModule + LayoutModule + CLI 写命令 | 小/完整界面按时切换；拖堂延迟可配置；写操作有备份与审计            |
| 4  | 文档 / CI / Release                       | tag 构建出 `.cipx`，包内清单版本一致                |

---

## 10. 待你确认的关键点

1. **预备铃状态实现**：默认走「虚拟状态」（A，零侵入），还是直接要宿主级全局状态覆盖（B，Harmony，风险高）？
2. **与现有插件的关系**：ClassExland 是「整合替代」DutyList / DutyRoster / 试写作业（导入后建议停用），还是「增量补充」（并存，只接管铃与界面切换）？
3. **CLI 主要使用者**：老师现场操控（要简单命令 + 确认提示），还是你做运维/批量配置（要 `--json` + 脚本友好）？两者都要也可以。
4. **仓库命名与归属**：`yiwubianjihui/ClassIsland.Plugin.ClassExland` 是否可用？插件 ID 用 `yibianhui.classexland` 是否可以？
