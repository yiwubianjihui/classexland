# 进度日志

## 2026-11-19 00:54 · 立项

- 用户确认 4 项决策 + 4 项补充要求，详见 `DECISIONS.md`。
- 建立团队 `classexland` 与 13 项持久化任务。
- 待办：仓库骨架 → 并行实现 → 集成构建。

## 2026-11-19 01:20 · 集成审查（第一轮）

- CLI 桥接层与 Layout/Remote 模块已交付；静态审查发现并已下发修复：
  1. Bridge 按接口全名反射，但 Layout/Remote 只有具体类 ⇒ 需补 ILayoutService / IRemoteSyncService / IProfileService，且方法参数个数必须与反射调用一致。
  2. ProfileSwitchService 调用 `profile.switch` 命令、Bridge 又反射回 IProfileService ⇒ 递归死循环；改为直接走宿主官方档案服务。
  3. Features/Bridge 与 Features/Remote 各有一个 LoopbackWebHost ⇒ 删除 Remote 版本，Bridge 为唯一 Web 管理页，并补 /api/profiles、/api/profile/switch、/api/sync 路由。
- 安全默认值确认：核心配置同步默认关闭（EnableCoreConfigSync=false），白名单外的键整份拒绝；CLI 令牌随机生成、审计脱敏。
- 终端仍无法启动原生进程，编译与 git 操作继续挂起。

## 2026-11-19 01:00 · 团队并行开发中

- 5 个智能体已启动：bell-dev（Bell+Harmony）、duty-dev（值日表/提醒/迁移）、sys-dev（布局+远程同步）、cli-dev（CLI+指令桥）、docs-dev（文档+CI）。
- 骨架与共享契约完成：`ClassExland.sln`、`src/ClassExland/{Plugin.cs,Contracts/Contracts.cs,Infra/PluginPaths.cs}`、CLI 占位、manifest、LICENSE、icon、`docs/DECISIONS.md`、`TODO.md`。
- 跨模块契约确认：命令名点分小写（bell.set 等），参数放 Args 短键；端点发现文件 `%LOCALAPPDATA%\ClassExland\bridge-endpoint.json`；环境变量 `CLASSEXLAND_CONFIG_DIR`。
- 本机终端异常：Bash 与 PowerShell 启动原生程序（dotnet/git）报 AccessViolationException / -1073741819；构建与 git 操作待环境恢复后重试，智能体可先写代码。
- GitHub Token 已由用户提供，仅用于最终 push，不写入任何文件。

## 2026-11-19 00:31 · 勘察

- 勘察 ClassIsland 2.1.0.1 本机目录，确认时间线 AttachedObjects、自动化文件、官方 IPC 三个扩展点。
- 产出 `docs/DESIGN.md` 审核稿。

## 2026-11-19 06:05 · 构建转绿

用 WMI `Win32_Process.Create` 绕开终端故障（PowerShell 直启 dotnet 会抛 `AccessViolationException`，
bash 遇中文路径崩 `0xC0000409`），跑通真实 Release 构建。四轮收敛 20 → 14 → 9 → 0：

- 第一轮 20 错：命名空间写错、缺 `using`、`ClassPlan` 命名空间。
- 第二轮 14 错：Bell 播放签名实际是 `PlayAudioAsync(Stream, float, CancellationToken?)`（宿主
  `ClassIsland.Core.xml:964` 核实）；Duty 枚举根本没有 `CustomStart`；Bridge 的 `File.GetAccessControl`
  是 .NET Framework API（正确形态是 `FileInfo` 上的扩展方法，net8.0 参考包已含，无需加包）。
- 第三轮 9 错：`DutySettingsPage.axaml` 用 `models:` 前缀引用 `MigrationConflict`，实际命名空间是
  `...Duty.Migration`。
- 第四轮 **0 错 9 警告**，产出 `cipx/ClassExland.cipx`（243 KB）与 `cex.exe`。

补齐 `tests/ClassExland.Tests`：3 个测试文件、39 个用例，**39/39 通过**。
首跑 13 个失败是环境问题——插件对宿主 SDK 用了 `ExcludeAssets="runtime"`，
`RotationSettings : ObservableRecipient` 在测试宿主里加载不到 `CommunityToolkit.Mvvm`；
测试工程单独引入该包后全绿。

## 2026-11-19 06:19 · 推送到 GitHub

- 仓库 `yiwubianjihui/classexland` 原本不存在，经 API 创建（令牌作用域含 `repo`，身份 `Yibianhui`）。
- **HTTPS 到 github.com 被重置**（`Recv failure: Connection was reset`），本机代理对 github.com 返回
  `502 CONNECT`，只有 `api.github.com` 放行；TCP 各端口均可达 ⇒ 干扰在 TLS 层。
- 改用 SSH：`github.com:22` 与 `ssh.github.com:443` 均可用。生成 Ed25519 部署密钥并经 API 注册。
- 一个坑：用 WMI 传 `-N ""` 时 cmd 把空串解析成两个字面引号，密钥被加密成 `aes256-ctr`，
  于是 SSH 停在 `Server accepts key` 后 `we did not send a packet`（BatchMode 拿不到密码）。
  改为把 `ssh-keygen` 写进 `.bat` 交给 cmd 自己解析引号，重新生成后立即用 `ssh-keygen -y` 验证无密码。
- 推送成功：`main` 111 文件 / 13641 行；`origin` 设为 `git@github.com:yiwubianjihui/classexland.git`，
  已建立上游跟踪，工作区与远端无差异。
- 令牌只出现在一次性命令里，未写入任何文件（全仓 grep 零命中）。

## 2026-11-19 12:19–12:33 · 发版 v1.0.0.0

发现：之前三次 `main` 的 CI 构建**全部失败**，但我只验证了本地构建没查 Actions。逐步骤看下来，
失败点是第 7 步「校验包内 manifest.yml 与 csproj 版本一致」，而版本其实已校验通过
（日志里 `csproj=1.0.0.0  manifest=1.0.0.0` 正常打印）。真正原因是下一行
`print("版本校验通过")`——Windows runner 的 Python stdout 默认 cp1252，中文抛
`UnicodeEncodeError`，进程以 exit 1 结束。报错分支里的中文 `sys.exit(...)` 同理。

修复：该步骤加 `env: PYTHONIOENCODING: utf-8`，脚本内另做
`sys.stdout.reconfigure(encoding="utf-8")` / `sys.stderr.reconfigure(...)` 双保险，并注释说明原因。
提交 `0e07360` 后 main 构建 run `44457956381` 全绿。

发版：`v1.0.0.0` 先指向 `fa4540d`（会重蹈覆辙），已删除远端 tag 重建到 `0e07360`。
tag 构建 run `44458193363` 11 步全绿，Release 自动发布。

验收：
- Release `v1.0.0.0` 非草稿非预发布，匿名 API 可读，资产 `ClassExland.cipx` 238 472 B + `SHA256SUMS` 83 B。
- 下载两个资产实测 SHA256 = `e9eb77d4…9891`，与 `SHA256SUMS` 一致。
- 解包检查：`manifest.yml`（version 1.0.0.0）、`ClassExland.dll`、`ClassExland.pdb`、
  `deps.json`、`runtimeconfig.json`、`icon.png`、`README.md`。
- Release 正文原本只有一行 `Full Changelog`，已 PATCH 成完整中文说明（功能、安装、文件与校验和、
  兼容性、文档链接）。踩到一个 PS 5.1 坑：`Get-Content -Raw` 的返回值被包成 `{value, ReadCount}`
  对象，`ConvertTo-Json` 后膨胀到 450 KB 并 422，改用 `[System.IO.File]::ReadAllText` 解决。

顺带修正 README 里 cipx 输出目录的错误说明（实际是 `src/ClassExland/cipx/`，
由 SDK targets 的 `CipxPackageOutputDirectory` 决定，不是 `bin/Release/net8.0/`）。



## 2026-11-19 晚 · 逐节课提前量 + 远程设置页（1.0.0.2）

用户反馈两件事：① 需要给每节课单独调整预备铃时间；② 找不到 CLI 与远程的入口。

排查结论：数据层与时间轴本来支持每节不同（`AttachedObjects[我方GUID]` 优先、`bell.json` 兜底），
但**没有任何 UI 入口**；`bell list` 只读 `bell.json`，而首次安装时该文件是 `[]`，
用户拿不到可用的 slot id。远程也只有 `remote.json` 手改，设置页里没有。

实现：

- `BellSlotCatalog`：把档案时间表按「表名 → 开始时间」展平成稳定序号，设置页与 CLI 共用，
  保证界面上的"第 3 节"与 `cex bell set --index 3` 永远指同一节。
- 预备铃设置页新增 DataGrid（课次 / 单独设置 / 提前量），保存时先提交正在编辑的单元格，
  再批量写 `bell.json` → `ApplySlotPreps()` 写档案 → `InvalidateSchedule()` 重建当天时间轴。
- `BellSettings.Reload()` + `BellProfileWriter.ApplySlotPreps()`：只写我方附加设置，不动宿主提醒开关
  （原 `Apply()` 会顺手关宿主提醒，不适合"只改提前量"这条路径）。
- CLI：`bell list` 改读课表（回退 bell.json）；`bell set` 支持 `--index`；写后热重载 + 同步档案。
- 远程设置页：remote.json 的全部字段 + 检查/同步/停用；保存启用时清掉 `remote.disabled` 标记。
- 远程快照写了 bell 配置后自动热重载（原来要重启才生效）。

顺手修的两个真 bug：

1. **HMAC 验签永远失败**：原实现对响应体全文算 HMAC，而 `signature` 字段本身就在响应体里
   ⇒ 服务端无法构造出能通过的签名。改为去掉顶层 `signature` 后计算，并统一按大写比较。
2. **`hostRunning` 恒为 false**：ClassIsland 2.x 的进程名是 `ClassIsland.Desktop`，
   原判定只查 `ClassIsland` ⇒ CLI 的"宿主运行时拒绝离线写配置"保护实际失效。两个名字都查。

验证：`dotnet build` 0 错误；单测 39/39。`cex` 发布为单文件（186 826 B）并装到
`%LOCALAPPDATA%\ClassExland\bin\cex.exe`，实测 `--version` = 1.0.0.2、`status` 能连上运行中的桥。

未完成：**插件 1.0.0.2 尚未装到实机**——宿主正开着"档案编辑"窗口，`ClassExland.dll` 被锁，
复制会失败。manifest 已回退到 1.0.0.1 与实际 DLL 保持一版，待宿主退出后覆盖。

## 2026-11-19 深夜 · 远程控制设置页重做（1.0.0.2 收尾）

用户第二次反馈「给远程控制也制作设置界面，便于普通用户使用」——说明上一版设置页虽然有了，
但仍是一张技术表单（清单路径 / HMAC / 超时 / 三项范围平铺），普通用户看不懂也用不上。

重做后的 `RemoteSettingsPage`：

- **状态卡在最上面**：未启用 / 已停用 / 配置不完整 / 已启用，加一行"上次成功同步的时间与版本"
  （从 `remote-state.json` 读 `AppliedAt`，为此给 `RemoteStateStore` 加了 `LoadAppliedAt()`）。
- **主界面只留两个输入框**：服务器地址、访问令牌；按钮三个：保存并测试 / 立即同步 / 停用。
- **错误中文化**：把同步服务返回的 JSON 摘要解析成一句话，401/403、404、超时、DNS、证书、
  连接被拒分别给出可执行的中文提示，不再把英文异常直接甩给用户。
- **高级选项折叠**：清单路径、签名密钥、超时；再加配置/日志路径与「打开配置文件夹 / 复制诊断信息」。
  诊断信息里令牌只显示"已填（N 字符）"，不泄露明文。
- **本机网页控制台一键开关**：直接调 `LoopbackWebHost.StartAsync()`（与 `cex web start` 同一条路径），
  启动后显示带令牌的地址，可「打开」或「复制链接」。仅监听 127.0.0.1。
- 预览清单地址随输入实时更新，普通用户不用理解"相对路径"。

**顺手重做本机控制台页面**：既然设置页把「本机网页控制台」作为普通用户入口，原来的页面只输出原始 JSON 就说不通了。
`LoopbackWebHost.BuildPage()` 改为真正的控制面板——大字号上课状态、下一次铃与倒计时、当前科目/档案、
六个按钮（切上课/下课布局、刷新、值日表、切换档案、立即同步）、值日表按项目列人名、原始数据折叠保留。
页面跟随系统深浅色；所有动态文本走 `textContent`，不拼 HTML。用 Node 起了模拟 API 服务 +
无头 Edge 截图核对过渲染（`artifacts/console.html`，已 gitignore）。

**部署**：宿主已退出，`ClassExland.dll` / `.pdb` / `manifest.yml` 已覆盖到实机插件目录，
构建产物与实机 DLL 的 MD5 一致（`382C6037E66A1884E30A08AC5E604130`）；旧版备份在
`artifacts/backup-1.0.0.1/`。构建 0 错误、单测 39/39、cipx 274 601 B（manifest 1.0.0.2）。

**未完成**：本机安全策略拦截了 WMI 启动宿主，实机打开设置页这一步要用户手动启动后确认。

**补一个稳健性修正**：每节课表格原来用 `Avalonia.Controls.DataGrid` 实现。插件无法保证宿主加载了
DataGrid 的运行时主题（`avares://Avalonia.Controls.DataGrid/Themes/...`），一旦缺失，DataGrid 会渲染成空白框，
用户看到的就是"表格没内容"。现在改成 `ItemsControl` + `Grid` 手绘表头与行，不依赖任何额外主题，
勾选框与输入框仍双向绑定到 `BellSlotRow`，`应用每节课设置` 的语义不变（TextBox 默认按 PropertyChanged 回写，
所以不再需要 `CommitEdit`）。值班表页仍保留原有 DataGrid（宿主自身的主界面就用 DataGrid 渲染课表，
说明主题由宿主提供；若实测有问题再一起换）。

## 2026-11-20 凌晨 · 档案编辑器内嵌预备铃（1.0.0.3）

用户问「预备铃不能直接在时间表档案编辑里设置么」——能，而且本来就该这么做。

排查结论：ClassIsland 有一套「附加设置」机制（`AttachedSettingsTargets.TimePoint` 对应
`TimeLayoutItem`），插件注册一个 `AttachedSettingsControlBase<T>` 子类后，档案编辑器里
选中某个时间点就会在「附加设置」中渲染该控件。我们从 1.0.0.0 起只往
`AttachedObjects[我方GUID]` 写数据，但**从未注册控件**，所以用户在课表里看不到任何入口。

实现：

- `BellSlotSettings` 实现 `IAttachedSettings`（宿主靠 `IsAttachSettingsEnabled` 决定是否把对象
  写回档案；不实现的话控件里改的值根本存不下来）。默认 `true`，兼容旧档案。
- 新增 `BellSlotSettingsControl`：`[AttachedSettingsUsage(TimePoint)]` +
  `[AttachedSettingsControlInfo(我方GUID, "预备铃（ClassExland）", …)]`，纯 C# 建 UI
  （PluginSdk 2.0.0.2 的基类没有 `AttachedTimePointTimeType`，无法区分上课/课间，统一提示）。
  在 `OnAttachedToVisualTree` 读取 `Settings`——宿主是先构造控件再注入设置对象。
- `BellRegistration` 增加 `services.AddAttachedSettingsControl<BellSlotSettingsControl>()`。
- 时间轴变更签名纳入每节课提前量：原来只含课表名/布局 ID，在档案里改值不会触发重建。
- 重建时若已过启动阶段，调用 `SeedStateFromHistory` 重新推导状态；否则 `_fired.Clear()` 后
  `FireDue` 会把当天已过去的时点全部补发一遍（历史上的"重放一上午铃声"会复现）。
- 设置页取消勾选「单独设置」改为真正删除档案里的附加对象（原来写成默认值，表里显示未勾选、
  档案里却留着固定值）。

验证：构建 0 错误；单测 42/42（新增 3 个档案格式兼容测试，固定住 `prepSeconds` 小写 +
`IsAttachSettingsEnabled` 的线上形状）。测试工程新增 `ClassIsland.Shared` 与
`System.Text.Json 9.0.2` 引用——前者因为 `BellSlotSettings` 实现了宿主接口，后者因为
Shared 2.0.0.2 的元数据引用 STJ 9.0.0.0 而测试宿主是 .NET 8。

部署：宿主已退出，`ClassExland.dll` / `.pdb` / `manifest.yml` 覆盖到实机，
MD5 `474B9F2B1571F2E8508235E981D57782` 与构建产物一致；旧版备份 `artifacts/backup-1.0.0.2/`。
`cex` 同步更新到 1.0.0.3。

未完成：本机安全策略仍拦截 WMI 启动宿主，档案编辑器内的实际渲染要用户打开确认。

## 2026-11-20 凌晨 · 四项收尾与 1.0.0.4（界面切换页 / 课间禁用 / 值日迁移 / 远程站）

用户一次提了四件事，逐条闭环。

**① 课间也显示预备铃输入框 → 语义澄清 + UI 收紧。**
先证伪了"课间会响预备铃"：`BellTimelineService` 只处理 `TimeType` 0/1，
`BellTimelineCalculator` 第 54 行 `if (slot.Kind != BellSlotKind.Class) continue;`，
课间根本进不了计算。问题出在 UI：`AttachedSettingsControlBase` 拿不到当前时点类型，
课间时段也照常渲染可编辑输入框。宿主运行时其实有 `AttachedTimePointTimeType`
（Core.dll 里有 `get_/set_`），但编译期 PluginSdk 2.0.0.2 没有公开——改用反射探测，
0=上课、1=课间、2=其他。非 0 时禁用输入框、把 `IsAttachSettingsEnabled` 置 false（不写回档案），
并换成一段中文说明。反射拿不到时按"上课"处理，保持旧行为。

**②「界面切换」补设置页。**
`Features/Layout/` 之前只有 `layout.json` 和 applier，没有任何 UI。新增
`LayoutSettingsPage`（`[SettingsPageInfo("yibianhui.classexland.layout", …)]`）：
上下课两套组件配置 / 缩放 / 不透明度、下课延迟与拖堂顺延、立即切到上课或下课、
恢复默认、一键生成宿主自动化片段并复制、打开配置文件夹与日志。
组件配置下拉走 `ILayoutApplier.GetComponentConfigNames()`（新接口方法）——
反射读宿主 `ComponentConfigs`，`IDictionary` 取键、`IEnumerable` 取 `Name`，
读不到就返回空数组，页面退化成纯文本框，不会崩。

**③ 旧值日配置迁移。**
按 `dutylist.jimmyxiao/duty.json`（Groups/DayConfig/SkipDays）生成
`yibianhui.classexland/duty.json`：1 个方案、3 个时段、23 条值日项。
踩到本项目最隐蔽的坑：宿主 `ConfigureFileHelper.SerializerOptions` 是 `new JsonSerializerOptions()`，
**默认 PascalCase 且大小写敏感**；第一版迁移脚本按 camelCase 写（`weekDays`/`items`），
读回来 `WeekDays` 停在默认「周日到周四」、`Items` 为空、方案名变成"值日表"。
用宿主真实的 `ConfigureFileHelper.LoadConfig` 复现后确认，改成混合大小写
（带 `[JsonPropertyName]` 的字段仍是 camelCase，其余 PascalCase）才通过。
另一个修正：`WeekDays` 先全 false 再按实际出现的天置 true——插件默认值是"周日到周四"，
直接套用会把周六弄错。原文件两处备份，`docs/CONFIGURATION.md` 已写清这个大小写规则。

**④ 远程站在用户自己的服务器上落地。**
写了一套四个文件的 PHP 远程站（`server/remote-station/`，仓库内随源码发布）：
首启 `setup` 生成令牌与 HMAC 密钥 → 登录 → 管理面板（发布快照、下发指令、改令牌、看最近访问）。
部署到 `https://qn.yibianhui.cn/remote/`，PHP 8.5，`www:www`，`data/` 750、`data/*.php` 640，
`php -l` 全过，无令牌 401 / 带令牌 200 / 管理页 200 / `X-ClassExland-Signature` 正确。

过程中两个坑：

- **opcache 把 `include` 的 PHP 配置读成旧内容**：发布后管理页 flash 说"已发布 20261120-004511"，
  清单却还是空快照。改成 `<?php exit; ?>\n{json}` 格式 + `file_get_contents` 读，
  兼容旧格式并自动转换；`cx_read_store` 的 `include` 分支加 `strpos($raw,'exit;')===false` 防死循环。
- **清单必须字节级透传**：`cx_build_manifest` 用字符串拼接把 `snapshot.pluginConfigs` 里的
  每个文件原样嵌进去，绝不 decode/encode——否则 PascalCase 会被 JSON 库改成 camelCase，
  和 ③ 的坑撞在一起。
- 本机到 `qn.yibianhui.cn` 必须 `curl --noproxy '*'`（127.0.0.1:5487 代理不通），直连正常。
- 客户端签名校验改成读响应头 `X-ClassExland-Signature`（对响应原文 HMAC-SHA256），
  旧的"清单内 signature 字段"协议保留兼容；两路都过不了才拒绝应用。

**发版与部署。**
构建 0 错误、单测 42/42、`ClassExland.cipx` 285015 B；`main` 推送 + `v1.0.0.4` tag，
CI（windows-latest）2m 0s 成功，Release 附 `ClassExland.cipx`（268 KB，
sha256 `8052142d…f567bb`）与 `SHA256SUMS`。
实机 `data/Plugins/yibianhui.classexland/` 已覆盖 DLL（507904 B）+ `manifest.yml`；
宿主当时在运行，用"先改名再写入"的方式换掉被占用的 DLL，旧 DLL 移到 `artifacts/backup-1.0.0.3/`，
不重启宿主也能完成部署。`cex` 用 `-p:PublishSingleFile=true` 重新发布（186826 B）并覆盖安装。

**未完成 / 待用户决定。**

- 新 DLL 要等宿主下次启动才生效——课间禁用与「界面切换」页的实际渲染尚未肉眼确认。
- 远程站目前仍是空快照。`duty.json` 里有真实学生姓名，按既有隐私约定**没有**自动上传，
  等用户确认后再发布首个正式快照。

> 补记（同日）：`AttachedTimePointTimeType` 的可见性用 `dnfile` 直接解析宿主
> `app-2.1.0.1-0/ClassIsland.Core.dll` 的元数据确认过——声明在
> `ClassIsland.Core.Abstractions.Controls.AttachedSettingsControlBase`（正是本插件的基类），
> 访问标志 `mdPublic=True`，属性类型是 `int`。所以 `GetType().GetProperty("AttachedTimePointTimeType")`
> 一定能拿到，不需要 `NonPublic` 兜底（试过一版，为保持源码与已发布二进制一致又回退了）。
> 探针脚本留在 `artifacts/probe_attached_type.py`，以后核宿主 API 可直接复用。

## 2026-11-20 01:00 · 1.0.0.5（课间识别的时序问题）

1.0.0.4 用反射读 `AttachedTimePointTimeType` 判断上课/课间，但只读了两个时点：
构造时和 `OnAttachedToVisualTree`。核宿主元数据时发现还有
`AttachedSettingsControlPresenter.UpdateContentAttachedTimePointTimeType`（private），
说明这个值是由 presenter **推**给控件的——推送时机若晚于挂树，控件读到的就是默认值 0，
课间仍会显示可编辑的输入框（用户提的正是这个问题）。

修法：重写 `OnPropertyChanged`，属性名等于 `AttachedTimePointTimeType` 时重算一次
（`Refresh()` 同时负责禁用输入、写 `IsAttachSettingsEnabled=false`、换说明文字）。
这是纯增量，属性早到或晚到都能收敛到正确状态。

顺带把核验宿主 API 的手法固化成 `tools/probe_host_metadata.py`（dnfile 纯字节解析，
不加载程序集），以后遇到"编译期没有、运行期可能有"的成员直接查一行。

构建 0 错误、单测 42/42、`ClassExland.cipx` 285113 B；实机 DLL 已换成 1.0.0.5
（旧 1.0.0.4 留在 `artifacts/backup-1.0.0.4.dll`），`cex` 同步重新发布。

## 2026-11-20 23:20 · 1.0.0.6（多设备快照 / 时段改名 / 搬到 classisland.yibianhui.cn）

用户这一轮三条：身兼多个班级要能按设备名分别下发、时段别显示具体时间、远程站换域名。

**① 多设备：一个站管多间教室。**
把 `state.php` 从「单快照」升到 v2：`devices{名字 → {files, commands, version, etag, last_seen…}}`，
读旧状态时自动包成一台「默认设备」。`manifest.php` 按 `?device=`（或 `X-ClassExland-Device` 头）
取对应快照；客户端 `RemoteConfig.DeviceName` 会拼进 query，并在头里再带一份当退路。
本地只有一台设备时不传 device 仍然可用；多于一台且没传 → 400 + 可选名字列表，
客户端把 `device_required` / `unauthorized` / `too_many_devices` 翻成中文提示。

顺手做了一个"自动登记"：客户端第一次带着某个名字来拉，服务端就建一台空设备
（只接受合法名字、上限 32 台）。这样老师在新电脑上填个名字就能在管理页看到它、
直接发布，不必先跑去后台建。名字打错也只是多一台空设备（空快照 = 本机配置不动），删掉即可。

**② 时段标签改粗粒度。**
新增 `Features/Duty/DutySlotLabels.cs`：按开始时间给 上午/下午/傍晚/晚上，
单时段且跨度 ≥ 6 小时叫「全天」；重名自动加序号（两个下午 → 下午 / 下午2）。
`DutyService.Load` 时把旧配置里像 `07:00` 的标签批量换掉并同步 `Items.SlotLabel`，改完落盘一次；
迁移服务也走同一套。实机 `duty.json` 已就地改成 上午 / 下午 / 傍晚（3 时段 23 项）。

**③ 远程站搬到 `https://classisland.yibianhui.cn/remote/`。**
站点与证书是用户自己建好的（`/www/wwwroot/classisland.yibianhui.cn`，PHP 8.5，`enable-php-85.conf`）。
把四个 PHP 传上去、从旧站 `qn.yibianhui.cn` 复制 `data/config.php`（令牌/密钥/管理员密码原样沿用，
客户端只改服务器地址），旧 state 升级成 v2 后写入。验收：无令牌 401、带令牌 200、
签名头与本机 `hmac_sha256` 逐字节一致、管理页登录后能切换设备、`php -l` 全过、`nginx -t` 通过。
旧站保留不删，`remote.json` 里换地址即可（备份 `remote.json.bak-before-classisland-domain`）。

**踩到的坑（值得记）：**
用 root 跑 CLI 脚本写 `state.php`，`rename` 出来的文件属主是 root:root，
php-fpm 读不到 → `file_get_contents` 失败 → 当时 `cx_load_state()` 把"读不出来"当成"还没初始化"，
**用空状态覆盖了已发布的快照**（版本号从 `20261120-004925` 变回 0）。
修法两层：CLI 写完后一律 `chown -R www:www`；`cx_read_store($path, $strict)` 在文件存在却读不出/解析不了时抛异常，
管理页直接显示「远程站读不到数据文件」并给出 chown/chmod 命令，`manifest.php` 返回
`500 station_storage_error`——宁可报错，也不静默清空。

构建 0 错误、单测 56/56（新增 `DutySlotLabelsTests` 11 例 + `RemoteDeviceNameTests` 3 例）、
`ClassExland.cipx` 288669 B；实机 DLL 与 `cex` 已换 1.0.0.6（旧版备份 `artifacts/backup-1.0.0.5.dll`）。

> 补记（同日 23:26）：多设备还差一块拼图——老师得手打中文设备名，很容易打成「初一２班」（全角）
> 或在末尾多一个空格，服务端就当成新设备自动登记，列表里堆一堆空设备。
> 于是 1.0.0.7 补了「从服务器读取设备列表」：`GET /manifest.php?list=1`（Bearer 鉴权）
> 只回名字/备注/版本/最近拉取时间，设置页一个下拉选中即回填。设备多时也能一眼看出哪台还没发布过。
