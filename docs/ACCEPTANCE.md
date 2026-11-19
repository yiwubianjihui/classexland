# 验收清单（ClassExland 1.0.0.0）

> 状态：**A / B / D 已完成**；**C 已在真机（ClassIsland 2.1.0.1）上开跑**，首轮暴露并修复 3 个问题（v1.0.0.1），
> 铃声三连提醒、值日弹窗、拖堂顺延、设置页目视检查仍需在课节时段确认。
>
> 环境说明：本机会话的 bash 工具对中文路径会崩（`0xC0000409`），PowerShell 直启原生进程会抛
> `AccessViolationException`。构建与 git 操作改由 **WMI `Win32_Process.Create`** 发起，已跑通。

## A. 静态核对

- [x] 每个 Feature 目录都提供约定的注册扩展：`AddBell` / `AddDuty` / `AddLayout` / `AddRemoteSync` / `AddCommandBridge`。
- [x] `Plugin.cs` 调用了全部 5 个注册扩展，且只依赖 `Contracts` / `Infra`。
- [x] `manifest.yml` 的 `version` 与 `ClassExland.csproj` 的 `<Version>` 一致（1.0.0.0）。
- [x] 没有任何硬编码的服务器地址、令牌、密钥、真实学生名单（全仓 grep 过 `ghp_`、`http://` 常量、学生姓名）。
- [x] 所有事件处理器有 try/catch；所有定时器订阅 `PostMainTimerTicked`。
- [x] 主界面组件单行、无显式 Height、无写死 FontSize。
- [x] `IAppHost` 服务获取统一走 `TryGetService<T>()`（已对官方源码核实：C# 8 静态接口成员）。

## B. 构建

- [x] `dotnet restore ClassExland.sln` 成功。
- [x] `dotnet build -c Release` 成功：**0 错误 / 9 警告**（警告为 CS1998 空异步、CS0618 过时对话框、
      CS8604 可空实参，均不影响功能）。
- [x] `dotnet test` 通过：**39/39**，覆盖 `BellTimelineCalculator`（14）、`RotationCalculator`（15）、
      `DutyPersonParser`（10）。
- [x] `cipx/ClassExland.cipx` 生成（243 KB），包内清单为 `manifest.yml` + `ClassExland.dll` + `icon.png`
      + `README.md` + `deps/runtimeconfig`，版本与 csproj 一致。

### 构建收敛过程（Release 实测，非静态推测）

| 轮次 | 错误数 | 根因 | 处置 |
|---|---|---|---|
| 1 | 20 | `ClassExland.Shared.Helpers` 命名空间写错、3 个模型缺 `using System.Collections.ObjectModel`、`ClassPlan` 缺 `ClassIsland.Shared.Models.Profile` | 各模块自修 |
| 2 | 14 | Bell 播放签名传 string（实际为 `PlayAudioAsync(Stream, float, CancellationToken?)`）；Duty 枚举无 `CustomStart`、局部变量 `start` 撞名；Bridge 4 类（`BridgeEndpoint.Token` 不存在、`CreateEntryFromFile` 缺 using、`File.GetAccessControl` 是 .NET Framework API、async 方法里误用 `Task.FromResult`） | 按宿主 XML 文档与 net8.0 参考包逐一核实后修 |
| 3 | 9 | `DutySettingsPage.axaml` 用 `models:` 前缀引用 `MigrationConflict` / `MigrationConflictChoice`，但二者实际在 `...Duty.Migration` | 增加 `xmlns:migration` 前缀 |
| 4 | 0 | — | 构建成功 |

> 单测首跑 13 个失败，原因是插件工程对宿主 SDK 用了 `ExcludeAssets="runtime"`，
> 运行时资产不进测试输出目录，`RotationSettings : ObservableRecipient` 无法实例化。
> 测试工程单独引入 `CommunityToolkit.Mvvm` 后 39/39 全绿。

## C. 运行时（安装到 ClassIsland 2.1.0.1）

### C.0 加载前置核对（只读，已做）

发布包**不携带**宿主已有的程序集（`PluginSdk` 与 `Lib.Harmony` 均设了 `ExcludeAssets="runtime;native"`），
因此必须逐项确认宿主能提供。对 `app-2.1.0.1-1` 实测：

| 插件引用的程序集 | 宿主是否提供 | 版本 |
|---|---|---|
| `ClassIsland.Core` | ✅ | 宿主 2.1.0.1（编译期 2.0.0.2，更高版本可前滚） |
| `ClassIsland.Shared` | ✅ | 2.1.0.1 |
| `0Harmony` | ✅ | 宿主 2.4.2（编译期 Lib.Harmony 2.3.3，更高版本可前滚） |
| `Avalonia.Base` | ✅ | 宿主 11.3.17（编译期 11.3.6） |
| `CommunityToolkit.Mvvm` | ✅ | 8.2.1.1 |

> `ClassIsland.PluginSdk.dll` 在宿主目录中**不存在**，但它只是元包，插件程序集里也没有对它的引用
> （已对 `ClassExland.dll` 的字符串堆核实），不构成缺失依赖。
>
> 结论：加载期不会因缺程序集失败。以下仍需实机确认。

- [x] **安装后插件未自动禁用**：日志 `此次会话已加载插件: …,ClassExland(yibianhui.classexland,1.0.0.1)`；
      同时注册了「ClassExland 预备铃」与「ClassExland 值日提醒」两个提醒提供方。
- [ ] 设置页各 Tab 正常显示（未逐页目视确认）。
- [ ] 每节课提前量可编辑；`T−N−60s` / `T−N` / `T` 三条提醒各出现一次。
- [ ] Harmony 覆盖使宿主在 `T−N` 进入 OnClass；关闭开关后回落虚拟状态且不崩溃。
      （日志已确认 patch 生效：`宿主状态覆盖已启用（patch ProcessLessons）`，但未到预备铃时刻验证效果。）
- [ ] 下课延迟 D 后弹出值日提醒，约 17 秒后自动消失，不重复排队。
- [ ] 特殊活动提前 10 分钟提醒，且附带值日名单。
- [x] **上课小界面 / 下课完整界面按状态切换**：`[Break] 已应用 component=offclass scale=1 opacity=0.7`
      且 `Settings.json` 同步落盘（mtime 与写入时刻一致）。拖堂顺延仍未验证。
- [ ] 值日组件在编辑模式可选中；空数据显示「暂无安排」。
- [x] **`cex status` / `config doctor` 正常**：`version` / `status` / `config doctor` / `duty show` /
      `layout current` / `bell list` 均 `exit 0`；`doctor.healthy=true`、`issueCount=0`。
- [x] **写命令默认 dry-run**：`cex bell set --slot … --prep 90` 输出「预览（未写入，需 --yes 生效）」，
      随后 `bell.json` 仍为 `[]`。
- [x] **远程同步关闭时完全离线可用**：`audit.log` 记录
      `remote skip :: 未配置有效的 https 地址/令牌，或开关关闭；完全离线可用`，未发起网络请求。
      开启后的验签失败回滚与白名单拒写仍未验证。

### C.1 实机验收中修掉的问题（v1.0.0.1）

| # | 现象 | 根因 | 修复 |
|---|---|---|---|
| 1 | 布局切换后 `Settings.json` 不变，日志称成功 | 宿主 `SaveSettings(string note = "")` 参数带默认值，旧实现只认「参数个数为 0」，永远匹配不上 | 接受全部参数可选的 `SaveSettings`/`Save`，按 `ParameterInfo.DefaultValue` 补实参 |
| 2 | 午休启动时布局被切成上课样式 | 宿主启动期连续发状态变化，`StartAsync` 立刻读 `VirtualState` 读到瞬态 `OnClass` | 延后 1.5 s 再应用初始状态（实测 34.676 订阅 → 36.232 应用） |
| 3 | 启动时连发 5 次「进入上课状态」，并会重放一上午的铃声 | `FireDue` 每 tick 只取最早的一条未触发事件，启动时把所有历史事件逐条补发 | 新增 `SeedStateFromHistory`：用历史时点直接推导当前状态，不触发提醒与铃声 |

> 修复后同一场景日志：`按历史时点推导当前状态：None -> Breaking`，
> 不再出现「取消待触发的下课切换」连发；`Settings.json` 从误写的 `Default/0.8/0.5`
> 被重新写回 `offclass/1/0.7`，证明保存链路真的生效。

## D. 发版

- [x] 初始提交已推送到 `yiwubianjihui/classexland`（`main`），111 个文件。
- [x] `git tag v1.0.0.0`（annotated，指向 `0e07360`）推送后 CI 产出 `.cipx` 与 `SHA256SUMS`。
- [x] Release 页面可见资产，README 安装步骤可用。

### 发版实测记录

| 项 | 结果 |
|---|---|
| CI 流水线 | run `44458193363` 全 11 步 success（含版本一致性校验、SHA256SUMS 生成、Release 发布） |
| Release | `v1.0.0.0`，名称「ClassExland v1.0.0.0 · 首个公开版本」，非草稿、非预发布，匿名可访问 |
| 资产 | `ClassExland.cipx` 238 472 B、`SHA256SUMS` 83 B |
| 校验和 | `e9eb77d4184daf0750993bc3beb9e8ab77a70eaab198f6d2b400f58a73919891`，下载后本地实测一致 |
| 包内容 | `manifest.yml`（version 1.0.0.0）、`ClassExland.dll`、`ClassExland.pdb`、`deps/runtimeconfig.json`、`icon.png`、`README.md` |

> CI 前三次 `main` 构建均失败，但**不是代码问题**：第 7 步「校验版本一致性」里 Python 在 Windows
> runner 上用 cp1252 输出，`print("版本校验通过")` 的中文抛 `UnicodeEncodeError`，而版本其实已校验通过
> （日志中 `csproj=1.0.0.0 manifest=1.0.0.0` 已正常打印）。修复为步骤级 `PYTHONIOENCODING=utf-8`
> 加脚本内 `sys.stdout.reconfigure(encoding="utf-8")` 双保险，`0e07360` 之后 main 与 tag 两次构建均全绿。

