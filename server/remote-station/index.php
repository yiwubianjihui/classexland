<?php
declare(strict_types=1);

require __DIR__ . '/lib.php';
cx_boot();

session_name('cxstation');
session_set_cookie_params(['httponly' => true, 'samesite' => 'Lax', 'path' => '/']);
session_start();

$config = cx_load_config();
$state  = cx_load_state();
$needsSetup = ($config['admin_password'] === '');
$loggedIn = !empty($_SESSION['cx_ok']);

if (empty($_SESSION['csrf'])) {
    $_SESSION['csrf'] = bin2hex(random_bytes(16));
}
$csrf = $_SESSION['csrf'];

function cx_redirect(string $message): void
{
    $_SESSION['flash'] = $message;
    header('Location: ' . strtok($_SERVER['REQUEST_URI'], '?'));
    exit;
}

function cx_require_csrf(string $csrf): void
{
    if (!hash_equals($csrf, (string)($_POST['csrf'] ?? ''))) {
        http_response_code(400);
        exit('CSRF 校验失败，请刷新页面重试。');
    }
}

function cx_require_login(bool $loggedIn): void
{
    if (!$loggedIn) {
        http_response_code(403);
        exit('未登录。');
    }
}

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $action = (string)($_POST['action'] ?? '');

    if ($action === 'setup' && $needsSetup) {
        cx_require_csrf($csrf);
        $password = (string)($_POST['password'] ?? '');
        if (strlen($password) < 8) {
            cx_redirect('管理员密码至少 8 位。');
        }
        $config['admin_password'] = password_hash($password, PASSWORD_DEFAULT);
        $config['api_token'] = cx_new_token();
        $config['hmac_secret'] = cx_new_token(32);
        if (!cx_save_config($config)) {
            cx_redirect('写入配置失败，请检查 data 目录权限。');
        }
        $_SESSION['cx_ok'] = true;
        session_regenerate_id(true);
        cx_redirect('初始化完成。下面的访问令牌与签名密钥需要填进 ClassExland 的远程控制页。');
    }

    if ($action === 'login') {
        cx_require_csrf($csrf);
        if (password_verify((string)($_POST['password'] ?? ''), (string)$config['admin_password'])) {
            $_SESSION['cx_ok'] = true;
            session_regenerate_id(true);
            cx_redirect('已登录。');
        }
        usleep(300000);
        cx_redirect('密码不正确。');
    }

    if ($action === 'logout') {
        cx_require_csrf($csrf);
        $_SESSION = [];
        session_destroy();
        header('Location: ' . strtok($_SERVER['REQUEST_URI'], '?'));
        exit;
    }

    cx_require_login($loggedIn);
    cx_require_csrf($csrf);

    if ($action === 'publish') {
        $files = [];
        $errors = [];
        foreach (CX_ALLOWED_KEYS as $key) {
            $field = 'f_' . str_replace(['.', '-'], '_', $key);
            $raw = trim((string)($_POST[$field] ?? ''));
            if ($raw === '') {
                continue;
            }
            if (strlen($raw) > CX_MAX_BODY) {
                $errors[] = $key . ' 超过 512 KB';
                continue;
            }
            if (!cx_is_json($raw)) {
                $errors[] = $key . ' 不是合法 JSON';
                continue;
            }
            $files[$key] = $raw;
        }

        if ($errors) {
            cx_redirect('未发布：' . implode('；', $errors));
        }

        $state['files'] = $files;
        $state['version'] = date('Ymd-His') . '-' . substr(bin2hex(random_bytes(2)), 0, 4);
        $state['etag'] = substr(hash('sha256', cx_build_manifest($state)), 0, 12);
        $state['updated_at'] = date('Y-m-d H:i:s');
        if (!cx_save_state($state)) {
            cx_redirect('写入状态失败，请检查 data 目录权限。');
        }
        cx_log_request('publish', 200, 'version=' . $state['version'] . ' files=' . count($files));
        cx_redirect('已发布版本 ' . $state['version'] . '，客户端下次同步即生效。');
    }

    if ($action === 'command_add') {
        $command = trim((string)($_POST['command'] ?? ''));
        $argsRaw = trim((string)($_POST['args'] ?? ''));
        if ($command === '') {
            cx_redirect('指令名不能为空。');
        }
        $args = [];
        if ($argsRaw !== '') {
            $decoded = json_decode($argsRaw, true);
            if (!is_array($decoded)) {
                cx_redirect('参数必须是 JSON 对象，例如 {"to":"class"}。');
            }
            foreach ($decoded as $k => $v) {
                if (!is_scalar($v) && $v !== null) {
                    cx_redirect('参数值只能是字符串或数字。');
                }
                $args[(string)$k] = $v === null ? '' : (string)$v;
            }
        }
        $state['commands'][] = [
            'command' => $command,
            'args'    => $args,
            'apply'   => !empty($_POST['apply']),
            'dryRun'  => false,
        ];
        $state['version'] = date('Ymd-His') . '-' . substr(bin2hex(random_bytes(2)), 0, 4);
        $state['etag'] = substr(hash('sha256', cx_build_manifest($state)), 0, 12);
        $state['updated_at'] = date('Y-m-d H:i:s');
        cx_save_state($state);
        cx_log_request('command_add', 200, $command);
        cx_redirect('已加入指令队列：' . $command . '（客户端下次同步时执行）');
    }

    if ($action === 'command_clear') {
        $state['commands'] = [];
        $state['version'] = date('Ymd-His') . '-' . substr(bin2hex(random_bytes(2)), 0, 4);
        $state['etag'] = substr(hash('sha256', cx_build_manifest($state)), 0, 12);
        $state['updated_at'] = date('Y-m-d H:i:s');
        cx_save_state($state);
        cx_log_request('command_clear', 200, '');
        cx_redirect('指令队列已清空。');
    }

    if ($action === 'settings_security') {
        $newToken = trim((string)($_POST['api_token'] ?? ''));
        if ($newToken === '') {
            $newToken = cx_new_token();
        }
        $config['api_token'] = $newToken;

        if (!empty($_POST['clear_hmac'])) {
            $config['hmac_secret'] = '';
        } elseif (trim((string)($_POST['hmac_secret'] ?? '')) !== '') {
            $config['hmac_secret'] = trim((string)$_POST['hmac_secret']);
        }

        $newPassword = (string)($_POST['new_password'] ?? '');
        if ($newPassword !== '') {
            if (strlen($newPassword) < 8) {
                cx_redirect('新密码至少 8 位。');
            }
            if (!password_verify((string)($_POST['current_password'] ?? ''), (string)$config['admin_password'])) {
                cx_redirect('当前密码不正确，安全设置未修改。');
            }
            $config['admin_password'] = password_hash($newPassword, PASSWORD_DEFAULT);
        }

        if (!cx_save_config($config)) {
            cx_redirect('写入配置失败，请检查 data 目录权限。');
        }
        cx_log_request('settings', 200, '');
        cx_redirect('安全设置已保存。改了令牌或密钥后，记得在客户端同步更新。');
    }
}

$flash = (string)($_SESSION['flash'] ?? '');
unset($_SESSION['flash']);

$base = cx_current_url();
$manifestPath = rtrim(dirname($_SERVER['PHP_SELF']), '/\\') . '/manifest.php';
$manifestUrl = $base . $manifestPath;
$version = $state['version'] !== '' ? $state['version'] : '尚未发布';
$updated = $state['updated_at'] !== '' ? $state['updated_at'] : '—';
$fileCount = count($state['files']);
$commandCount = count($state['commands']);
$token = (string)$config['api_token'];
$hmac = (string)$config['hmac_secret'];
?>
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>ClassExland 远程站</title>
<style>
  :root{color-scheme:light dark;--bg:#f4f6f8;--card:#fff;--fg:#1b1f24;--muted:#6a737d;--line:#e2e6ea;--accent:#2f6fed;--ok:#1a7f37;--warn:#b45309;--danger:#c0392b}
  @media (prefers-color-scheme:dark){:root{--bg:#14171a;--card:#1c2024;--fg:#e9edf2;--muted:#98a2ad;--line:#2a3036;--accent:#6b9dff;--ok:#4ac26b;--warn:#f0b429;--danger:#ff7b72}}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.55 system-ui,"Segoe UI","Microsoft YaHei",sans-serif}
  .wrap{max-width:980px;margin:0 auto;padding:28px 18px 64px}
  header{display:flex;align-items:baseline;gap:10px;margin-bottom:16px;flex-wrap:wrap}
  header h1{font-size:19px;margin:0}
  .tag{font-size:12px;color:var(--muted);border:1px solid var(--line);border-radius:99px;padding:2px 9px}
  .card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px;margin-bottom:14px}
  h2{font-size:15px;margin:0 0 10px}
  h3{font-size:13px;margin:16px 0 6px;color:var(--muted)}
  .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}
  .stat{background:var(--bg);border:1px solid var(--line);border-radius:10px;padding:10px 12px}
  .stat b{display:block;font-size:16px;margin-top:2px;word-break:break-all}
  .muted{color:var(--muted);font-size:13px}
  textarea,input[type=text],input[type=password],select{width:100%;font:inherit;padding:9px 10px;border-radius:9px;border:1px solid var(--line);background:var(--bg);color:var(--fg)}
  textarea{font-family:Consolas,Menlo,monospace;font-size:12.5px;line-height:1.5;min-height:120px;resize:vertical}
  label{display:block;font-size:13px;color:var(--muted);margin:10px 0 4px}
  button{font:inherit;padding:9px 14px;border-radius:9px;border:1px solid var(--line);background:var(--card);color:var(--fg);cursor:pointer}
  button:hover{border-color:var(--accent)}
  button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
  button.danger{color:var(--danger);border-color:var(--danger)}
  .row{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-top:10px}
  .flash{background:color-mix(in srgb,var(--ok) 12%,transparent);border:1px solid color-mix(in srgb,var(--ok) 40%,var(--line));color:var(--fg);padding:10px 12px;border-radius:10px;margin-bottom:14px}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{text-align:left;padding:6px 4px;border-bottom:1px dashed var(--line);vertical-align:top}
  code{font-family:Consolas,Menlo,monospace;font-size:12.5px;word-break:break-all}
  .kv{display:grid;grid-template-columns:120px 1fr;gap:6px 12px;font-size:13.5px;align-items:center}
  .kv span{color:var(--muted)}
  .pill{display:inline-block;font-size:12px;padding:1px 8px;border-radius:99px;border:1px solid var(--line);color:var(--muted)}
  details summary{cursor:pointer;color:var(--muted);font-size:13px}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <h1>ClassExland 远程站</h1>
    <span class="tag">配置下发与指令队列</span>
    <?php if ($loggedIn): ?><span class="tag">已登录</span><?php endif; ?>
  </header>

  <?php if ($flash !== ''): ?><div class="flash"><?= cx_h($flash) ?></div><?php endif; ?>

  <?php if ($needsSetup): ?>
    <section class="card">
      <h2>首次初始化</h2>
      <p class="muted">设置一个管理员密码。系统会同时生成访问令牌与签名密钥，稍后填进 ClassExland 的「远程控制」页。</p>
      <form method="post">
        <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
        <input type="hidden" name="action" value="setup">
        <label>管理员密码（至少 8 位）</label>
        <input type="password" name="password" required minlength="8" autocomplete="new-password">
        <div class="row"><button class="primary" type="submit">初始化</button></div>
      </form>
    </section>
  <?php elseif (!$loggedIn): ?>
    <section class="card">
      <h2>登录</h2>
      <form method="post">
        <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
        <input type="hidden" name="action" value="login">
        <label>管理员密码</label>
        <input type="password" name="password" required autofocus autocomplete="current-password">
        <div class="row"><button class="primary" type="submit">登录</button></div>
      </form>
    </section>
  <?php else: ?>

  <section class="card">
    <h2>当前状态</h2>
    <div class="grid">
      <div class="stat"><span class="muted">已发布版本</span><b><?= cx_h($version) ?></b></div>
      <div class="stat"><span class="muted">更新时间</span><b><?= cx_h($updated) ?></b></div>
      <div class="stat"><span class="muted">配置文件</span><b><?= $fileCount ?> 个</b></div>
      <div class="stat"><span class="muted">待执行指令</span><b><?= $commandCount ?> 条</b></div>
    </div>
  </section>

  <section class="card">
    <h2>客户端填这些</h2>
    <div class="kv">
      <span>服务器地址</span><div><code><?= cx_h($base) ?></code></div>
      <span>清单路径</span><div><code><?= cx_h($manifestPath) ?></code></div>
      <span>访问令牌</span><div><code><?= cx_h($token) ?></code></div>
      <span>签名密钥</span><div><code><?= $hmac === '' ? '（未启用）' : cx_h($hmac) ?></code></div>
    </div>
    <p class="muted">在 ClassExland → 远程控制里填入服务器地址与令牌；签名密钥可留空。地址和令牌只显示给已登录的管理员。</p>
  </section>

  <section class="card">
    <h2>配置快照</h2>
    <p class="muted">留空的文件不会写进快照（客户端保持原样）。保存后会生成新版本，客户端下次同步时应用。</p>
    <form method="post">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="publish">
      <?php foreach (CX_ALLOWED_KEYS as $key): ?>
        <h3><?= cx_h($key) ?></h3>
        <textarea name="f_<?= cx_h(str_replace(['.', '-'], '_', $key)) ?>"
                  placeholder="留空表示不下发这个文件"><?= cx_h(isset($state['files'][$key]) ? cx_pretty((string)$state['files'][$key]) : '') ?></textarea>
      <?php endforeach; ?>
      <div class="row"><button class="primary" type="submit">保存并发布</button></div>
    </form>
  </section>

  <section class="card">
    <h2>指令队列</h2>
    <p class="muted">指令与 CLI 共用命令层。客户端同步时按顺序执行，执行后版本会前进。</p>
    <form method="post">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="command_add">
      <label>指令</label>
      <select name="command">
        <option value="layout.switch">layout.switch（切换上课/下课布局）</option>
        <option value="bell.set">bell.set（设置某节课预备铃）</option>
        <option value="duty.rotate">duty.rotate（轮换到下一批值日）</option>
        <option value="remote.sync">remote.sync（让客户端立即再拉一次）</option>
      </select>
      <label>参数（JSON，值必须是字符串）</label>
      <input type="text" name="args" placeholder='{"to":"class"} 或 {"index":"3","prep":"90"}'>
      <div class="row">
        <label style="margin:0"><input type="checkbox" name="apply" checked> 真正执行（不勾选只预览）</label>
        <button type="submit">加入队列</button>
      </div>
    </form>

    <?php if ($commandCount > 0): ?>
      <table style="margin-top:14px">
        <tr><th>指令</th><th>参数</th><th>执行</th></tr>
        <?php foreach ($state['commands'] as $cmd): ?>
          <tr>
            <td><code><?= cx_h((string)($cmd['command'] ?? '')) ?></code></td>
            <td><code><?= cx_h(json_encode($cmd['args'] ?? [], JSON_UNESCAPED_UNICODE)) ?></code></td>
            <td><?= !empty($cmd['apply']) ? '是' : '预览' ?></td>
          </tr>
        <?php endforeach; ?>
      </table>
      <form method="post" style="margin-top:10px">
        <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
        <input type="hidden" name="action" value="command_clear">
        <button class="danger" type="submit">清空队列</button>
      </form>
    <?php endif; ?>
  </section>

  <section class="card">
    <h2>安全设置</h2>
    <form method="post">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="settings_security">
      <label>访问令牌（留空 = 重新生成）</label>
      <input type="text" name="api_token" placeholder="留空会自动生成新的">
      <label>签名密钥（留空 = 保持不变）</label>
      <input type="text" name="hmac_secret" placeholder="留空保持不变">
      <div class="row">
        <label style="margin:0"><input type="checkbox" name="clear_hmac"> 关闭签名校验（清空密钥）</label>
      </div>
      <h3>修改管理员密码</h3>
      <label>当前密码</label>
      <input type="password" name="current_password" autocomplete="current-password">
      <label>新密码（至少 8 位，留空不改）</label>
      <input type="password" name="new_password" autocomplete="new-password">
      <div class="row">
        <button class="primary" type="submit">保存安全设置</button>
      </div>
    </form>
    <form method="post" style="margin-top:12px">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="logout">
      <button type="submit">退出登录</button>
    </form>
  </section>

  <section class="card">
    <h2>最近访问</h2>
    <?php if (empty($state['requests'])): ?>
      <p class="muted">还没有记录。</p>
    <?php else: ?>
      <table>
        <tr><th>时间</th><th>来源</th><th>动作</th><th>结果</th><th>说明</th></tr>
        <?php foreach (array_slice($state['requests'], 0, 20) as $r): ?>
          <tr>
            <td><?= cx_h((string)($r['at'] ?? '')) ?></td>
            <td><?= cx_h((string)($r['ip'] ?? '')) ?></td>
            <td><?= cx_h((string)($r['action'] ?? '')) ?></td>
            <td><?= (int)($r['status'] ?? 0) ?></td>
            <td><?= cx_h((string)($r['note'] ?? '')) ?></td>
          </tr>
        <?php endforeach; ?>
      </table>
    <?php endif; ?>
  </section>

  <details>
    <summary>清单地址（调试用）</summary>
    <p class="muted"><code><?= cx_h($manifestUrl) ?></code></p>
  </details>

  <?php endif; ?>
</div>
</body>
</html>
