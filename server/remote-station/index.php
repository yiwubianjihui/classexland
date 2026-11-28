<?php
declare(strict_types=1);

require __DIR__ . '/lib.php';
require __DIR__ . '/history.php';
cx_boot();

// 所有 POST 都会读-改-写 state/history：加锁到请求结束（exit 时由 shutdown 回调释放）。
if (($_SERVER['REQUEST_METHOD'] ?? 'GET') === 'POST') {
    cx_lock_store(CX_STATE_FILE);
    cx_lock_store(CX_HISTORY_FILE);
}

session_name('cxstation');
session_set_cookie_params(['httponly' => true, 'samesite' => 'Lax', 'path' => '/']);
session_start();

$storageError = '';
try {
    $config = cx_load_config();
    $state  = cx_load_state();
    $history = cx_load_history();
} catch (Throwable $ex) {
    // 存储坏了就别装成「还没初始化」：那样会让人重新 setup，把真实令牌覆盖掉。
    $storageError = $ex->getMessage();
    $config = cx_default_config();
    $state  = cx_default_state();
    $history = cx_default_history();
}
$needsSetup = ($config['admin_password'] === '');
$loggedIn = !empty($_SESSION['cx_ok']);

if ($storageError !== '') {
    http_response_code(500);
    header('Content-Type: text/html; charset=utf-8');
    echo '<!doctype html><meta charset="utf-8"><title>远程站存储异常</title>'
        . '<div style="max-width:640px;margin:60px auto;font:15px/1.6 system-ui,Microsoft YaHei,sans-serif">'
        . '<h1 style="font-size:18px">远程站读不到数据文件</h1>'
        . '<p style="color:#b45309">' . cx_h($storageError) . '</p>'
        . '<p>多半是 <code>data/</code> 里的文件被改成了别的属主（例如用 root 跑过命令行脚本）。'
        . '在服务器上执行一次修复即可：</p>'
        . '<pre style="background:#f4f6f8;padding:12px;border-radius:8px">'
        . 'chown -R www:www ' . cx_h(CX_DATA_DIR) . "\n"
        . 'chmod 750 ' . cx_h(CX_DATA_DIR) . "\n"
        . 'chmod 640 ' . cx_h(CX_DATA_DIR) . '/*.php</pre>'
        . '<p>修好后刷新本页；数据没有被删除。</p></div>';
    exit;
}

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

/**
 * 管理页可编辑字段：插件配置 + core（含几个常用空项，方便第一次添加）。
 * 每项都是 [字段名, 原文]，字段名直接进 f_name[]，不改写成别的 form name。
 */
function cx_edit_fields(array $device): array
{
    $flat = cx_flat_snapshot($device);
    $groups = ['plugins' => [], 'core' => []];

    foreach (CX_ALLOWED_PLUGINS as $key) {
        $groups['plugins'][] = [$key, $flat[$key] ?? ''];
    }

    $groups['core'][] = [CX_CORE_SETTINGS_TREE_KEY, $flat[CX_CORE_SETTINGS_TREE_KEY] ?? ''];

    $profiles = is_array($device['core'][CX_CORE_PROFILES] ?? null)
        ? array_keys($device['core'][CX_CORE_PROFILES]) : [];
    foreach (array_values(array_unique(array_merge($profiles, ['YBH.json']))) as $name) {
        $key = CX_CORE_PROFILES_PREFIX . $name;
        $groups['core'][] = [$key, $flat[$key] ?? ''];
    }

    $layouts = is_array($device['core'][CX_CORE_LAYOUTS] ?? null)
        ? array_keys($device['core'][CX_CORE_LAYOUTS]) : [];
    foreach (array_values(array_unique(array_merge($layouts, ['Default.json', 'offclass.json']))) as $name) {
        $key = CX_CORE_LAYOUTS_PREFIX . $name;
        $groups['core'][] = [$key, $flat[$key] ?? ''];
    }
    return $groups;
}

/** 选中某台设备：写进 state，供刷新后保持。 */
function cx_select(array &$state, string $name): void
{
    if ($name !== '' && isset($state['devices'][$name])) {
        $state['selected'] = $name;
        cx_save_state($state);
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

    if ($action === 'device_create') {
        $name = cx_device_name((string)($_POST['name'] ?? ''));
        if (!cx_device_name_ok($name)) {
            cx_redirect('设备名只能用中英文、数字、空格与 -_.()，最长 40 个字符。');
        }
        if (isset($state['devices'][$name])) {
            cx_redirect('已经有一台叫「' . $name . '」的设备了。');
        }
        $device = cx_default_device();
        $device['note'] = mb_substr(trim((string)($_POST['note'] ?? '')), 0, 60);
        $device['created_at'] = date('Y-m-d H:i:s');
        $state['devices'][$name] = $device;
        $state['selected'] = $name;
        cx_save_state($state);
        cx_log_request('device_create', 200, $name);
        cx_redirect('已新增设备「' . $name . '」，客户端把设备名填成它就能拿到这份配置。');
    }

    if ($action === 'device_rename') {
        $from = cx_device_name((string)($_POST['from'] ?? ''));
        $to   = cx_device_name((string)($_POST['to'] ?? ''));
        if (!isset($state['devices'][$from])) {
            cx_redirect('设备「' . $from . '」不存在。');
        }
        if (!cx_device_name_ok($to)) {
            cx_redirect('新设备名不合法。');
        }
        if ($to !== $from && isset($state['devices'][$to])) {
            cx_redirect('已经有一台叫「' . $to . '」的设备了。');
        }
        $state['devices'][$to] = $state['devices'][$from];
        if ($to !== $from) {
            unset($state['devices'][$from]);
        }
        $state['selected'] = $to;
        cx_save_state($state);
        cx_log_request('device_rename', 200, $from . ' -> ' . $to);
        cx_redirect('设备名已改为「' . $to . '」。记得把客户端的设备名也改过来。');
    }

    if ($action === 'device_clone') {
        $from = cx_device_name((string)($_POST['from'] ?? ''));
        $to   = cx_device_name((string)($_POST['to'] ?? ''));
        if (!isset($state['devices'][$from])) {
            cx_redirect('作为模板的设备不存在。');
        }
        if (!cx_device_name_ok($to)) {
            cx_redirect('新设备名不合法。');
        }
        if (isset($state['devices'][$to])) {
            cx_redirect('已经有一台叫「' . $to . '」的设备了。');
        }
        $src = $state['devices'][$from];
        $dev = cx_default_device();
        $dev['note']       = '复制自「' . $from . '」';
        $dev['created_at'] = date('Y-m-d H:i:s');
        $dev['files']      = $src['files'] ?? [];
        $dev['core']       = $src['core'] ?? [];
        $dev['commands']   = $src['commands'] ?? [];
        $flat = cx_flat_snapshot($dev);
        $baseHashes = cx_core_base_hashes($dev);
        if ($flat) {
            // 复制出来的算一次新发布：有自己的版本与 etag，客户端才会认为有变化。
            $dev['version']    = date('Ymd-His') . '-' . substr(bin2hex(random_bytes(2)), 0, 4);
            $dev['updated_at'] = date('Y-m-d H:i:s');
            $head = cx_tree_hash($flat);
            $dev['etag']       = substr(hash('sha256', cx_build_manifest($dev, $to, $head)), 0, 12);
        }
        $state['devices'][$to] = $dev;
        $state['selected'] = $to;
        if ($flat) {
            cx_commit_snapshot($history, $to, $flat, '复制自「' . $from . '」', '管理员', '', $baseHashes);
            cx_save_history($history);
        }
        cx_save_state($state);
        cx_log_request('device_clone', 200, $from . ' -> ' . $to);
        cx_redirect('已复制成「' . $to . '」，记得把客户端的设备名也改成它。');
    }

    if ($action === 'device_delete') {
        $name = cx_device_name((string)($_POST['name'] ?? ''));
        if (!isset($state['devices'][$name])) {
            cx_redirect('设备不存在。');
        }
        if (trim((string)($_POST['confirm'] ?? '')) !== $name) {
            cx_redirect('删除确认不匹配，未执行。请把设备名原样填进确认框。');
        }
        unset($state['devices'][$name]);
        unset($history['devices'][$name]);
        cx_gc_blobs($history);
        if (($state['selected'] ?? '') === $name) {
            $state['selected'] = '';
        }
        cx_save_state($state);
        cx_save_history($history);
        cx_log_request('device_delete', 200, $name);
        cx_redirect('设备「' . $name . '」及其快照已删除。');
    }

    if ($action === 'device_select') {
        cx_select($state, cx_device_name((string)($_POST['name'] ?? '')));
        cx_redirect('已切换到该设备。');
    }

    if ($action === 'publish') {
        $name = cx_device_name((string)($_POST['device'] ?? ''));
        if (!isset($state['devices'][$name])) {
            cx_redirect('设备不存在，未发布。');
        }

        // 字段用平行数组 f_name[]/f_content[]，避免按文件名拼字段时 . / - 撞名。
        $postNames = $_POST['f_name'] ?? [];
        $postContents = $_POST['f_content'] ?? [];
        if (!is_array($postNames) || !is_array($postContents)) {
            cx_redirect('表单格式不合法，未发布。');
        }
        $flat = [];
        $errors = [];
        foreach ($postNames as $i => $key) {
            $key = (string)$key;
            if (!cx_is_allowed_key($key)) {
                $errors[] = $key . ' 不在白名单';
                continue;
            }
            $raw = trim((string)($postContents[$i] ?? ''));
            if ($raw === '') continue;
            if (strlen($raw) > CX_MAX_BODY) {
                $errors[] = $key . ' 超过 2 MB';
                continue;
            }
            if (!cx_is_json($raw)) {
                $errors[] = $key . ' 不是合法 JSON';
                continue;
            }
            if (strpos($key, 'core.') === 0 && cx_json_has_forbidden_key($raw)) {
                $errors[] = $key . ' 命中禁止键';
                continue;
            }
            $flat[$key] = $raw;
        }

        if ($errors) {
            cx_redirect('未发布：' . implode('；', $errors));
        }

        $head = cx_tree_hash($flat);
        $version = cx_publish_files($state, $name, $flat, $head);
        $parent  = (string)($history['devices'][$name]['head'] ?? '');
        cx_commit_snapshot($history, $name, $flat, (string)($_POST['message'] ?? '管理页发布'), '管理员', $parent);
        if (!cx_save_state($state) || !cx_save_history($history)) {
            cx_redirect('写入状态失败，请检查 data 目录权限。');
        }
        cx_log_request('publish', 200, $name . ' version=' . $version . ' files=' . count($flat));
        cx_redirect('已为「' . $name . '」发布版本 ' . $version . '，该设备下次同步即生效。');
    }

    if ($action === 'command_add') {
        $name = (string)($_POST['device'] ?? '');
        $allDevices = $name === '*';
        if (!$allDevices && !isset($state['devices'][cx_device_name($name)])) {
            cx_redirect('设备不存在，未加入队列。');
        }
        $command = trim((string)($_POST['command'] ?? ''));
        $argsRaw = trim((string)($_POST['args'] ?? ''));
        // 命令名来自共享命令层（CLI 与远程队列同一套），允许任意合法命令，不锁死在预设下拉里。
        if ($command === '' || strlen($command) > 80 || preg_match('/^[a-z][a-z0-9.]*$/i', $command) !== 1) {
            cx_redirect('指令名不合法：用小写字母与点，例如 layout.switch。');
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

        $targets = $allDevices ? array_keys($state['devices']) : [cx_device_name($name)];
        foreach ($targets as $target) {
            if (!isset($state['devices'][$target])) continue;
            $state['devices'][$target]['commands'][] = [
                'id'      => bin2hex(random_bytes(12)),
                'command' => $command,
                'args'    => $args,
                'apply'   => !empty($_POST['apply']),
                'dryRun'  => false,
            ];
            $state['devices'][$target]['updated_at'] = date('Y-m-d H:i:s');
            // 同时前进版本号：老客户端没有 command.php 轮询，仍能从清单里拿到指令。
            $state['devices'][$target]['version'] = date('Ymd-His') . '-' . substr(bin2hex(random_bytes(2)), 0, 4);
            $targetHead = cx_tree_hash(cx_flat_snapshot($state['devices'][$target]));
            $state['devices'][$target]['etag'] = substr(
                hash('sha256', cx_build_manifest($state['devices'][$target], $target, $targetHead)), 0, 12
            );
        }
        if (!cx_save_state($state)) cx_redirect('写入状态失败，请检查 data 目录权限。');
        cx_log_request('command_add', 200, implode(',', $targets) . ' ' . $command);
        cx_redirect('已给 ' . count($targets) . ' 台设备加入指令：' . $command . '（客户端命令轮询默认 10 秒内执行）');
    }

    if ($action === 'command_clear') {
        $name = cx_device_name((string)($_POST['device'] ?? ''));
        if (!isset($state['devices'][$name])) {
            cx_redirect('设备不存在，未清空。');
        }
        $state['devices'][$name]['commands'] = [];
        $state['devices'][$name]['version'] = date('Ymd-His') . '-' . substr(bin2hex(random_bytes(2)), 0, 4);
        $state['devices'][$name]['updated_at'] = date('Y-m-d H:i:s');
        $clearHead = cx_tree_hash(cx_flat_snapshot($state['devices'][$name]));
        $state['devices'][$name]['etag'] = substr(
            hash('sha256', cx_build_manifest($state['devices'][$name], $name, $clearHead)), 0, 12
        );
        cx_save_state($state);
        cx_log_request('command_clear', 200, $name);
        cx_redirect('「' . $name . '」的指令队列已清空。');
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

    if ($action === 'policy_save') {
        $policy = (string)($_POST['upload_policy'] ?? 'review');
        $config['upload_policy'] = $policy === 'auto' ? 'auto' : 'review';
        if (!cx_save_config($config)) cx_redirect('写入配置失败，请检查 data 目录权限。');
        cx_log_request('policy', 200, $config['upload_policy']);
        cx_redirect($config['upload_policy'] === 'auto'
            ? '上传冲突策略已改为「自动接受」：旧版本仍会留在历史里。'
            : '上传冲突策略已改为「人工审核」：冲突上传进待审队列，不会覆盖当前快照。');
    }

    if ($action === 'pending_accept') {
        $name = cx_device_name((string)($_POST['device'] ?? ''));
        $id   = (string)($_POST['id'] ?? '');
        if (!isset($history['devices'][$name]['pending'])) cx_redirect('待审队列不存在。');
        $target = null;
        foreach ($history['devices'][$name]['pending'] as $p) {
            if (hash_equals((string)($p['id'] ?? ''), $id)) { $target = $p; break; }
        }
        if ($target === null) cx_redirect('找不到这条待审上传。');

        // 待审上传的 tree 也是摊平视图（含 core 命名空间键），用 blob 还原后整份采用。
        $targetTree = is_array($target['tree'] ?? null) ? $target['tree'] : [];
        $flat = cx_commit_files($history, ['tree' => $targetTree]);
        $baseHashes = is_array($target['baseHashes'] ?? null) ? $target['baseHashes'] : [];
        $parent = (string)($history['devices'][$name]['head'] ?? '');
        $head = cx_tree_hash($flat);
        $commit = cx_commit_snapshot(
            $history, $name, $flat,
            '采用待审上传：' . (string)($target['message'] ?? ''), '管理员', $parent, $baseHashes
        );
        cx_publish_files($state, $name, $flat, $head, $baseHashes);
        $history['devices'][$name]['pending'] = array_values(array_filter(
            $history['devices'][$name]['pending'],
            static fn($p) => (string)($p['id'] ?? '') !== $id
        ));
        if (!cx_save_history($history) || !cx_save_state($state)) cx_redirect('写入失败，请检查 data 目录权限。');
        cx_log_request('pending-accept', 200, $name . ' commit=' . substr($commit['id'], 0, 10));
        cx_redirect('已采用这次上传，并生成新版本（旧版本仍可回滚）。');
    }

    if ($action === 'pending_reject') {
        $name = cx_device_name((string)($_POST['device'] ?? ''));
        $id   = (string)($_POST['id'] ?? '');
        $history['devices'][$name]['pending'] = array_values(array_filter(
            $history['devices'][$name]['pending'] ?? [],
            static fn($p) => (string)($p['id'] ?? '') !== $id
        ));
        if (!cx_save_history($history)) cx_redirect('写入失败，请检查 data 目录权限。');
        cx_log_request('pending-reject', 200, $name . ' ' . substr($id, 0, 10));
        cx_redirect('已丢弃这次待审上传，当前快照未改动。');
    }

    if ($action === 'history_rollback') {
        $name = cx_device_name((string)($_POST['device'] ?? ''));
        $id   = (string)($_POST['id'] ?? '');
        $target = null;
        foreach ($history['devices'][$name]['commits'] ?? [] as $c) {
            if (hash_equals((string)($c['id'] ?? ''), $id)) { $target = $c; break; }
        }
        if ($target === null) cx_redirect('找不到这个版本。');

        $flat = cx_commit_files($history, $target);
        $baseHashes = is_array($target['baseHashes'] ?? null) ? $target['baseHashes'] : [];
        $parent = (string)($history['devices'][$name]['head'] ?? '');
        $head = cx_tree_hash($flat);
        $commit = cx_commit_snapshot(
            $history, $name, $flat, '回滚到 ' . substr($id, 0, 10), '管理员', $parent, $baseHashes
        );
        cx_publish_files($state, $name, $flat, $head, $baseHashes);
        if (!cx_save_history($history) || !cx_save_state($state)) cx_redirect('写入失败，请检查 data 目录权限。');
        cx_log_request('rollback', 200, $name . ' -> ' . substr($id, 0, 10));
        cx_redirect('已回滚到该版本；这次回滚本身也是一条新提交，可以再滚回来。');
    }
}

$flash = (string)($_SESSION['flash'] ?? '');
unset($_SESSION['flash']);

$names    = cx_device_names($state);
$requested = cx_device_name((string)($_GET['device'] ?? ''));
$selected  = $requested !== '' && isset($state['devices'][$requested])
    ? $requested
    : (string)($state['selected'] ?? '');
if ($selected === '' || !isset($state['devices'][$selected])) {
    $selected = $names[0] ?? '';
}
$device = $selected !== '' ? $state['devices'][$selected] : null;
$devHistory = $history['devices'][$selected] ?? ['head' => '', 'commits' => [], 'pending' => []];
$commits = array_reverse($devHistory['commits'] ?? []);
$pendingList = $devHistory['pending'] ?? [];
$policy = (string)($config['upload_policy'] ?? 'review');

$base = cx_current_url();
$manifestPath = rtrim(dirname($_SERVER['PHP_SELF']), '/\\') . '/manifest.php';
$manifestUrl  = $base . $manifestPath . ($selected !== '' ? '?device=' . rawurlencode($selected) : '');
$token = (string)$config['api_token'];
$hmac  = (string)$config['hmac_secret'];
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
  .wrap{max-width:1020px;margin:0 auto;padding:28px 18px 64px}
  header{display:flex;align-items:baseline;gap:10px;margin-bottom:16px;flex-wrap:wrap}
  header h1{font-size:19px;margin:0}
  .tag{font-size:12px;color:var(--muted);border:1px solid var(--line);border-radius:99px;padding:2px 9px}
  .tag.on{color:var(--ok);border-color:currentColor}
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
  button{font:inherit;padding:8px 13px;border-radius:9px;border:1px solid var(--line);background:var(--card);color:var(--fg);cursor:pointer}
  button:hover{border-color:var(--accent)}
  button.primary{background:var(--accent);border-color:var(--accent);color:#fff}
  button.danger{color:var(--danger);border-color:var(--danger)}
  .row{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin-top:10px}
  .flash{background:color-mix(in srgb,var(--ok) 12%,transparent);border:1px solid color-mix(in srgb,var(--ok) 40%,var(--line));color:var(--fg);padding:10px 12px;border-radius:10px;margin-bottom:14px}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{text-align:left;padding:7px 6px;border-bottom:1px dashed var(--line);vertical-align:middle}
  tr.self{background:color-mix(in srgb,var(--accent) 8%,transparent)}
  code{font-family:Consolas,Menlo,monospace;font-size:12.5px;word-break:break-all}
  .kv{display:grid;grid-template-columns:110px 1fr;gap:6px 12px;font-size:13.5px;align-items:center}
  .kv span{color:var(--muted)}
  .pill{display:inline-block;font-size:12px;padding:1px 8px;border-radius:99px;border:1px solid var(--line);color:var(--muted)}
  details summary{cursor:pointer;color:var(--muted);font-size:13px}
  .inline{display:inline-flex;gap:6px;align-items:center}
  .inline input[type=text]{width:auto;min-width:120px;padding:5px 8px;font-size:13px}
  .inline button{padding:5px 9px;font-size:13px}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <h1>ClassExland 远程站</h1>
    <span class="tag">配置下发与指令队列</span>
    <?php if ($loggedIn): ?><span class="tag">已登录</span><?php endif; ?>
    <?php if ($selected !== ''): ?><span class="tag on">当前设备：<?= cx_h($selected) ?></span><?php endif; ?>
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
    <h2>设备（每台教室电脑一份快照）</h2>
    <p class="muted">
      在这里按名字登记设备，比如「初一2班」「录播教室」。客户端的「设备名称」填成哪个名字，
      就只会拿到那一份快照和那一条指令队列，互相不干扰。客户端只要带着设备名来拉过一次，
      就会自动出现在下表里（最近访问时间会更新）。
    </p>

    <?php if (empty($names)): ?>
      <p class="muted">还没有任何设备，先在下面新增一台。</p>
    <?php else: ?>
      <table>
        <tr><th>设备</th><th>备注</th><th>版本</th><th>文件 / 指令</th><th>最近拉取</th><th></th></tr>
        <?php foreach ($names as $name): $d = $state['devices'][$name]; ?>
          <tr class="<?= $name === $selected ? 'self' : '' ?>">
            <td><strong><?= cx_h($name) ?></strong></td>
            <td class="muted"><?= cx_h((string)($d['note'] ?? '')) ?: '—' ?></td>
            <td><code><?= cx_h(($d['version'] ?? '') !== '' ? (string)$d['version'] : '未发布') ?></code></td>
            <td><?= count($d['files'] ?? []) ?> / <?= count($d['commands'] ?? []) ?></td>
            <td class="muted">
              <?= cx_h(($d['last_seen'] ?? '') !== '' ? (string)$d['last_seen'] : '从未') ?>
              <?php if (($d['last_ip'] ?? '') !== ''): ?><br><span class="pill"><?= cx_h((string)$d['last_ip']) ?></span><?php endif; ?>
            </td>
            <td>
              <?php if ($name !== $selected): ?>
                <form method="post" style="display:inline">
                  <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
                  <input type="hidden" name="action" value="device_select">
                  <input type="hidden" name="name" value="<?= cx_h($name) ?>">
                  <button type="submit">切到这台</button>
                </form>
              <?php else: ?>
                <span class="pill">正在编辑</span>
              <?php endif; ?>
            </td>
          </tr>
        <?php endforeach; ?>
      </table>
    <?php endif; ?>

    <h3>新增设备</h3>
    <form method="post">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="device_create">
      <div class="grid">
        <div>
          <label>设备名（客户端要填同一个名字）</label>
          <input type="text" name="name" placeholder="初一2班" required maxlength="40">
        </div>
        <div>
          <label>备注（只给自己看）</label>
          <input type="text" name="note" placeholder="教室大屏 / 备用机" maxlength="60">
        </div>
      </div>
      <div class="row"><button class="primary" type="submit">新增设备</button></div>
    </form>

    <?php if ($selected !== ''): ?>
      <h3>重命名 / 删除「<?= cx_h($selected) ?>」</h3>
      <div class="row">
        <form method="post" class="inline">
          <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
          <input type="hidden" name="action" value="device_rename">
          <input type="hidden" name="from" value="<?= cx_h($selected) ?>">
          <input type="text" name="to" value="<?= cx_h($selected) ?>" maxlength="40" required>
          <button type="submit">改名</button>
        </form>
        <form method="post" class="inline">
          <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
          <input type="hidden" name="action" value="device_delete">
          <input type="hidden" name="name" value="<?= cx_h($selected) ?>">
          <input type="text" name="confirm" placeholder="输入设备名确认删除" required>
          <button class="danger" type="submit">删除</button>
        </form>
      </div>

      <h3>以「<?= cx_h($selected) ?>」为模板复制一台</h3>
      <p class="muted" style="margin:0 0 6px">几个班用同一份值日表 / 铃声时最省事：复制出来的设备自带同样的快照与指令队列，版本号是新的。</p>
      <form method="post" class="inline">
        <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
        <input type="hidden" name="action" value="device_clone">
        <input type="hidden" name="from" value="<?= cx_h($selected) ?>">
        <input type="text" name="to" placeholder="新设备名，例如 初一3班" maxlength="40" required>
        <button type="submit">复制</button>
      </form>
    <?php endif; ?>
  </section>

  <section class="card">
    <h2>客户端填这些<?= $selected !== '' ? '（设备：' . cx_h($selected) . '）' : '' ?></h2>
    <div class="kv">
      <span>服务器地址</span><div><code><?= cx_h($base) ?></code></div>
      <span>访问令牌</span><div><code><?= cx_h($token) ?></code></div>
      <span>设备名称</span><div><code><?= $selected !== '' ? cx_h($selected) : '（先新增一台设备）' ?></code></div>
      <span>签名密钥</span><div><code><?= $hmac === '' ? '（未启用）' : cx_h($hmac) ?></code></div>
    </div>
    <p class="muted">在 ClassExland →「远程控制」里填服务器地址、访问令牌和设备名称即可，清单地址会自动带上设备名。地址和令牌只显示给已登录的管理员。</p>
    <details>
      <summary>清单地址（调试用）</summary>
      <p class="muted"><code><?= cx_h($manifestUrl) ?></code></p>
    </details>
  </section>

  <?php if ($device !== null): ?>
  <?php $editFields = cx_edit_fields($device); ?>
  <section class="card">
    <h2>「<?= cx_h($selected) ?>」的配置快照</h2>
    <p class="muted">留空的文件不会写进这份快照（该设备保持原样）。core 项保存后由客户端按 baseHash 判断能否覆盖本地修改；这里发布会强制下发。保存后生成新版本，这台设备下次同步时应用。</p>
    <form method="post">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="publish">
      <input type="hidden" name="device" value="<?= cx_h($selected) ?>">
      <h3>插件配置</h3>
      <?php foreach ($editFields['plugins'] as [$key, $raw]): ?>
        <label><?= cx_h($key) ?></label>
        <input type="hidden" name="f_name[]" value="<?= cx_h($key) ?>">
        <textarea name="f_content[]" placeholder="留空表示不下发这个文件"><?= cx_h($raw !== '' ? cx_pretty($raw) : '') ?></textarea>
      <?php endforeach; ?>
      <h3>核心配置（ClassIsland）</h3>
      <?php foreach ($editFields['core'] as [$key, $raw]): ?>
        <label><?= cx_h($key) ?></label>
        <input type="hidden" name="f_name[]" value="<?= cx_h($key) ?>">
        <textarea name="f_content[]" placeholder="留空表示不下发这个文件"><?= cx_h($raw !== '' ? cx_pretty($raw) : '') ?></textarea>
      <?php endforeach; ?>
        <h3>说明（可选）</h3>
        <input type="text" name="message" placeholder="这次改了什么，例如：周一值日调整" maxlength="120">
        <div class="row"><button class="primary" type="submit">保存并发布到「<?= cx_h($selected) ?>」</button></div>
    </form>
  </section>

  <section class="card">
    <h2>给设备下指令</h2>
    <p class="muted">指令与 CLI 共用命令层，命令名和参数原样下发。客户端有独立的命令轮询（默认 10 秒），不必等配置快照；按指令 id 去重，不会重复执行。选「所有设备」可一次群发。</p>
    <form method="post">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="command_add">
      <label>目标设备</label>
      <select name="device">
        <?php foreach ($names as $n): ?>
          <option value="<?= cx_h($n) ?>" <?= $n === $selected ? 'selected' : '' ?>><?= cx_h($n) ?></option>
        <?php endforeach; ?>
        <option value="*">所有设备（<?= count($names) ?> 台）</option>
      </select>
      <label>命令名（可手填任意合法命令）</label>
      <input type="text" id="cxCommand" name="command" list="cxCommandList" placeholder="layout.switch" required maxlength="80">
      <datalist id="cxCommandList">
        <option value="layout.switch"><option value="layout.current">
        <option value="bell.set"><option value="duty.rotate"><option value="duty.complete">
        <option value="activity.test"><option value="remote.sync"><option value="remote.status">
        <option value="profile.switch"><option value="class.set"><option value="config.doctor">
      </datalist>
      <div class="row">
        <button type="button" onclick="document.getElementById('cxCommand').value='layout.switch';document.getElementById('cxArgs').value='{"to":"class"}'">切上课布局</button>
        <button type="button" onclick="document.getElementById('cxCommand').value='layout.switch';document.getElementById('cxArgs').value='{"to":"break"}'">切下课布局</button>
        <button type="button" onclick="document.getElementById('cxCommand').value='duty.rotate';document.getElementById('cxArgs').value='{}'">轮换值日</button>
        <button type="button" onclick="document.getElementById('cxCommand').value='remote.sync';document.getElementById('cxArgs').value='{}'">立即同步</button>
      </div>
      <label>参数（JSON 对象，值用字符串）</label>
      <input type="text" id="cxArgs" name="args" placeholder='{"to":"class"} 或 {"index":"3","prep":"90"}'>
      <div class="row">
        <label style="margin:0"><input type="checkbox" name="apply" checked> 真正执行（不勾选只预览）</label>
        <button class="primary" type="submit">立即下发</button>
      </div>
    </form>

    <?php if (!empty($device['commands'])): ?>
      <table style="margin-top:14px">
        <tr><th>id</th><th>指令</th><th>参数</th><th>执行</th></tr>
        <?php foreach ($device['commands'] as $cmd): ?>
          <tr>
            <td><code><?= cx_h(substr((string)($cmd['id'] ?? ''), 0, 10)) ?></code></td>
            <td><code><?= cx_h((string)($cmd['command'] ?? '')) ?></code></td>
            <td><code><?= cx_h(json_encode($cmd['args'] ?? [], JSON_UNESCAPED_UNICODE)) ?></code></td>
            <td><?= !empty($cmd['apply']) ? '是' : '预览' ?></td>
          </tr>
        <?php endforeach; ?>
      </table>
      <form method="post" style="margin-top:10px">
        <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
        <input type="hidden" name="action" value="command_clear">
        <input type="hidden" name="device" value="<?= cx_h($selected) ?>">
        <button class="danger" type="submit">清空队列</button>
      </form>
    <?php endif; ?>
  </section>

  <section class="card">
    <h2>版本历史（<?= cx_h($selected) ?>）</h2>
    <p class="muted">
      内容按 SHA-256 存成 blob，相同内容只存一份；每次变更生成一条带 parent 的提交与逐行 diff。
      当前 tree 哈希：<code><?= cx_h(substr(cx_tree_hash(cx_flat_snapshot($device)), 0, 16)) ?>…</code>
    </p>
    <?php if (empty($commits)): ?>
      <p class="muted">还没有提交。客户端上传第一次配置，或在上面「保存并发布」后就会出现。</p>
    <?php else: ?>
      <table>
        <tr><th>时间</th><th>说明</th><th>来源</th><th>tree</th><th></th></tr>
        <?php foreach (array_slice($commits, 0, 12) as $c): ?>
          <tr>
            <td class="muted"><?= cx_h((string)($c['at'] ?? '')) ?></td>
            <td><?= cx_h((string)($c['message'] ?? '')) ?></td>
            <td class="muted"><?= cx_h((string)($c['author'] ?? '')) ?></td>
            <td><code><?= cx_h(substr(cx_tree_hash(cx_commit_files($history, $c)), 0, 8)) ?></code></td>
            <td>
              <form method="post" class="inline" onsubmit="return confirm('回滚到这个版本？当前快照会先留一条提交。')">
                <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
                <input type="hidden" name="action" value="history_rollback">
                <input type="hidden" name="device" value="<?= cx_h($selected) ?>">
                <input type="hidden" name="id" value="<?= cx_h((string)($c['id'] ?? '')) ?>">
                <button type="submit">回滚</button>
              </form>
            </td>
          </tr>
          <?php if (!empty($c['diff'])): ?>
            <tr><td colspan="5">
              <details>
                <summary>查看 diff（<?= count($c['diff']) ?> 个文件）</summary>
                <?php foreach ($c['diff'] as $fileName => $diffText): ?>
                  <h3><?= cx_h((string)$fileName) ?></h3>
                  <pre style="background:var(--bg);border:1px solid var(--line);padding:10px;border-radius:8px;overflow:auto;font-size:12px"><?= cx_h((string)$diffText) ?></pre>
                <?php endforeach; ?>
              </details>
            </td></tr>
          <?php endif; ?>
        <?php endforeach; ?>
      </table>
    <?php endif; ?>
  </section>

  <section class="card">
    <h2>客户端上传与冲突</h2>
    <form method="post" class="inline">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="policy_save">
      <label style="margin:0">冲突策略
        <select name="upload_policy" style="width:auto;display:inline-block">
          <option value="review" <?= $policy === 'review' ? 'selected' : '' ?>>人工审核（默认）</option>
          <option value="auto" <?= $policy === 'auto' ? 'selected' : '' ?>>自动接受（保留历史）</option>
        </select>
      </label>
      <button type="submit">保存策略</button>
    </form>
    <p class="muted">客户端每次轮询会比对本地配置哈希：有变化就带着上次看到的 head 上传。head 对不上说明两边都改过——人工审核时进下面的待审队列，服务器当前快照保持不变。</p>

    <?php if (empty($pendingList)): ?>
      <p class="muted">没有待审冲突。</p>
    <?php else: ?>
      <?php foreach ($pendingList as $p): ?>
        <details open style="border:1px solid var(--warn);border-radius:10px;padding:10px;margin-top:10px">
          <summary>待审上传 <?= cx_h(substr((string)($p['id'] ?? ''), 0, 10)) ?> · <?= cx_h((string)($p['at'] ?? '')) ?> · base <?= cx_h(substr((string)($p['base'] ?? ''), 0, 8)) ?></summary>
          <p class="muted"><?= cx_h((string)($p['message'] ?? '')) ?></p>
          <?php foreach (($p['diff'] ?? []) as $fileName => $diffText): ?>
            <h3><?= cx_h((string)$fileName) ?></h3>
            <pre style="background:var(--bg);border:1px solid var(--line);padding:10px;border-radius:8px;overflow:auto;font-size:12px"><?= cx_h((string)$diffText) ?></pre>
          <?php endforeach; ?>
          <div class="row">
            <form method="post" class="inline">
              <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
              <input type="hidden" name="action" value="pending_accept">
              <input type="hidden" name="device" value="<?= cx_h($selected) ?>">
              <input type="hidden" name="id" value="<?= cx_h((string)($p['id'] ?? '')) ?>">
              <button class="primary" type="submit">采用这次上传</button>
            </form>
            <form method="post" class="inline">
              <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
              <input type="hidden" name="action" value="pending_reject">
              <input type="hidden" name="device" value="<?= cx_h($selected) ?>">
              <input type="hidden" name="id" value="<?= cx_h((string)($p['id'] ?? '')) ?>">
              <button class="danger" type="submit">丢弃</button>
            </form>
          </div>
        </details>
      <?php endforeach; ?>
    <?php endif; ?>
  </section>
  <?php endif; ?>

  <section class="card">
    <h2>安全设置</h2>
    <form method="post">
      <input type="hidden" name="csrf" value="<?= cx_h($csrf) ?>">
      <input type="hidden" name="action" value="settings_security">
      <label>访问令牌（留空 = 重新生成，所有设备共用）</label>
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

  <?php endif; ?>
</div>
</body>
</html>
