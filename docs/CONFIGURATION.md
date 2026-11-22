# 配置说明

本文件说明 ClassExland 的全部配置项、默认值与示例 JSON。

## 配置文件位置与编辑方式

- 配置文件位于 ClassIsland 提供给插件的配置目录（由 `PluginConfigFolder` 提供，通常在 ClassIsland 数据目录下的 `Config/Plugins/yibianhui.classexland/`）。
- 首次启动会自动创建下列文件；目录内还会生成 `Backups/`（备份）、`Cache/`（缓存）和审计日志 `audit.log`。
- **推荐通过插件设置页或 CLI 修改配置**。直接编辑 JSON 时请先完全退出 ClassIsland：CLI 检测到 ClassIsland 正在运行会拒绝离线写入；写入采用"备份 → schema 校验 → 临时文件 + 原子替换"。
- 任何时候都可用 `cex config doctor` 校验时间表、自动化与插件配置的一致性。

| 文件 | 用途 |
|---|---|
| `bell.json` | 每节课的预备铃提前量（根为数组，CLI 直接读写） |
| `bell-options.json` | 预备铃全局选项（默认提前量、下课延迟、预告、铃声、Harmony 开关） |
| `duty.json` | 值日表、组件显示、提醒与 TTS |
| `activities.json` | 特殊活动（根数组） |
| `activity-options.json` | 活动提前提醒的全局选项（提前开关与分钟数） |
| `layout.json` | 上课/下课界面切换 |
| `remote.json` | 远程同步（默认关闭） |
| `bridge.json` | 本机指令桥与令牌位置 |
| `classisland-snapshot.json` | 远程同步使用的宿主配置快照 |

> 字段命名以运行时配置文件为准；以下示例基于 v1.0.0.0 的设计与共享契约。

---

## 1. `bell.json` + `bell-options.json` — 预备铃与课堂状态

配置拆成两个文件，避免 CLI 读写每节课时被全局选项污染：

- **`bell.json`**：根为 **JSON 数组**，一项对应一节课，真值字段是 `prepSeconds`。`cex bell list/set` 直接读写它。
- **`bell-options.json`**：全局参数（对象），字段名 PascalCase。

每节课的提前量最终会写入课表 `TimeLayouts[i].Layouts[i].AttachedObjects[我方GUID]`（跟随档案与课表，不影响其他档案）；`bell.json` 是兜底副本，档案读取失败时仍可用。**档案里的值优先于 bell.json**，所以 CLI / 远程改完必须同步档案——本插件已自动处理（见下）。

三个入口写的是同一份数据，改哪个都一样：

| 入口 | 适合 |
|---|---|
| ClassIsland「档案编辑 → 时间表」选中上课时段 → 附加设置「预备铃（ClassExland）」 | 一边看课表一边改，最直观 |
| 插件设置页「预备铃 → 每节课提前量」表格 | 批量改多节 |
| `cex bell set --index N --prep 90` 或远程指令 `bell.set` | 脚本 / 远程 |

在附加设置里关掉这一项，等同于"这一节没单独配置"，会回退到 `bell.json` 或默认值。

> **只有上课时段（`TimeType = 0`）会响预备铃。** 课间（`TimeType = 1`）与放学前等其他时段的附加设置不参与时间轴计算：
> 时间轴构建时会跳过它们。附加设置界面**不再禁用输入框**——提前量照常填写与保存（宿主的启用开关也完全交给用户），
> 只用一行说明提示这个时段不会响铃。

### bell.json（每节课）

```jsonc
[
  {
    "id": "<slotKey>",        // 稳定键：档案ID|开始HH:mm:ss|结束HH:mm:ss
    "prepSeconds": 120,       // 该节提前量（0–600 秒）
    "enabled": true,
    "slot": "周一课表 · 第 3 节 09:40-10:20",  // 可读标签，仅用于展示
    "subject": "数学"          // 可选
  }
]
```

数组里没有的节次使用 `defaultPrepSeconds`。

### bell-options.json（全局）

```jsonc
{
  "EnableBell": true,
  "DefaultPrepSeconds": 120,          // 默认提前量（秒）
  "ClassEndDelaySeconds": 60,         // 下课延迟 D（秒），0–300
  "PreNoticeLeadSeconds": 60,         // 预告提醒：预备铃前 N 秒（原"上课前 60 秒提醒"平移到此处）
  "PreNoticeOverflow": 0,             // 0=裁剪到课间中点 | 1=跳过预告 | 2=保持原时间
  "MakeupWindowSeconds": 1800,        // 启动补发窗口（秒）
  "EnableHostStateOverride": true,    // Harmony 全局覆盖：预备铃时即进入 OnClass
  "EnableBellSound": true,
  "BellSoundPath": "",                // 铃声文件路径；空表示不播放
  "BellVolume": 0.8                   // 0.0–1.0
}
```

### 逐节课调整的三条路径（效果一致）

1. **设置页**：ClassIsland 设置 → ClassExland 预备铃 → 「每节课提前量」，勾选「单独设置」并填秒数，点「应用每节课设置」。
2. **CLI**：`cex bell list` 查序号 → `cex bell set --index 3 --prep 90 --yes`（写后立即热生效）。
3. **远程**：清单 `commands` 里下发 `{"command":"bell.set","args":{"index":"3","prep":"90"},"apply":true}`，
   或在 `snapshot.pluginConfigs` 里给 `bell.json` 整份数组（同步完成后自动重载并同步档案）。

> 宿主自带的上课前/上课提醒通过"一键配置"写入档案的宿主 GUID 附加对象并可直接回滚，不体现在这两个文件中。

时间线行为（`T` 为正式上课时刻，`N` 为该节提前量，`D` 为下课延迟）：

| 时点 | 事件 | 行为 |
|---|---|---|
| `T − N − 60s` | 预告提醒 | 纯提醒，不改状态 |
| `T − N` | 预备铃 | 响铃 + 遮罩；进入上课状态 |
| `T` | 正式上课 | 仅一行文字提醒，不再切换状态 |
| `T_end + D` | 下课 | 触发值日展示；若期间又进入上课则取消 |

边界：`N + 60s` 大于前一课间时按 `preNoticeOverflow` 取中点或跳过；连续两节课以下一节的预备铃优先，自动取消上一节待触发的下课；无课/休息日/临时调课按当天实际生效的 `ClassPlan` 计算。

---

## 2. `duty.json` — 值日表与提醒

轮换计算在**根级** `rotation`（字段 `startDate` / `periodDays` / `mode` / `skipWeekdays`），当前批次由根级 `activePlanIndex` 指定；`skipWeekdays` 用 `0=周日 … 6=周六`，跳过日不消耗"第 N 天"计数（对齐 DutyList 语义）。

> ⚠️ **字段大小写是混合的**：带 `[JsonPropertyName]` 的字段用 camelCase（`enabled` / `plans` / `rotation` /
> `startDate` / `periodDays` / `mode` / `skipWeekdays` / `activePlanIndex`），其余字段用 **PascalCase**
> （`WeekDays` / `TimeSlots` / `Items` / `Project` / `Persons` / `SlotLabel` / `Weekday` / `Scope` /
> `ClassEndDelaySeconds` …）。宿主用 `ConfigureFileHelper` 的默认序列化选项，不做大小写归一。
>
> 手工编辑、脚本迁移或远程快照都要照这个规则写：把 `WeekDays` 写成 `weekDays`，插件会静默读不到、
> 退回默认值（周一至周五），而且不会报错。判断依据：插件自己保存的 `duty.json` 就是这个混合形态。

> 🕗 **时段名不写具体时间**：界面、主界面徽章、提醒文案都只说「上午 / 下午 / 傍晚 / 晚上」，
> 单个覆盖整天（跨度 ≥ 6 小时）的时段说「全天」。如果旧配置里把开始时间当标签（`07:00`），
> 插件加载时会自动换成粗粒度名字，并把 `Items` 里对应的 `SlotLabel` 一起改掉——
> 时段边界仍然按 `Start` / `End` 判断，换的只是显示用的名字。

```jsonc
{
  "enabled": true,
  "activePlanIndex": 0,              // 当前启用的 plans 下标
  "rotation": {
    "startDate": "2026-09-01",
    "periodDays": 7,
    "mode": "Week",                  // Day | Week | Month | Manual
    "skipWeekdays": []               // 0=周日 … 6=周六
  },
  "plans": [
    {
      "Name": "值日表",
      "Enabled": true,
      "Scope": "FullDay",            // Morning | Afternoon | FullDay | Custom
      "WeekDays": [true, true, true, true, true, false, false], // 周一…周日
      "TimeSlots": [
        // Label 是粗粒度说法（上午 / 下午 / 傍晚 / 晚上 / 全天），不要把 07:30 这种时间当名字
        { "Label": "早读", "Start": "07:30:00", "End": "08:00:00" }
      ],
      "Items": [
        {
          "Project": "扫地",
          "Persons": "张三,李四",    // 分隔文本（非数组）
          "Color": null,
          "IsSweep": false,          // 大扫除类可选整屏样式
          "RemindEnabled": true,     // 是否在提醒中显示该项目
          "SlotLabel": "早读",       // 必须与某个 TimeSlot 的 Label 一致
          "Weekday": -1              // -1=每天；0=周日 … 6=周六
        }
      ]
    }
  ],
  "classEndDelaySeconds": 60,        // 下课延迟 D（0–300）；期间进入上课自动取消本次下课
  "maskDurationSeconds": 5,          // 遮罩显示时长
  "overlayDurationSeconds": 15,      // 正文显示时长；到时主动 Cancel，先关上一条再弹新条
  "enableClassEndReminder": true,
  "enableTtsSpeech": false,          // TTS 人名朗读，默认关（走宿主已装语音引擎）
  "reminderTitle": "值日提醒",
  "reminderTemplate": "{items}",
  "showSlotBadge": true,             // 主界面组件显示时段徽标
  "enableChecklist": false,          // 值日完成勾选 / 交接记录
  "checks": [],
  "enableHolidaySkip": false,        // 节假日跳过（本地校历，离线可用）
  "holidays": [],
  "enableLuckyPicker": false,        // LuckyPicker 联动随机抽值日生
  "luckyPickerNamesFile": ""         // 本地名单文件；禁止上传公网
}
```

主界面组件严格单行（`[时段徽标] 项目：张三、李四 · 项目：王五`），超长省略，字体继承宿主 `MainWindowBodyFontSize`，编辑模式强制可见；空数据显示"暂无安排"占位而不是整块消失。所有数据仅保存在本地。

---

## 3. `activities.json` / `activity-options.json` — 特殊活动

`activities.json` 的 CLI 契约是**根数组**，每项是一条活动；早期对象格式 `{ "activities": [...] }` 仍可读取但写出时统一为数组。提前提醒的全局选项（`EnableAdvanceNotice` 默认 `true`、`AdvanceMinutes` 默认 `10`）存放在独立的 `activity-options.json`（见决策记录 #5）。

每条活动的契约字段为 `id` / `name` / `enabled` / `rotationMode` / `reminderTimes`，其中 `reminderTimes` 是 TimeSpan 序列化得到的字符串数组（形如 `"16:25:00"`）。输入兼容 `HH:mm`（如 `"16:25"`）与 `HH:mm:ss` 两种写法，写出时统一规范化为 `HH:mm:ss`。

`activities.json`：

```jsonc
[
  {
    "id": "trial-homework",
    "name": "试写作业",
    "enabled": true,
    "rotationMode": "Week",          // Month | Week | CustomStart
    "reminderTimes": [ "16:25:00", "17:05:00" ], // TimeSpan -> HH:mm:ss 字符串
    "CustomStartDate": "2026-09-01",
    "ActiveGroupIndex": 0,
    "Groups": [                      // ActivityGroup：按 Monday…Sunday 存名单分隔文本
      {
        "Name": "第1组",
        "Monday": "张三,李四",
        "Tuesday": "王五,赵六",
        "Wednesday": "",
        "Thursday": "",
        "Friday": "",
        "Saturday": "",
        "Sunday": ""
      }
    ],
    "MessageTemplate": "{names}",
    "WeekConfirmed": false,          // 周确认：未确认的周不弹活动提醒
    "ConfirmedWeekKey": "",
    "IncludeDutyNames": true,        // 提醒附带当前值日名单
    "SchoolDaysOnly": true           // 仅上学日提醒
  }
]
```

`activity-options.json`：

```jsonc
{
  "EnableAdvanceNotice": true,
  "AdvanceMinutes": 10               // 活动开始前 N 分钟提醒并附带值日名单
}
```

> `Persons` 与名单文本的分隔符支持 `、`、`，`、`,`、`;`、`；`、`/`、空格、全角空格、换行与制表符；导出 CSV 时人员逐行拆开。活动触发走 BellModule 时间轴，不单独起定时器，与值日表共用提醒通道与组件渲染器。内置"试写作业"预设可从旧插件一键导入。

---

## 4. `layout.json` — 界面切换

布局名可配置，默认沿用你现有的 `Default` / `offclass`。本插件**不直接覆写** `Automations/Default.json`；若 SDK 未提供触发注册扩展，可通过"生成推荐自动化片段"按钮把片段粘贴进现有动作集。

```jsonc
{
  "Enabled": true,
  "ClassLayout": {                   // 预备铃起（PrepareOnClass / OnClass）应用
    "Name": "Default",
    "Scale": 0.8,
    "Opacity": 0.5
  },
  "BreakLayout": {                   // 下课延迟后（Breaking / AfterSchool）应用
    "Name": "offclass",
    "Scale": 1.0,
    "Opacity": 0.7
  },
  "EndDelaySeconds": 60,             // 下课延迟，与 bell.json 的 ClassEndDelaySeconds 保持一致（0–300）
  "Overtime": { "RetryDelaySeconds": 30, "MaxRetries": 3 },
  "GenerateAutomationSnippet": false,
  "RecomputeOnStartup": true         // 首次启动/配置变更/临时课表强制重算，防止状态残留
}
```

---

## 5. `remote.json` — 远程同步（可选，默认关闭）

地址不写进代码；只在这里（或环境变量）配置你自己托管的服务器。详见 [REMOTE-SYNC.md](REMOTE-SYNC.md)。

```jsonc
{
  "Enabled": false,
  "ServerUrl": "",                   // 必须 https:// 开头；空表示未配置
  "ManifestPath": "/remote/manifest.php",
  "UploadPath": "/remote/upload.php",// 留空时按清单路径推导（manifest.php → upload.php）
  "DeviceName": "初一2班",            // 同一服务器按名字区分设备；留空时仅单设备可用
  "BearerToken": "",                 // 管理员给的令牌；不写进代码
  "HmacSecret": "",                  // 可选；配了就对响应原文与上传请求体双向签名
  "FetchTimeoutSeconds": 15,
  "PollIntervalSeconds": 60,         // 周期轮询：先查服务端新版本，再查本地变更并上传
  "CommandPollSeconds": 10,          // 指令单独轮询（最短 5 秒），服务端点「立即下发」后很快生效
  "AutoUpload": true,                // 本地四个配置有变化时自动上传
  "EnableSnapshotSync": true,        // 是否应用服务端下发的插件配置快照
  "EnableCoreConfigSync": false,     // 高风险：课程表 Settings.json / Profiles
  "EnableCommandQueue": true,        // 是否执行服务端下发的指令
  "Loopback": { "Enabled": false, "Port": 18765, "Token": "" }
}
```

字段名大小写按实际 JSON 原样写；`remote.json` 由插件自己的 `RemoteConfigStore` 读写，
不受值日表那套混合大小写规则影响。上次应用的版本、服务端 head 与本地文件指纹记在
`remote-state.json`（自动生成，别手改）。

---

## 6. `bridge.json` — 本机指令桥

CLI 与远程指令队列共用同一命令层。令牌在插件首次启动时生成到本机用户目录（仅当前用户可读），不落网、不进仓库。

```jsonc
{
  "Enabled": true,
  "AllowLoopbackOnly": true,         // 只接受本机 IPC 命名管道；不开 HTTP 端口
  "AllowedCommands": [               // 允许通过指令桥执行的命令白名单
    "status", "bell.list", "bell.set", "duty.show", "duty.import",
    "duty.rotate", "activity.test", "layout.switch", "config.doctor",
    "backup", "restore"
  ],
  "TokenPath": "bridge.token",       // 相对配置目录；实际文件权限仅当前用户可读
  "AuditLogPath": "audit.log"        // 写操作审计日志
}
```
