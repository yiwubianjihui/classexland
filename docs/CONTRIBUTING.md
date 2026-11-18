# 贡献指南

## 仓库与基线

- 仓库：`yiwubianjihui/classexland`
- 插件 ID：`yibianhui.classexland`；入口程序集：`ClassExland.dll`；目标 API：`2.0.0.0`；版本：`1.0.0.0`。
- 技术栈：.NET 8 / Avalonia / ClassIsland.PluginSdk 2.0.0.* / `.cipx`。
- 许可：MIT。

## 文档索引

- 设计：[DESIGN.md](DESIGN.md) · 决策：[DECISIONS.md](DECISIONS.md) · 进度：[PROGRESS.md](PROGRESS.md) · 任务：[../TODO.md](../TODO.md)
- 配置：[CONFIGURATION.md](CONFIGURATION.md) · 迁移：[MIGRATION.md](MIGRATION.md) · 远程同步：[REMOTE-SYNC.md](REMOTE-SYNC.md) · CLI：[CLI.md](CLI.md) · 更新日志：[../CHANGELOG.md](../CHANGELOG.md)

## 环境与构建

需要 .NET 8 SDK 与 NuGet（nuget.org 源）。

```bash
dotnet restore ClassExland.sln
dotnet build ClassExland.sln -c Release
dotnet test ClassExland.sln -c Release        # 纯逻辑单测
```

插件构建产物为 `.cipx`（输出目录 `src/ClassExland/bin/Release/net8.0/`，CI 收集为 `cipx/ClassExland.cipx`）。CLI（`src/ClassExland.Cli/`）是独立可执行项目，**不打包进 cipx**。

## 代码约定

- 命名空间统一 `ClassExland.*`；共享契约集中在 `src/ClassExland/Contracts/`，由 team-lead 维护。
- 各功能只写自己的 `Features/<Name>/` 目录；跨模块只依赖共享契约，避免并行冲突。
- 功能注册扩展方法名固定：`AddBell` / `AddDuty` / `AddLayout` / `AddRemoteSync` / `AddCommandBridge`。
- **BellModule 是唯一时间真相源**：其他模块只订阅事件，不各自读时间、不起定时器；所有定时器订阅 `ILessonsService.PostMainTimerTicked`，禁止重写非虚的 `StartAsync`。
- 所有事件处理器必须 try/catch——插件回调抛异常会导致 ClassIsland 自动禁用插件。
- 组件容器高度固定 40，组件必须单行；不要写死 `FontSize`，继承宿主 `MainWindowBodyFontSize`。
- 取服务用 `TryGetService<T>()`（`GetService<T>()` 取不到会抛异常）；`AddNotificationProvider<T>()` 只注册为 `IHostedService`。
- `TimeState` 枚举注意是 `Breaking` 而非 `BreakingTime`。

## 安全与隐私

- 不碰个人敏感名单文件，不上传或提交任何真实学生数据。
- 服务器地址不写进代码；远程同步只允许 HTTPS，密钥/令牌不硬编码、不进仓库。
- 写操作一律"备份 → schema 校验 → 临时文件 + 原子替换"，并记审计日志。

## 版本与发版

- `.csproj` 的 `<Version>` 与 `manifest.yml` 的 `version` **必须同步 bump**。
- tag `vX.Y.Z.W` 推送触发 Release：CI 构建并校验 `.cipx` 包内 `manifest.yml` 版本与 `csproj` 一致，附 `.cipx` 与 `SHA256SUMS` 发布。
- 工作流见 [../.github/workflows/build.yml](../.github/workflows/build.yml)。

## 协作流程

1. 从 `main` 建分支，改动尽量限定在自己的模块目录。
2. 提交前本地构建通过；涉及逻辑的补充单测。
3. 发起 PR，说明变更与验证方式；关键决策先提出并写入 `DECISIONS.md`，不自行假设。
4. 阶段结束更新 `PROGRESS.md` 与 `CHANGELOG.md`。
