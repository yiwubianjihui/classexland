<?php
declare(strict_types=1);

/**
 * 插件拉取的清单端点。
 *
 *   GET /manifest.php?device=<设备名>
 *   Authorization: Bearer <访问令牌>
 *
 * 不传 device 时：只有一台设备就用那台；多台会返回 400 并列出可选设备名，
 * 避免"随便拿一份快照把自己配置覆盖了"。
 *
 * 返回：{"device","version","etag","snapshot":{"pluginConfigs":{...}},"commands":[...]}
 * 配了签名密钥时，额外返回 X-ClassExland-Signature = hex(hmac_sha256(密钥, 响应原文))。
 */

require __DIR__ . '/lib.php';
cx_boot();

try {
    $config = cx_load_config();
    $state  = cx_load_state();
} catch (Throwable $ex) {
    // 别把空状态当成「没发布过」静默写回去——直接报错，让人去看文件权限。
    cx_json_out(500, ['error' => 'station_storage_error', 'detail' => $ex->getMessage()]);
}

if (($config['api_token'] ?? '') === '') {
    cx_json_out(503, ['error' => 'station not initialized']);
}

$token = cx_bearer_token();
if ($token === '' || !hash_equals((string)$config['api_token'], $token)) {
    cx_log_request('manifest', 401, '令牌不匹配');
    cx_json_out(401, ['error' => 'unauthorized']);
}

$names = cx_device_names($state);

// ?list=1：只列设备名与版本（不返回任何配置内容），给客户端的「选择设备」下拉用。
if (isset($_GET['list'])) {
    $items = [];
    foreach ($names as $name) {
        $dev = $state['devices'][$name];
        $items[] = [
            'name'       => $name,
            'note'       => (string)($dev['note'] ?? ''),
            'version'    => (string)($dev['version'] ?? ''),
            'updated_at' => (string)($dev['updated_at'] ?? ''),
            'last_seen'  => (string)($dev['last_seen'] ?? ''),
        ];
    }
    cx_log_request('list', 200, count($items) . ' 台设备');
    cx_json_out(200, ['devices' => $items]);
}

$deviceName = cx_device_name((string)($_GET['device'] ?? (cx_header('X-ClassExland-Device'))));

if ($deviceName === '') {
    if (count($names) === 1) {
        $deviceName = $names[0];
    } elseif (count($names) === 0) {
        // 还没建任何设备：给一份空清单，客户端保持本地配置不动。
        cx_json_out(200, [
            'device'   => '',
            'version'  => '0',
            'etag'     => '0',
            'snapshot' => ['pluginConfigs' => new stdClass()],
            'commands' => [],
        ]);
    } else {
        cx_log_request('manifest', 400, '未指定设备');
        cx_json_out(400, ['error' => 'device_required', 'devices' => $names]);
    }
}

if (!isset($state['devices'][$deviceName])) {
    // 客户端第一次带着新名字来拉：自动登记一台空设备，管理页上就能直接给它发布。
    // 只登记合法名字、且总量有上限，避免被乱填刷爆 state.php。
    if (!cx_device_name_ok($deviceName)) {
        cx_log_request('manifest', 400, '设备名不合法：' . $deviceName);
        cx_json_out(400, ['error' => 'invalid_device_name', 'device' => $deviceName]);
    }
    if (count($names) >= CX_MAX_DEVICES) {
        cx_log_request('manifest', 429, '设备数已达上限：' . $deviceName);
        cx_json_out(429, ['error' => 'too_many_devices', 'devices' => $names]);
    }

    $state['devices'][$deviceName] = array_merge(cx_default_device(), [
        'note'       => '自动登记（客户端首次拉取）',
        'created_at' => date('Y-m-d H:i:s'),
    ]);
    $names[] = $deviceName;
    cx_save_state($state);
    cx_log_request('device_auto', 200, $deviceName);
}

$device = $state['devices'][$deviceName];
$body   = cx_build_manifest($device, $deviceName);

$sig = '';
if (($config['hmac_secret'] ?? '') !== '') {
    $sig = hash_hmac('sha256', $body, (string)$config['hmac_secret']);
}

// 记一次心跳（设备列表里能看出哪台最近还活着），与访问日志合并成一次写入。
$state['devices'][$deviceName]['last_seen']   = date('Y-m-d H:i:s');
$state['devices'][$deviceName]['last_ip']     = $_SERVER['REMOTE_ADDR'] ?? '-';
$state['devices'][$deviceName]['last_version'] = (string)($device['version'] ?? '');
$client = cx_header('X-ClassExland-Version');
if ($client !== '') {
    $state['devices'][$deviceName]['last_client'] = mb_substr($client, 0, 40);
}
array_unshift($state['requests'], [
    'at'     => date('Y-m-d H:i:s'),
    'ip'     => $_SERVER['REMOTE_ADDR'] ?? '-',
    'action' => 'manifest',
    'status' => 200,
    'note'   => mb_substr('device=' . $deviceName . ' version=' . (($device['version'] ?? '') ?: '-') . ' files=' . count($device['files'] ?? []), 0, 200),
]);
$state['requests'] = array_slice($state['requests'], 0, 60);
cx_save_state($state);

http_response_code(200);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
if (($device['etag'] ?? '') !== '') {
    header('ETag: "' . $device['etag'] . '"');
}
if ($sig !== '') {
    header('X-ClassExland-Signature: ' . $sig);
}
echo $body;
