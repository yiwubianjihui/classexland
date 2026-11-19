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


