# 远程配置同步

远程同步是 ClassExland 的**可选、高风险能力**，默认关闭。它采用"**快照（snapshot）+ 指令队列（command queue）+ 本地变更上传**"三种形态：快照用于服务端下发，指令用于临时操控，上传用于把本机现有配置交给服务器；指令与 CLI 共用同一命令层（`ICommandDispatcher`）。

服务端按设备保存**内容哈希版本库**（见「双向同步」一节），一个站可以同时管多间教室。

## 在设置页里配置（推荐）

设置 → **ClassExland 远程控制**：

1. 填「服务器地址」（必须 `https://` 开头）与管理员提供的「访问令牌」；
2. 选一个「设备名称」（可以从服务器下拉里挑，也可以手填新名字）；
3. 点「保存并测试」——只发一次 GET 读回清单并验签，**不应用任何配置**；
4. 确认提示"连接成功"后，再点「立即同步」应用；首次同步完成后，本机现有的 `bell/duty/activities/layout`
   会在下一轮轮询里整体上传（含 `duty.json` 里的人名，确认可以放在自托管服务器上再用）；
5. 想立刻上传点「上传当前配置」；「自动上传」与轮询间隔在「允许远程做什么」里调；
6. 只想在这台电脑上操作，可勾选「启用本机网页控制台」，点「打开」直接在浏览器里用（仅监听 `127.0.0.1`）。

清单路径、上传路径、签名密钥、超时都在同一页的「高级选项」里，日常不需要改。手动编辑 `remote.json` 仍然有效，字段见下文。

## 安全约定（必须遵守）

- **地址不写进代码**：服务器地址只写在外部配置 `remote.json` 的 `ServerUrl`（或环境变量），插件二进制中不含任何服务器地址。
- **用户自托管**：插件不提供也不依赖任何官方/第三方服务器；你需要自己搭建并维护服务端。
- **仅 HTTPS**：`ServerUrl` 必须以 `https://` 开头，证书校验默认开启；不允许通过配置关闭校验（本地开发需显式、单独的开关）。
- **令牌 + HMAC 双因子**：每个请求携带 `Authorization: Bearer <token>`；配了签名密钥时，服务端返回
  `X-ClassExland-Signature: hex(hmac_sha256(密钥, 响应原文))`，客户端对**原始字节**验签。
  令牌与密钥只存服务器与本机 `remote.json`，**不硬编码、不进仓库**。
- **离线可用**：无网络时插件照常本地工作；同步失败只记审计日志并保留本地配置，不影响上课铃与值日提醒。
- **失败回滚**：任何写入前先备份；签名校验失败、版本冲突或中途出错时回滚到上一版本快照。
- **可停用**：`remote.json` 的 `Enabled: false` 或设置页一键关闭；关闭后不再发起任何网络请求。

同步范围（在 `remote.json` 的 `Scope` 中逐项开关）：插件配置（默认开）、ClassIsland 档案（默认关，高风险）、ClassIsland 全局设置（默认关，高风险）。

## 服务端部署约定

服务端只需提供一个静态 JSON（路径写进 `remote.json` 的 `ManifestPath`，默认 `/config/manifest.json`）：

| 端点 | 方法 | 说明 |
|---|---|---|
| `/config/manifest.json` | GET | 返回当前清单（快照 + 指令队列 + `head` 内容哈希）；建议支持 `ETag` |
| `/remote/upload.php` | POST | 上传本机配置原文；带 `base`（上次看到的 head）做快进/冲突判定 |

- 客户端用 `Bearer` 令牌鉴权，可选 HMAC-SHA256 验签（上行也签请求体原文）。
- 版本：清单里放一个单调递增的 `version` 与唯一 `etag`；两者都没变则客户端直接跳过。
- 远程下发的指令只在本机执行，执行结果写本机 `audit.log`，不回传服务器。

## 双向同步：本地变更如何上传

插件启动后先拉一次清单；之后按 `PollIntervalSeconds`（默认 60 秒）周期轮询，每轮：

1. **先看服务端**：`version`/`etag` 变了就走完整同步（备份 → 校验 → 写入 → 热重载 → 指令）；
2. **再看本地**：对 `bell.json` / `duty.json` / `activities.json` / `layout.json` 逐文件算 SHA-256，
   与上次同步时记在 `remote-state.json` 的 `LocalHashes` 比较；
3. **有变化就 POST 上传**：请求体 `{"base":"<head>","files":{"…":"原文"},"message":"…"}`，
   原文**不重新编码**，避免 PascalCase 被 JSON 库改掉。

服务端用 `base` 与当前快照的 tree 哈希比对：

- **一致** → 快进：生成一个新提交（parent 指向旧 head）、更新当前快照，返回 200；
- **不一致（两边都改过）** → 服务端裁决：默认 `review` 策略把上传收进**待审队列**并返回 409，
  当前快照不动；管理员在面板看 diff 后「采用」或「丢弃」。策略改成 `auto` 则一律接受，
  但旧版本仍留在历史里，随时可回滚。

服务端存储是轻量「Git 式」结构（`data/history.php`）：blob 按 SHA-256 内容寻址（相同内容只存一份），
commit 记录 parent / tree / 逐行 diff，每台设备独立；`data/state.php` 只留当前快照与访问日志，
所以每轮心跳不会重写历史文件。管理页可以看每个设备的版本历史、diff、待审冲突并回滚。

上传内容含真实学生姓名时请确认自托管服务器与令牌安全；不想上传值日表就别在本机改 `duty.json`，
或关掉「自动上传」只用手动上传其他文件（上传内容是四个白名单文件的当前并集）。

## 开箱可用的远程站（PHP）

`server/remote-station/` 是一个完整的参考实现，放到任意 PHP 8 主机即可：

| 文件 | 作用 |
|---|---|
| `manifest.php` | 插件拉取的清单端点（Bearer 鉴权 + 可选签名头，按 `?device=` 区分设备） |
| `upload.php` | 客户端上传端点（Bearer + 可选请求体签名，按 head 快进或进待审队列） |
| `index.php` | 管理界面：设备、版本历史/diff、待审冲突、快照发布、指令队列、改令牌/密钥/密码 |
| `lib.php` | 公共库（读写状态、签名、鉴权） |
| `history.php` | 轻量版本库（blob / commit / diff / 回滚） |
| `data/` | 自动生成，存 `config.php`、`state.php`（当前快照与日志）与 `history.php`（版本历史） |

部署步骤：

1. 把五个 PHP 文件传到站点子目录（例如 `/www/wwwroot/<站点>/remote/`）；
2. 把 `install.php` 也传上去，执行 `php install.php '<管理员密码>'`（至少 8 位）。
   脚本会生成 `data/config.php`、`data/state.php` 与 `data/history.php`，打印访问令牌与签名密钥，然后自删
   （它只接受命令行运行，Web 访问返回 404）；
3. `chown -R www:www remote && chmod 750 remote/data && chmod 640 remote/data/*.php`；
4. 浏览器打开 `https://<站点>/remote/`，用管理员密码登录，把「服务器地址 + 清单路径 + 设备名 + 令牌」填进插件。

`data/` 里这些文件都以 `<?php exit; ?>` 开头、后面跟 JSON：直接访问时 PHP 立即退出、不输出内容，
读取走 `file_get_contents` 而不是 `include`——宝塔的 opcache 可能关闭了时间戳校验，
用 `include` 会把刚发布的版本读回旧值（实测踩过）。

## 多台设备：一个站管多间教室

一个人管好几间教室的设备时，不必给每台电脑开一个站——同一个站里按名字登记设备即可：

1. 管理页「设备」卡片里新增一台，名字自己起（例如 `初一2班`、`录播教室`），可以加一句备注；
2. 在插件「远程控制」页把**设备名称**填成同一个名字——懒得手打就点「从服务器读取设备列表」，
   下拉里选一台，名字会自动填进去；客户端请求会变成
   `GET /manifest.php?device=初一2班`，只拿这一台设备的快照与指令；
3. 管理页顶部的「当前设备」决定下面的快照与指令队列作用于哪一台，随时可切换。

> 设备列表走 `GET /manifest.php?list=1`（同样要 Bearer 令牌），只返回名字、备注、版本与最近拉取时间，
> **不含任何配置内容**，所以给别人看这一屏也不会泄露名单。

约定与兜底：

- **每台设备有独立的 `version` / `etag` / 快照 / 指令队列**，互不影响；几个班共用同一份值日表时，
  在管理页「以当前设备为模板复制一台」即可，复制出来的设备自带同样的快照与指令队列，
  版本号是新的（客户端会正常认到变化）。
- 客户端只要带着设备名拉过一次，就会自动出现在设备表里，并显示最近拉取时间与来源 IP——
  **没有登记也能"自动注册"**（备注写"自动登记"），之后再去补备注就行。自动注册只接受合法设备名，
  且总量上限 32 台。
- 服务器上只有一台设备时，客户端不填设备名也能用（向后兼容）；**多于一台而不指定**会返回
  `400 {"error":"device_required","devices":[…]}`，插件会把这句翻译成人话并列出可选名字。
- 名字**写错不会报错**，而是多登记一台空设备（快照为空 ⇒ 本地配置不会被改动），
  在管理页把多余那台删掉即可；名字里出现非法字符是 `400 invalid_device_name`，
  设备数已满 32 台是 `429 too_many_devices`。
- 设备名只允许中英文、数字、空格与 `-_.()`，最长 40 字符；客户端的 `DeviceName` 会做同样的收敛，
  所以不会出现"填了但发不出去"的情况。

## 存储与排错

`data/` 里两个文件都以 `<?php exit; ?>` 开头、后面跟 JSON：直接访问时 PHP 立即退出、不输出内容，
读取走 `file_get_contents` 而不是 `include`——宝塔的 opcache 可能关闭了时间戳校验，
用 `include` 会把刚发布的版本读回旧值（实测踩过）。

这些文件**必须归 `www` 所有**。用 root 在命令行跑过脚本（迁移、修数据之类）之后，文件属主会变成 root，
php-fpm 读不到；此时管理页会明确报「远程站读不到数据文件」并给出修复命令，
`manifest.php` 返回 `500 {"error":"station_storage_error"}`——**不会再静默当成空状态清掉已发布的快照**。

```bash
chown -R www:www /www/wwwroot/<站点>/remote
chmod 750 /www/wwwroot/<站点>/remote/data
chmod 640 /www/wwwroot/<站点>/remote/data/*.php
```

## 安全约定

- 管理密码用 `password_hash`，令牌比较用 `hash_equals`；表单带 CSRF 校验，Cookie 为 `HttpOnly` + `SameSite=Lax`。
- 只允许发布 `bell.json` / `duty.json` / `activities.json` / `layout.json` 四个白名单文件，
  每个都必须是合法 JSON；客户端还会再做一次 `CoreConfigPolicy` 校验。
- 令牌、签名密钥、学生名单都在 `data/` 里，**不要提交到仓库，也不要放进可公开访问的目录**。
- 想彻底关掉签名，把 `HmacSecret` 留空即可（仅剩 HTTPS + Bearer，仍是加密通道）。

## 清单 JSON 格式（实际实现）

客户端只发一个 `GET`：`ServerUrl + ManifestPath`，配了设备名时再加 `?device=<名字>`。
快照与指令都在**同一份清单**里，不需要额外的 `/commands.json` 端点。
响应头里还有 `X-ClassExland-Signature`，设备名回写在 `device` 字段上（方便确认自己连的是哪一台）。

```json
{
  "version": "7",
  "etag": "v7-9f2c1a",
  "snapshot": {
    "pluginConfigs": {
      "bell.json": [ { "id": "<slotKey>", "prepSeconds": 90, "enabled": true } ],
      "bell-options.json": { "DefaultPrepSeconds": 120, "EnableBell": true },
      "duty.json": { "...": "见 CONFIGURATION.md" }
    },
    "core": {
      "settingsJson": { "...": "仅 EnableCoreConfigSync=true 时应用" },
      "settingsBaseHash": "",
      "profiles": {
        "YBH.json": { "json": { "...": "..." }, "baseHash": "" }
      }
    }
  },
  "commands": [
    { "command": "bell.set", "args": { "index": "3", "prep": "90" }, "apply": true, "dryRun": false }
  ],
  "signature": "<响应原文的 HMAC-SHA256 十六进制串>"
}
```

- `pluginConfigs` 的键必须是**单层文件名 + `.json`**，且不能是 `remote.json` / `remote-state.json`（防止被远程改向或关闭）；其他非法名整条拒绝。
- `version` + `etag` 都相同 ⇒ 跳过，不重复应用。
- `signature`：配置了 `HmacSecret` 时必填，值是对**响应体原文**做 `HMAC-SHA256` 的十六进制串（不是对某个子集重算）。
- 写入顺序：整份备份 → 逐个插件配置（备份 + JSON 校验 + 原子替换）→ 核心配置（可选）→ 指令队列。
- 写了 `bell.json` / `bell-options.json` 后会自动热重载并同步档案，逐节提前量立即生效。
- 任一环节失败 ⇒ 回滚本批已写入的插件配置，保留本地原文件。

## 指令项格式

```json
{
  "command": "bell.set",
  "args": { "index": "3", "prep": "90" },
  "apply": true,
  "dryRun": false
}
```

- `command` 用点分名（`bell.set` / `layout.switch` / `class.set`…），与 CLI 共用同一命令层，参数名也一致（见 [CLI.md](CLI.md)）。
- `apply: true` 才会真正落盘；高危命令仍然要求 `args.confirm = "yes"`，与 CLI 的安全模型完全相同。
- 远程来源的每次执行都会写进 `audit.log`（`source=remote`）。

## 服务端生成清单的最小示例（Python）

演示"如何签出客户端认得的格式"，不含任何真实地址或密钥。

```python
import hashlib, hmac, json, pathlib

SECRET = b"<与 remote.json 的 HmacSecret 一致>"

manifest = {
    "version": "2026-11-19-1",
    "etag": "v1",
    "snapshot": {
        "pluginConfigs": {
            # 逐节课改预备铃：整份数组，id 用 cex bell list 输出的 id
            "bell.json": [
                {"id": "<slotKey>", "prepSeconds": 90, "enabled": True},
            ],
        }
    },
    "commands": [],
}

# 客户端会先去掉 signature 字段再算 HMAC，所以这里用紧凑 JSON（无缩进）签名
payload = json.dumps(manifest, ensure_ascii=False, separators=(",", ":"))
manifest["signature"] = hmac.new(SECRET, payload.encode("utf-8"), hashlib.sha256).hexdigest()

pathlib.Path("manifest.json").write_text(
    json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
```

注意两点：

- 签名是 **十六进制**，不是 base64；对 `separators=(",", ":")` 的紧凑 JSON 计算。
- 客户端比较字符串时忽略大小写差异（`Convert.ToHexString` 输出大写），服务端用小写也能通过。

## 出问题怎么办

- 设置页一键关闭远程同步，或把 `remote.json` 的 `Enabled` 改为 `false`——插件立即回到纯本地模式。
- 用 `cex backup / restore` 回滚到同步前的配置。
- 查看配置目录下审计日志（`audit.log`）中的同步记录，定位是哪次拉取/写入失败。
