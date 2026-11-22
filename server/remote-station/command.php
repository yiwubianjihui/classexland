<?php
declare(strict_types=1);

/**
 * 轻量指令端点：客户端每隔几秒拉一次，只回当前设备的指令队列（不含配置快照）。
 *
 *   GET /command.php?device=<设备名>
 *   Authorization: Bearer <访问令牌>
 *
 * 返回 {"device":..., "commands":[{id,command,args,apply,dryRun},...]}
 * 客户端按 id 去重，所以这个端点可以随便轮询；指令是否删除由管理页决定。
 * 配了签名密钥时同样返回 X-ClassExland-Signature = hex(hmac_sha256(密钥, 响应原文))。
 */

require __DIR__ . '/lib.php';
cx_boot();

try {
    $config = cx_load_config();
    $state  = cx_load_state();
} catch (Throwable $ex) {
    cx_json_out(500, ['error' => 'station_storage_error', 'detail' => $ex->getMessage()]);
}

if (($config['api_token'] ?? '') === '') {
    cx_json_out(503, ['error' => 'station not initialized']);
}

$token = cx_bearer_token();
if ($token === '' || !hash_equals((string)$config['api_token'], $token)) {
    cx_log_request('command', 401, '令牌不匹配');
    cx_json_out(401, ['error' => 'unauthorized']);
}

$names = cx_device_names($state);
$deviceName = cx_device_name((string)($_GET['device'] ?? cx_header('X-ClassExland-Device')));

if ($deviceName === '') {
    if (count($names) === 1) {
        $deviceName = $names[0];
    } elseif (count($names) === 0) {
        cx_json_out(200, ['device' => '', 'commands' => []]);
    } else {
        cx_json_out(400, ['error' => 'device_required', 'devices' => $names]);
    }
}

$commands = isset($state['devices'][$deviceName])
    ? cx_command_list($state['devices'][$deviceName], $deviceName)
    : [];

$body = json_encode([
    'device'   => $deviceName,
    'commands' => $commands,
], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);

http_response_code(200);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
if (($config['hmac_secret'] ?? '') !== '') {
    header('X-ClassExland-Signature: ' . hash_hmac('sha256', $body, (string)$config['hmac_secret']));
}
echo $body;
