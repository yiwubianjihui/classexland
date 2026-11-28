# 全覆盖远程控制 —— 实施跟踪日志（2026-11-28）

> 配合 `docs/PROPOSAL-20261128-FULL-REMOTE.md`（方案）与 `docs/PROTOCOL-CORE-SYNC.md`（协议契约）。
> 本文件随实施进度追加，避免长会话压缩导致信息丢失。**所有不确定点在此登记并先问用户。**

## 任务状态

| # | 任务 | 状态 |
| --- | --- | --- |
| 14 | 协作团队 + 持久化任务清单 | ✅ 完成 |
| 15 | 协议契约 `PROTOCOL-CORE-SYNC.md` | ✅ 完成 |
| 16 | 服务端 S1–S8（server-dev） | 🔄 进行中 |
| 17 | 客户端 C1–C8（client-dev） | 🔄 进行中 |
| 18 | 部署脚本 + 换包（deploy-dev） | 🔄 进行中 |
| 19 | 构建测试 + 发版 v1.0.0.18 + 部署 | ⏳ 待 |
| 20 | 端到端验证 + 文档交付 | ⏳ 待 |

## 用户已确认的决策

1. 20.bat 保持 `ClassIsland-20` 路径（教室机目录名不变），配置从母版复制覆盖。
   - 13:10 核对：两源包 DLL 与 5 个插件配置骨架哈希**已完全一致**，无需再复制；设备差异（课表群/duty/设备名）正确保留。
2. ComponentLayouts（Default.json/offclass.json）**纳入**远程同步。
3. 先本地全部改完测试，一次维护窗口内连续部署（服务端先上线→客户端发版→源包/bat 更新）。
4. 「即时备份+上传，家里合并」：教室端只留痕+上报，不做字段级 merge。

## 关键事实（调研结论，供三路交叉核对）

- 服务端 7 个 PHP：`lib.php`/`history.php`/`upload.php`/`manifest.php`/`command.php`/`index.php`/`install.php`；
  `state.php` 是数据文件不是接口；**下载能力内嵌在 manifest.php 的 snapshot，无独立 download 端点**。
- 白名单唯一来源 `lib.php:19 CX_ALLOWED_KEYS`；过滤点 5 处（upload/history tree/commit/manifest/index）。
- 客户端 `RemoteLocalSnapshot.cs:13-16` 硬编码 4 文件；`IsSafePluginConfigName` 在 `RemoteSyncModule.cs:677-685`。
- 现有「下载优先」是**无条件覆盖**（`Upload.cs:83-89`），与新需求冲突，必须改为 `NeedApply && !LocalDirty`。
- 现有备份只有 `Backups/full-<ts>/` 整份 + 单文件 `.bak`，**无 diff**。
- core 客户端已实现（`CoreConfigWriter`/`CoreConfigPolicy`/`SettingsServiceBridge`），服务端从不输出 → 收不到数据。
- `YBH.json` 母版 121,128 B（19）/ 143,397 B（20）；31 处 `08f0d9c3` 提示对象，15 处 `IsAttachSettingsEnabled=false`。
- 上课提示 13 个白名单字段见 `deploy-dev` 任务描述；GUID `08f0d9c3-c770-4093-a3d0-02f3d90c24bc`。
- 新 LuckyPicker 包：`YBH摇人器-WPF-1.2.4+26-内部版-Setup.exe`，71,214,080 B，`ProductVersion=1.2.4+26`，
  SHA256 `961E4B47968BE9AAA03092A6B87C96EA4B4208EAD56EB584455805DAA3B568ED`；需复制为 ASCII 名
  `LuckyPicker-win-setup-1.2.4+26.exe` 并移走旧包（1.2.3+25）。

## 不确定点登记（发现即在此追加，先问用户再动）

- （暂无；三个追问已在开工前问过并确认）

## 进度日志

- 12:57 用户确认方案，要求智能体协作 + 持久化 todo + 多写文档。
- 13:00 建立任务 #14–20；写协议契约文档；追问 3 个不确定点并获确认。
- 13:05 三智能体并行开工（server-dev / client-dev / deploy-dev）。
- 13:10 核对两源包配置骨架一致，确认无需再复制；写本跟踪日志。
