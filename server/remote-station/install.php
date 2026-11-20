<?php
declare(strict_types=1);

/**
 * 一次性初始化脚本。只能通过命令行运行，运行后自删。
 *
 *   php install.php '<管理员密码>'
 *
 * 生成 data/config.php（管理员密码哈希、访问令牌、签名密钥）与空的 data/state.php，
 * 把令牌与密钥打印到标准输出，然后把本文件删除。
 */

require __DIR__ . '/lib.php';
cx_boot();

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}

if (file_exists(CX_CONFIG_FILE)) {
    fwrite(STDERR, "already-installed\n");
    exit(2);
}

$password = $argv[1] ?? '';
if (strlen($password) < 8) {
    fwrite(STDERR, "password-too-short\n");
    exit(3);
}

$config = cx_default_config();
$config['admin_password'] = password_hash($password, PASSWORD_DEFAULT);
$config['api_token'] = cx_new_token();
$config['hmac_secret'] = cx_new_token(32);
cx_save_config($config);

// 先建一台「默认设备」，客户端什么都不填也能直接拉（多设备时才会强制指定）。
$state = cx_default_state();
$first = cx_default_device();
$first['created_at'] = date('Y-m-d H:i:s');
$state['devices']['默认设备'] = $first;
$state['selected'] = '默认设备';
cx_save_state($state);

echo json_encode([
    'token'   => $config['api_token'],
    'hmac'    => $config['hmac_secret'],
    'devices' => cx_device_names($state),
]), "\n";

@unlink(__FILE__);
