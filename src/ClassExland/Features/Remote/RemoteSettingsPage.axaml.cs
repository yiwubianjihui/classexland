using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassExland.Contracts;
using ClassExland.Features.Bridge;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;

namespace ClassExland.Features.Remote;

/// <summary>
/// 远程控制设置页：面向普通用户只暴露「地址 / 令牌 / 三个动作」，
/// 技术字段（清单路径、签名密钥、超时）收进「高级选项」。
/// 所有事件处理器都包 try/catch，避免插件异常导致 ClassIsland 自动禁用插件。
/// </summary>
[SettingsPageInfo("yibianhui.classexland.remote", "ClassExland 远程控制", "\uE823", "\uE823")]
public partial class RemoteSettingsPage : SettingsPageBase
{
    private readonly RemoteConfigStore _store;
    private readonly RemoteStateStore _state;
    private readonly IRemoteSyncService _sync;
    private readonly RemoteSyncModule _module;
    private readonly IPluginPaths _paths;
    private readonly LoopbackWebHost? _web;
    private bool _loading;

    public RemoteSettingsPage(
        RemoteConfigStore store,
        RemoteStateStore state,
        IRemoteSyncService sync,
        RemoteSyncModule module,
        IPluginPaths paths,
        IServiceProvider services)
    {
        _store = store;
        _state = state;
        _sync = sync;
        _module = module;
        _paths = paths;
        // 桥接模块可能未加载：惰性解析，取不到就优雅降级，不让设置页构造失败。
        _web = services.GetService(typeof(LoopbackWebHost)) as LoopbackWebHost;

        InitializeComponent();
        DataContext = this;
        LoadToUi();
    }

    // ---------------- 加载 ----------------

    private void LoadToUi()
    {
        _loading = true;
        try
        {
            var cfg = _store.Load();
            EnabledCheck.IsChecked = cfg.Enabled;
            ServerUrlBox.Text = cfg.ServerUrl;
            DeviceBox.Text = cfg.DeviceName;
            ManifestPathBox.Text = cfg.ManifestPath;
            TokenBox.Text = cfg.BearerToken;
            SecretBox.Text = cfg.HmacSecret;
            TimeoutBox.Text = cfg.FetchTimeoutSeconds.ToString();
            SnapshotCheck.IsChecked = cfg.EnableSnapshotSync;
            CoreCheck.IsChecked = cfg.EnableCoreConfigSync;
            CommandsCheck.IsChecked = cfg.EnableCommandQueue;

            PathsText.Text = $"配置文件：{_paths.Get(ConfigFiles.Remote)}\n审计日志：{_paths.AuditLogPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"载入远程配置失败：{ex.Message}";
        }
        finally
        {
            _loading = false;
        }

        UpdateEndpointHint();
        UpdateStatusCard();
        UpdateWebUi();
    }

    private void ServerUrlBox_OnTextChanged(object? sender, TextChangedEventArgs e) => UpdateEndpointHint();

    private void DeviceBox_OnTextChanged(object? sender, TextChangedEventArgs e) => UpdateEndpointHint();

    /// <summary>从服务器拉一份设备列表填进下拉，选中后写回「设备名称」。</summary>
    private async void ButtonFetchDevices_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 用界面上当前的地址/令牌去问，没必要先落盘；但 SaveFromUi 会顺带校验并提示错误。
            if (!SaveFromUi()) return;
            var cfg = _store.Load();
            if (!cfg.CanSync())
            {
                StatusText.Text = "还不能连接：服务器地址要以 https:// 开头，访问令牌不能为空。";
                return;
            }

            ButtonFetchDevices.IsEnabled = false;
            StatusText.Text = "正在读取设备列表…";
            var result = await _module.FetchDevicesAsync(cfg);
            DeviceCombo.ItemsSource = result.Devices;
            StatusText.Text = result.Message;
            if (result.Ok && result.Devices.Count > 0)
            {
                // 已经填过就顺手选中，省一次点击。
                var current = RemoteConfig.SanitizeDeviceName(DeviceBox.Text);
                DeviceCombo.SelectedItem = result.Devices.FirstOrDefault(d => d.Name == current);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取设备列表失败：{ex.Message}";
        }
        finally
        {
            ButtonFetchDevices.IsEnabled = true;
        }
    }

    private void DeviceCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (DeviceCombo.SelectedItem is RemoteDeviceInfo info)
            {
                DeviceBox.Text = info.Name;
            }
        }
        catch
        {
            // 选中项异常不影响手填。
        }
    }

    private void ManifestPathBox_OnTextChanged(object? sender, TextChangedEventArgs e) => UpdateEndpointHint();

    private void UpdateEndpointHint()
    {
        try
        {
            var url = ServerUrlBox.Text?.Trim().TrimEnd('/') ?? "";
            if (url.Length == 0)
            {
                EndpointHint.Text = "清单地址：填好服务器地址后自动生成";
                return;
            }
            var path = ManifestPathBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path)) path = "/config/manifest.json";
            if (!path.StartsWith('/')) path = "/" + path;

            var device = RemoteConfig.SanitizeDeviceName(DeviceBox.Text);
            EndpointHint.Text = device.Length == 0
                ? $"清单地址：{url}{path}（未指定设备）"
                : $"清单地址：{url}{path}?device={Uri.EscapeDataString(device)}";
        }
        catch
        {
            // 提示文本失败不影响使用
        }
    }

    private void UpdateStatusCard()
    {
        try
        {
            var cfg = _store.Load();
            var disabledMarker = File.Exists(_paths.Get("remote.disabled"));

            if (!cfg.Enabled)
            {
                StatusBadge.Text = disabledMarker ? "已停用" : "未启用";
                StatusSubText.Text = "完全离线运行，不发起任何网络请求";
            }
            else if (!cfg.CanSync())
            {
                StatusBadge.Text = "配置不完整";
                StatusSubText.Text = "地址要以 https:// 开头，访问令牌不能为空";
            }
            else
            {
                StatusBadge.Text = "已启用";
                StatusSubText.Text = string.IsNullOrEmpty(cfg.DeviceName)
                    ? cfg.ServerUrl
                    : $"{cfg.ServerUrl} · 设备 {cfg.DeviceName}";
            }

            var appliedAt = _state.LoadAppliedAt();
            var (version, _) = _state.Load();
            LastSyncText.Text = appliedAt is { } t
                ? $"上次成功同步：{t.LocalDateTime:yyyy-MM-dd HH:mm}" +
                  (string.IsNullOrEmpty(version) ? "" : $"（版本 {version}）")
                : "还没有成功同步过。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"刷新状态失败：{ex.Message}";
        }
    }

    private void UpdateWebUi()
    {
        try
        {
            if (_web == null)
            {
                WebConsoleCheck.IsEnabled = false;
                WebConsoleCheck.Content = "本机网页控制台不可用（桥接模块未加载）";
                WebUrlBox.Text = "";
                ButtonOpenWeb.IsEnabled = false;
                ButtonCopyWeb.IsEnabled = false;
                return;
            }

            var running = _web.IsRunning;
            WebConsoleCheck.IsChecked = running;
            WebUrlBox.Text = running ? _web.Url : "";
            ButtonOpenWeb.IsEnabled = running;
            ButtonCopyWeb.IsEnabled = running;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"刷新网页控制台状态失败：{ex.Message}";
        }
    }

    // ---------------- 保存 ----------------

    /// <summary>把界面写回 remote.json；返回是否保存成功。</summary>
    private bool SaveFromUi(bool forceEnabled = false)
    {
        var cfg = _store.Load();
        if (forceEnabled) EnabledCheck.IsChecked = true;

        cfg.Enabled = EnabledCheck.IsChecked == true;
        cfg.ServerUrl = ServerUrlBox.Text?.Trim().TrimEnd('/') ?? "";
        cfg.DeviceName = RemoteConfig.SanitizeDeviceName(DeviceBox.Text);
        cfg.ManifestPath = string.IsNullOrWhiteSpace(ManifestPathBox.Text)
            ? "/config/manifest.json"
            : ManifestPathBox.Text!.Trim();
        cfg.BearerToken = TokenBox.Text?.Trim() ?? "";
        cfg.HmacSecret = SecretBox.Text?.Trim() ?? "";
        cfg.FetchTimeoutSeconds = Math.Clamp(ParseInt(TimeoutBox.Text, 15), 1, 120);
        cfg.EnableSnapshotSync = SnapshotCheck.IsChecked == true;
        cfg.EnableCoreConfigSync = CoreCheck.IsChecked == true;
        cfg.EnableCommandQueue = CommandsCheck.IsChecked == true;

        if (!_store.Save(cfg))
        {
            StatusText.Text = "保存失败：无法写入 remote.json（请检查配置文件权限）。";
            return false;
        }

        // 之前点过「停用」会留下 remote.disabled 标记，重新启用时必须清掉，否则启动仍会跳过。
        if (cfg.Enabled) _module.Enable();

        UpdateStatusCard();
        UpdateEndpointHint();
        return true;
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text?.Trim(), out var value) ? value : fallback;

    // ---------------- 动作 ----------------

    private async void ButtonSaveTest_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!SaveFromUi(forceEnabled: true)) return;

            var cfg = _store.Load();
            if (!cfg.CanSync())
            {
                StatusText.Text = "已保存，但还不能连接：服务器地址要以 https:// 开头，访问令牌不能为空。";
                return;
            }

            SetBusy(true, "正在连接服务器…");
            var raw = await _sync.PollOnceAsync();
            StatusText.Text = DescribeResult(raw, syncing: false);
            UpdateStatusCard();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"测试失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ButtonSync_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!SaveFromUi(forceEnabled: true)) return;

            var cfg = _store.Load();
            if (!cfg.CanSync())
            {
                StatusText.Text = "已保存，但还不能同步：服务器地址要以 https:// 开头，访问令牌不能为空。";
                return;
            }

            SetBusy(true, "正在同步配置…");
            var raw = await _sync.SyncNowAsync();
            StatusText.Text = DescribeResult(raw, syncing: true);
            UpdateStatusCard();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"同步失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ButtonDisable_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            EnabledCheck.IsChecked = false;
            if (!SaveFromUi()) return;

            _module.Disable();
            UpdateStatusCard();
            StatusText.Text = "已停用远程控制：不会再发起任何网络请求，重启后也不会。已保存的地址和令牌仍留在本机，方便以后重新启用。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"停用失败：{ex.Message}";
        }
    }

    private void SetBusy(bool busy, string? hint = null)
    {
        ButtonSaveTest.IsEnabled = !busy;
        ButtonSync.IsEnabled = !busy;
        ButtonDisable.IsEnabled = !busy;
        if (hint != null) StatusText.Text = hint;
    }

    // ---------------- 本机网页控制台 ----------------

    private async void WebConsoleCheck_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_web == null) return;

            if (WebConsoleCheck.IsChecked == true)
            {
                if (_web.IsRunning)
                {
                    UpdateWebUi();
                    return;
                }

                var cfg = _store.Load();
                var url = await _web.StartAsync(cfg.Loopback.Port);
                WebUrlBox.Text = url;
                ButtonOpenWeb.IsEnabled = true;
                ButtonCopyWeb.IsEnabled = true;
                StatusText.Text = "本机网页控制台已启动，点「打开」即可在浏览器里使用。关闭 ClassIsland 后自动停止。";
            }
            else
            {
                _web.Stop();
                WebUrlBox.Text = "";
                ButtonOpenWeb.IsEnabled = false;
                ButtonCopyWeb.IsEnabled = false;
                StatusText.Text = "本机网页控制台已停止。";
            }
        }
        catch (Exception ex)
        {
            WebConsoleCheck.IsChecked = _web?.IsRunning == true;
            StatusText.Text = $"网页控制台启动失败：{ex.Message}";
        }
    }

    private void ButtonOpenWeb_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var url = WebUrlBox.Text?.Trim();
            if (string.IsNullOrEmpty(url)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开浏览器失败：{ex.Message}";
        }
    }

    private async void ButtonCopyWeb_OnClick(object? sender, RoutedEventArgs e)
    {
        await CopyAsync(WebUrlBox.Text ?? "");
    }

    // ---------------- 诊断 ----------------

    private void ButtonOpenFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.ConfigDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开文件夹失败：{ex.Message}";
        }
    }

    private async void ButtonCopyDiag_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CopyAsync(BuildDiagnostics());
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制诊断信息失败：{ex.Message}";
        }
    }

    private string BuildDiagnostics()
    {
        var cfg = _store.Load();
        var (version, etag) = _state.Load();
        var sb = new StringBuilder();
        sb.AppendLine("ClassExland 远程控制诊断");
        sb.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"插件版本：{typeof(RemoteSettingsPage).Assembly.GetName().Version}");
        sb.AppendLine($"界面状态：{StatusBadge.Text} / {StatusSubText.Text}");
        sb.AppendLine($"启用：{cfg.Enabled}，可同步：{cfg.CanSync()}");
        sb.AppendLine($"服务器：{(string.IsNullOrEmpty(cfg.ServerUrl) ? "未填" : cfg.ServerUrl)}");
        sb.AppendLine($"设备名称：{(string.IsNullOrEmpty(cfg.DeviceName) ? "（未指定）" : cfg.DeviceName)}");
        sb.AppendLine($"清单路径：{cfg.ManifestPath}");
        sb.AppendLine($"访问令牌：{(string.IsNullOrEmpty(cfg.BearerToken) ? "未填" : $"已填（{cfg.BearerToken.Length} 字符）")}");
        sb.AppendLine($"签名密钥：{(string.IsNullOrEmpty(cfg.HmacSecret) ? "未填" : "已填")}");
        sb.AppendLine($"同步范围：快照={cfg.EnableSnapshotSync} 核心配置={cfg.EnableCoreConfigSync} 指令={cfg.EnableCommandQueue}");
        sb.AppendLine($"上次已应用：版本 {version}，etag {etag}");
        sb.AppendLine($"本机网页控制台：{(_web?.IsRunning == true ? _web.Url : "未启动")}");
        sb.AppendLine($"配置文件：{_paths.Get(ConfigFiles.Remote)}");
        sb.AppendLine($"审计日志：{_paths.AuditLogPath}");
        return sb.ToString();
    }

    private async Task CopyAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "没有可复制的内容。";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            StatusText.Text = "当前无法访问剪贴板。";
            return;
        }

        await clipboard.SetTextAsync(text);
        StatusText.Text = "已复制到剪贴板。";
    }

    // ---------------- 结果翻译 ----------------

    /// <summary>把同步服务返回的 JSON 摘要翻译成普通用户能看懂的一句话。</summary>
    private static string DescribeResult(string raw, bool syncing)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "";

            if (ok)
            {
                if (syncing)
                    return "✅ 同步完成" + (version.Length > 0 ? $"，已应用版本 {version}。" : "。");

                if (message.Contains("已是最新"))
                    return "✅ 连接成功" + (version.Length > 0 ? $"，服务器版本 {version}" : "") + "；当前已是最新，无需同步。";

                return "✅ 连接成功" + (version.Length > 0 ? $"，发现版本 {version}" : "") + "；点「立即同步」应用。";
            }

            return "❌ " + Explain(message);
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>把异常信息翻译成可执行的提示，而不是把英文堆给用户。</summary>
    private static string Explain(string message)
    {
        var m = message ?? "";
        if (m.Contains("401") || m.Contains("403"))
            return "访问令牌被服务器拒绝（HTTP 401/403）。请向管理员确认令牌是否有效、是否已过期。";
        if (m.Contains("404"))
            return "服务器上找不到配置文件。请确认地址是否正确，或检查高级选项里的清单路径。";
        if (m.Contains("超时") || m.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("TaskCanceled"))
            return "连接超时。请检查网络是否正常，或在高级选项里把超时时间调大。";
        if (m.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Name or service", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("nodename", StringComparison.OrdinalIgnoreCase))
            return "域名解析失败。请检查服务器地址是否写错。";
        if (m.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return "HTTPS 证书校验失败。请确认服务器证书有效并已正确配置。";
        if (m.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return "服务器拒绝连接。请确认地址、端口和服务器运行状态。";
        if (m.Contains("配置无效") || m.Contains("开关关闭"))
            return "还没填服务器地址或访问令牌。";
        if (m.Contains("HMAC") || m.Contains("签名"))
            return "签名校验失败：服务器清单的签名与本机填写的签名密钥不一致，请联系管理员核对。";

        return m.Length > 0 ? m : "未知错误，可展开「高级选项」复制诊断信息发给管理员。";
    }
}
