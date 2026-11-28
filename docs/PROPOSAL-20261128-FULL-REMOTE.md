# 远程控制全覆盖 + 即时备份审理方案（待确认）

> 状态：**方案待确认，尚未改代码**。2026-11-28 12:45 根据用户补充「即时备份+上传，之后我在家里合并」定稿。
> 确认后进入实施；本文同时作为实施清单与验收依据。

## 0. 先请拍板的 6 个问题（TL;DR）

| # | 问题 | 我的建议 | 不这么做的后果 |
| --- | --- | --- | --- |
| Q1 | 「无冲突自动覆盖」的冲突判定用哪种？ | **(b) 本地变化已成功上传即视为不脏**（含进待审队列）；家里合并后的版本为权威 | 用 (a) 严格哈希会导致待审未决时永远不覆盖，需人工解锁 |
| Q2 | YBH.json 里的上课提示，部署脚本保护、远程同步要上传审理，两者并存可以吗？ | **可以**：bat 保护同学现场调整；远程把变化即时上传供你在家判断，合并后的版本才回写教室端 | 若两边都「保护」，家里的修复无法下发 |
| Q3 | 覆盖前备份粒度？ | **diff 文本为主**（`Backups/diffs/`），保留现有 `full-*` 整份兜底 | 只存 diff 无法整份回滚；只存整份则审阅困难 |
| Q4 | 「全都包含」的边界：凭据/令牌/缓存/日志/备份必须排除，同意吗？ | **排除**（见 3.3 红线清单） | 泄露 `remote.json` 的 Bearer/HMAC、`bridge.json` 本机 token，可被反向控制 |
| Q5 | 换课字段（`TempClassPlan*`/`IsChangedClass`）也随档案同步、由你在家裁决？ | **是**（当前值均为 null/false，同步无副作用） | 若也做本地保护，会出现「提示保护了、换课没保护」的不一致 |
| Q6 | LuckyPicker 新包装不进 GBK bat 的中文路径，用「复制成 ASCII 名」方案？ | **是**：复制为 `LuckyPicker-win-setup-1.2.4+26.exe` 到教学喵喵包目录，移走旧包 | 直接在 bat 写中文路径会因 GBK/UTF-8 编码错乱导致装不上 |

---

## 1. 需求 → 技术映射

| 你的要求 | 技术含义 | 现状差距 |
| --- | --- | --- |
| 白名单扩展，覆盖时间表/课表/上课提示/换课/设置 | 插件配置 4 → 6 个；新增 core 通道下发 `Settings.json` + `Profiles/YBH.json` + `ComponentLayouts` | 服务端白名单只有 4 个；`manifest.php` 从不输出 `core`（客户端 `CoreConfigWriter` 已实现但收不到数据） |
| 保留上课提示 = **部署脚本不覆盖教室机器** | `robocopy /mir` 前后加「备份教室端提示对象 → 同步 → 合并回去」 | 现在 `/mir` 按文件整份覆盖，31 处提示对象必丢 |
| 自动上传本地所有配置变化，交服务器审理 | 扩展 `RemoteLocalSnapshot.FileNames` + 新增 core 哈希；即时上传 | 现在只哈希 4 个插件文件，核心配置变化完全看不见 |
| 轮询到更新版本且无冲突 → 自动覆盖 + 留 diff 备份 | 改 `SyncTickAsync` 判定 + 新增 diff 备份生成 | 现在是**无条件覆盖**，且备份只有整份 `full-*` |
| 即时备份+上传，之后我在家里合并 | 教室端不做字段级 merge；只留痕+上报，合并决策全在管理页 | 现有 pending 待审 + diff 展示已具备雏形，缺 core 支持 |
| 换 LuckyPicker 安装包 | bat 第 103 行匹配模式/文件 | 现匹配 `LuckyPicker-win-setup-*.exe`，只找到旧版 1.2.3+25；新包中文名匹配不到 |

---

## 2. 核心闭环设计（请重点看逻辑）

### 2.1 五个角色与数据流

```
[教室端 ClassIsland]  --(1) 配置被改动 -->  [即时备份 Backups/changes/]
        |                                          |
        +--(2) 即时上传(files/base=head) --------> [远程站 upload.php]
                                                   |
                                          (3) 入 pending 待审队列
                                                   |
                                                   v
[你在家里] <--(4) 管理页看 diff、判断篡改/正常调整、手工合并--> [远程站 history.php 版本库]
        |
        +--(5) 发布新版本(bump version/etag) ------> [远程站 state.php]
                                                   |
[教室端轮询] <--(6) manifest 新版本 + LocalDirty=false --> 自动覆盖
        |
        +--(7) 覆盖前生成 diff 备份，再写新配置 ------> [Backups/diffs/]
```

### 2.2 关键：为什么不需要在教室端做合并引擎

你的补充「即时备份+上传，之后我在家里合并」把合并决策从机器移到了人：

- 教室端**不做**字段级 merge，只做两件简单事：**留痕**（备份）+ **上报**（上传）。
- 你在家里看服务端 diff，判断是同学篡改还是正常调整，手工合并成权威版本。
- 合并后的版本下发，教室端因为「本地变化已经上传过」而处于干净状态，可以放心覆盖。

这样避免了最危险的一环：让机器猜哪句提示该保留。代价是每次需要你在家点一次合并——这正是你要的审理权。

### 2.3 「无冲突自动覆盖」的精确定义（对应 Q1）

| 判定 | 定义 | 覆盖行为 |
| --- | --- | --- |
| 本地脏 (`LocalDirty=true`) | 本地文件的 SHA-256 ≠ `remote-state.json` 的 `LocalHashes` 基线 | **不覆盖**，先即时备份+上传；等上传成功/进入待审后基线更新 |
| 本地净 (`LocalDirty=false`) | 本地哈希 == 基线（含上传成功后 `SaveUploaded`/`SaveConflict` 更新的基线） | 轮询到服务器 version/etag 与本机不同 → **自动覆盖** + 生成 diff 备份 |

补充规则：

1. 上传进待审队列（409）后，`SaveConflict` 已把本次哈希记为基线 ⇒ 本地转净 ⇒ 服务器新版本可覆盖你已上报的现场调整。**这正是「你在家里合并」的前提**：你的合并版本里已经包含了对现场调整的判断。
2. 版本比较保留现有「不同即新」语义（服务器 version 是 `YYYYMMDD-HHMMSS-xxxx` 字符串，天然可当时间序）；不引入数值比较，回滚版本也能下发。
3. 覆盖前必须成功生成 diff 备份；备份失败则中止覆盖并记审计（防止无痕覆盖）。

### 2.4 两处备份的分工（对应 Q3）

| 时机 | 位置 | 内容 | 用途 |
| --- | --- | --- | --- |
| 本地配置**变化**被检测到（即时） | `Backups/changes/<时间戳>/<文件名>` | 变化后原文（只存变化文件，不整棵复制） | 现场留痕，即使后续覆盖也有据可查 |
| 服务器版本**覆盖**本地前 | `Backups/diffs/<version>-<文件名>.diff` | unified diff：旧本地 → 新服务器 | 你快速审阅「这次覆盖改了什么」 |
| 现有整份兜底 | `Backups/full-<时间戳>/` | 整棵插件配置目录 | 整份回滚（保留，不替代 diff） |

说明：服务端 `history.php` 已有 `cx_text_diff()`（LCS + 3 行上下文，3000 行降级摘要），diff 主要在服务端生成并展示在家里的管理页；教室端只生成覆盖前的本地 diff 文本。`YBH.json` 若为单行 JSON，服务端按行 diff 会退化成「整行替换」，需在实施时确认格式并考虑按顶层键切分（见 3.4）。

---

## 3. 白名单扩展清单

### 3.1 插件配置（`snapshot.pluginConfigs` 顶层键）

| 文件 | 现状 | 纳入后用途 | 风险 |
| --- | --- | --- | --- |
| `bell.json` / `duty.json` / `activities.json` / `layout.json` | 已在白名单 | 保持 | 无 |
| `bell-options.json` | **不在** | ByBreak 规则、`DefaultPrepMode` 可远程下发（解决上一轮的痛点） | 与档案 `AttachedObjects[a7e3c1b4]` 双写，需同步 |
| `lucky.json` | **不在** | 抽奖联动配置 | `HistoryPath` 可能含本机路径，需校验 |

### 3.2 核心配置（`snapshot.core`，客户端已实现、服务端需新增）

| 条目 | 映射 | 用途 | 风险 |
| --- | --- | --- | --- |
| `Settings.json` | `core.settingsJson` + `settingsBaseHash` | 宿主设置：上课提示开关、换课模式、周次、界面 | 198 个顶层键，需按白名单合并，禁止整份覆盖 |
| `Profiles/YBH.json` | `core.profiles["YBH.json"]` + `baseHash` | 时间表、课表、上课提示、换课、课表群 | **178 KB 单文件混装**；整份覆盖会丢失同学调整，需你家里合并时裁决 |
| `Config/ComponentLayouts/Default.json`、`offclass.json` | 建议作为 core 的第二组 profile-like 条目 | 上课/下课界面布局（含 `ClassScale`） | 同学下课时可能调过布局 |

### 3.3 明确排除（红线，请确认 Q4）

| 类别 | 文件 | 原因 |
| --- | --- | --- |
| 凭据 | `remote.json`（Bearer/HMAC）、`bridge.json`（token/pipeName）、`Credentials.json`、`Policy.json` | 上传会泄露；远程改向可反控教室端 |
| 同步状态 | `remote-state.json` | 运行时状态，上传会打乱基线 |
| 备份/缓存/日志 | `Backups/**`（含 989 层自嵌套 + 365 MB zip）、`Cache/**`、`Logs/**`、`*.bak*` | 体积失控、含历史凭据、无同步价值 |
| 插件本体 | `Plugins/**`（dll/pdb）、`PluginsIndex/**`、`.disabled` | 二进制与本机状态，非配置 |
| 机器特定 | `ReadAnnouncements.json`、`Tutorial.json`、`EnabledThemes.json`、`ThemePrivate*` | 本机已读/引导状态 |

服务端 `CoreConfigPolicy.ForbiddenKeyFragments`（credential/password/token/secret/policy/debug/update）必须**同时用于上传方向**，否则上传 `Settings.json` 会夹带敏感键。

### 3.4 容量与性能风险（需一并处理）

| 项 | 现状 | 扩后的风险 | 对策 |
| --- | --- | --- | --- |
| 单文件上限 | 512 KB | `YBH.json` 178 KB，OK | 保持；上传前本地校验 |
| 请求体上限 | 1 MB | 全量配置约 210 KB，OK | 保持 |
| diff 算法 | LCS O(n·m)，3000 行降级 | `Settings.json` 是单行 JSON；`YBH.json` 若为单行，diff 退化成整行 | 实施时确认格式；必要时按顶层键切块再做 diff |
| 存储膨胀 | 40 commits/设备，blob+diff 全在一个 `history.php` | 32 设备 × 全量配置会显著放大 | 调小 `CX_MAX_COMMITS`、blob 去重，监控文件大小 |
| 并发 | state/history 读-改-写无全局锁 | 心跳+上传+发布并发可能丢更新 | 加文件锁（`flock`） |

---

## 4. 部署脚本改造

### 4.1 上课提示保护流程（对应 Q2）

`19.bat` / `20.bat` 现为 GBK 编码，步骤调整为：

```
1. 管理员提权（不变）
2. 关闭 ClassIsland / LuckyPicker（不变）
3. ★ 备份教室端提示：读 %DST%\data\Profiles\YBH.json，抽取 31 处 08f0d9c3 对象 → <时间戳>.tips.json
4. robocopy /mir（不变，保证完整覆盖时间表/课表/设置/插件）
5. ★ 合并回去：调用 merge-class-tips.ps1，把备份的 31 处对象按「科目 GUID / 时间表 GUID+Layout 索引」写回
6. ★ 校验：合并后 JSON 可解析且命中数 > 0，失败则从备份还原 YBH.json 并中止
7. 重启 ClassIsland（从原第 91 行后移到此处）
8. 安装 LuckyPicker（换包，见 4.2）
```

合并脚本要点（推荐 Windows 自带 PowerShell 5.1，教室电脑无需 Python）：

- `Get-Content -Raw -Encoding UTF8` → `ConvertFrom-Json`；写回 `UTF8Encoding($false)` 保证无 BOM。
- 只覆盖白名单 13 个提示字段；`IsAttachSettingsEnabled=false` 原样保留，**绝不改成 true**。
- 目标有、备份无 ⇒ 用母版默认（新科目）；备份有、目标无 ⇒ 跳过并记日志（课表已删）。
- 实测 PS 5.1 解析 178 KB 文件约 58 ms、`ConvertTo-Json -Depth 100` 约 14 ms，但非压缩序列化会把 177,976 字符膨胀到 708,689 字符（格式重写）。若 ClassIsland 对格式敏感，改用「文本级片段替换」方案保留原始字节。

### 4.2 LuckyPicker 换包（对应 Q6）

- 新包已核实存在：`YBH摇人器-WPF-1.2.4+26-内部版-Setup.exe`，71,214,080 B，`ProductVersion=1.2.4+26`，SHA256 `961E4B47968BE9AAA03092A6B87C96EA4B4208EAD56EB584455805DAA3B568ED`。同目录有公开版，**必须区分**。
- 现 bat 第 103 行：`dir /b /a-d /o-d "%SCRIPT_DIR%LuckyPicker-win-setup-*.exe"` → 只匹配到旧版 `LuckyPicker-win-setup-1.2.3+build25.exe`。
- 建议：把新内部版复制为 `D:\我的文档\教学喵喵包\LuckyPicker-win-setup-1.2.4+26.exe`（ASCII 名），旧包移走/改名。`dir /o-d` 按修改时间取最新，可再加一行按版本号校验 `DisplayVersion`。
- 备选：bat 里用 `dir /x` 取 8.3 短名引用中文路径——不稳，不作为首选。

---

## 5. 服务端改动清单

按依赖顺序（前 4 项是必须，否则版本管理不可信）：

| # | 文件 | 改动 |
| --- | --- | --- |
| S1 | `lib.php:19` | `CX_ALLOWED_KEYS` 升级为「配置键集合」，区分 pluginConfigs 与 core 两个命名空间；`CX_MAX_BODY` 评估上调 |
| S2 | `upload.php:52-53` | 修复 HMAC 大小写：C# 发大写 hex，服务端 `hash_equals` 前需 `strtolower` 归一化 |
| S3 | `history.php:172-177` + `upload.php:160` | 修复 commit diff 恒为「新增」：读 `$c['tree']` 并 `cx_commit_files()`，`parent` 传 commit id 而非 tree hash |
| S4 | `history.php:266-268` | etag 口径纳入 head，避免「只 head 变」时客户端漏同步 |
| S5 | `manifest.php` + `lib.php:339-368` | 输出 `snapshot.core`（settingsJson/baseHash + profiles 的 json/baseHash），保持原文嵌入不重编码 |
| S6 | `upload.php` | 接收 core 上传字段，计入 head、tree、commit、pending |
| S7 | `index.php:229-246,363-417,635-639` | 管理页支持 core 编辑、pending diff 展示、采用/回滚；`f_` 字段改索引数组避免撞名 |
| S8 | `history.php` | 按顶层键切块 diff；加 `flock`；调 `CX_MAX_COMMITS`/`CX_DIFF_LIMIT` |
| S9 | 文档 | 更新 `docs/REMOTE-SYNC.md` 的「只允许四个文件」「单层文件名」约定 |

---

## 6. 客户端改动清单

| # | 文件 | 改动 |
| --- | --- | --- |
| C1 | `RemoteLocalSnapshot.cs:13-16` | `FileNames` 扩到 6 个；新增 core 哈希（key 加 `core:` 前缀区分） |
| C2 | `RemoteSyncModule.Upload.cs:141-147` | 上传 body 带 core 字段；上传方向套用 `CoreConfigPolicy` 敏感键过滤 |
| C3 | `RemoteSyncModule.Upload.cs:83-89` | 实现 Q1 判定：`NeedApply && !LocalDirty` 才覆盖；脏时先备份+上传 |
| C4 | `SafeConfigWriter.cs` | 新增 `Backups/changes/` 即时备份与 `Backups/diffs/` 覆盖前 diff 生成 |
| C5 | `CoreConfigPolicy.cs:19-34` | 扩充真实存在的设置键（时间表/课表/换课/界面）；把错位到 Settings.json 的提示键移到 Profile 合并规则 |
| C6 | `CoreConfigWriter.cs:60-64` | `SettingsServiceBridge` 热生效路径补 baseHash 冲突检测（现在绕过了） |
| C7 | `RemoteSyncModule.cs:465-486` | core 应用后通知宿主重载 Settings/Profile，避免要重启才生效 |
| C8 | `tests/` | 新增覆盖判定、备份、core 同步、敏感键过滤的测试 |

---

## 7. 风险与边界

1. **安全面扩大**：`Settings.json`/档案含学生姓名、课表；依赖 HTTPS + Bearer + HMAC + 服务端敏感键过滤。服务端 `data/` 绝不可公开访问。
2. **误判风险**：Q1 用 (b) 时，若你在家合并漏看了现场调整，该调整会被覆盖——但 diff 备份仍在，可追溯。
3. **格式兼容**：PowerShell 重写 `YBH.json` 格式可能让 ClassIsland 异常，需先在一台教室机小范围试。
4. **服务端容量**：全量配置 + 多设备 + 长历史会让 `data/history.php` 膨胀，需监控。
5. **20.bat 源包矛盾**：`DEPLOY-20261128.md` 写「唯一母版 = ClassIsland-19」，但 `20.bat` 的 `SRC=ClassIsland-20`。需你确认是否改成同指 19（靠 `SelectedClassPlanGroupId` 区分），本次方案暂按「沿用现状」处理，未擅改。

## 8. 实施顺序与验收

1. 服务端 S1–S4（修 bug + 扩白名单）→ 在测试设备验证上传/待审/历史 diff。
2. 服务端 S5–S8（core 通道 + 管理页合并）→ 你在家里试看 diff、试合并发布。
3. 客户端 C1–C8 → 本机单测 + 一台教室机灰度（只开 `bell-options.json` 与 `Settings.json`）。
4. 部署脚本 4.1 + 4.2 → 先在 19 机跑，确认 31 处提示原样保留、LuckyPicker 显示 1.2.4+26。
5. 全量放开白名单 → 观察一个课间→上课周期，确认提示、课表、换课、设置均按预期。

## 9. 与上一轮任务的衔接

上一轮已发版 v1.0.0.17、两台设备源包已配好、远程站四文件快照已发布。本方案是**增量扩展**，不推翻已部署内容；尤其 `bell-options.json` 从「只能靠 bat 同步」升级为「可远程下发」，会补上上一轮记录的白名单限制。
