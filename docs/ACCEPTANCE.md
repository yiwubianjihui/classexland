# 验收清单（ClassExland 1.0.0.0）

> 状态：待执行。当前会话的终端无法启动原生进程（dotnet/git 报 AccessViolationException），
> 编译与 git 操作需在终端环境恢复后补做；代码审查与静态核对先行。

## A. 静态核对

- [ ] 每个 Feature 目录都提供约定的注册扩展：`AddBell` / `AddDuty` / `AddLayout` / `AddRemoteSync` / `AddCommandBridge`。
- [ ] `Plugin.cs` 调用了全部 5 个注册扩展，且只依赖 `Contracts` / `Infra`。
- [ ] `manifest.yml` 的 `version` 与 `ClassExland.csproj` 的 `<Version>` 一致（1.0.0.0）。
- [ ] 没有任何硬编码的服务器地址、令牌、密钥、真实学生名单。
- [ ] 所有事件处理器有 try/catch；所有定时器订阅 `PostMainTimerTicked`。
- [ ] 主界面组件单行、无显式 Height、无写死 FontSize。

## B. 构建

- [ ] `dotnet restore ClassExland.sln` 成功。
- [ ] `dotnet build -c Release` 成功（允许 AVLN3001 无害警告）。
- [ ] `dotnet test` 通过：`BellTimelineCalculator`、`RotationCalculator`、迁移器纯逻辑测试。
- [ ] `cipx/ClassExland.cipx` 生成，包内 `manifest.yml` 版本一致。

## C. 运行时（安装到 ClassIsland 2.1.0.1）

- [ ] 安装后插件未自动禁用；设置页各 Tab 正常显示。
- [ ] 每节课提前量可编辑；`T−N−60s` / `T−N` / `T` 三条提醒各出现一次。
- [ ] Harmony 覆盖使宿主在 `T−N` 进入 OnClass；关闭开关后回落虚拟状态且不崩溃。
- [ ] 下课延迟 D 后弹出值日提醒，约 17 秒后自动消失，不重复排队。
- [ ] 特殊活动提前 10 分钟提醒，且附带值日名单。
- [ ] 上课小界面 / 下课完整界面按状态切换；拖堂时可顺延。
- [ ] 值日组件在编辑模式可选中；空数据显示「暂无安排」。
- [ ] `cex status` / `cex config doctor` 正常；写命令默认 dry-run，`--yes` 才落盘。
- [ ] 远程同步关闭时完全离线可用；开启后验签失败能回滚；核心配置白名单外的键被拒绝。

## D. 发版

- [ ] `git tag v1.0.0.0` 推送后 CI 产出 `.cipx` 与 `SHA256SUMS`。
- [ ] Release 页面可见资产，README 安装步骤可用。
