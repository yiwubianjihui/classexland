# LuckyPicker 联动

把 [LuckyPicker](https://github.com/yiwubianjihui) 抽到的名字，在 ClassIsland 的主界面上**广播**出来。

## 为什么是「读文件」而不是走接口

LuckyPicker 是独立程序（`desktop/` WinForms 版与 `wpf/` 版各一套），
没有 IPC、没有插件接口，也不依赖 ClassIsland 运行。它能稳定对外提供的唯一事实是：

| 文件 | 位置 | 内容 |
|---|---|---|
| 抽选记录 | `%LOCALAPPDATA%\LuckyPicker\history.json` | `[{"time","text","classId"}]`，**新记录插在最前** |
| 学生名单 | `%ProgramData%\LuckyPicker\students.json` | 姓名字典（本模块只读历史，不读名单） |

所以联动做成**只读监听 + 指纹去重**：ClassExland 低频轮询 `history.json`，
发现新记录就用宿主的提醒通道广播。整个过程不改 LuckyPicker 一行代码，
两边谁先启动都可以，LuckyPicker 没装时也完全不报错。

## 配置

**没有设置页**（与远程控制同理：避免课堂上被误点）。编辑插件配置目录下的 `lucky.json`：

```jsonc
{
  "Enabled": false,                  // 总开关，默认关；打开后才会读文件
  "HistoryPath": "",                 // 留空 = %LOCALAPPDATA%\LuckyPicker\history.json
  "PollIntervalSeconds": 2,          // 轮询间隔，1–600 秒
  "MaskDurationSeconds": 5,          // 顶部遮罩显示时长
  "OverlayDurationSeconds": 12,      // 正文显示时长
  "MessageTemplate": "本次抽到：{names}",
  "MaskTitle": "幸运摇人",
  "EnableSpeech": false,             // 是否朗读（走宿主已装的语音引擎）
  "SkipExistingOnStart": true        // 启动时忽略历史，只广播之后的新抽选
}
```

改完不用重启：每轮轮询都会重新读取配置。

## 行为细节

- **首次启动不刷屏**：`SkipExistingOnStart` 默认为真，开机时把已有历史全部标记为"已见"，
  只广播之后新产生的记录。关掉它则会把整本历史按时间正序播一遍（不推荐）。
- **连抽也能拆**：LuckyPicker 的连抽记录形如 `连抽： 张三、李四、王五`，
  广播前会拆出人名再用模板拼接。
- **不会重复播**：用「时间 + 内容 + 班级」指纹去重；历史被清空后指纹集合跟着清空，
  所以清空再抽同一个人也能正常广播。
- **读到写一半不会误播**：LuckyPicker 用整文件覆盖写入，读失败就跳过这一拍。
- **JSON 坏了不会清空状态**：解析失败只记日志并跳过，不会把已见集合清掉导致重复广播。
- **播完自动关**：显式设定时长并主动 `Cancel()`，避免宿主把它重新排队再播一次。

## 命令行

```bash
cex lucky status                 # 是否启用、历史文件是否存在、已见记录数
cex lucky announce --text "张三" --yes   # 手动广播一段文字（试用/临时通知）
```

`lucky.announce` 属于写操作，默认只预览，需要 `--yes` 才真正广播。

## 排错

| 现象 | 排查 |
|---|---|
| 抽了人但 ClassIsland 没反应 | `cex lucky status` 看 `historyExists` 与 `Enabled`；`Enabled` 默认是 `false` |
| 只在 ClassIsland 启动后抽的才播 | 这是 `SkipExistingOnStart` 的预期行为 |
| 广播两遍 | 不该发生；`AddNotificationProvider` 已按项目约定只注册一次 |
| 便携版 LuckyPicker | 它的历史文件不在默认位置，需在 `lucky.json` 里指定 `HistoryPath` |
