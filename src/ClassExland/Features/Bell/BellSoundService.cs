using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.Logging;

namespace ClassExland.Features.Bell;

/// <summary>
/// 预备铃声音：完全走宿主 IAudioService，不自带任何音频解码逻辑。
/// 宿主未提供音频服务或配置缺失时静默降级为无声音，绝不抛异常导致插件被禁用。
/// </summary>
public sealed class BellSoundService
{
    private readonly BellSettings _settings;
    private readonly ILogger<BellSoundService> _logger;
    private readonly IAudioService? _audio;
    private bool _warnedMissingService;

    public BellSoundService(BellSettings settings, ILogger<BellSoundService> logger)
    {
        _settings = settings;
        _logger = logger;
        try
        {
            // IAppHost.GetService 取不到会抛异常，必须用 TryGetService。
            _audio = IAppHost.TryGetService<IAudioService>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取宿主音频服务失败，预备铃将以静音方式工作。");
            _audio = null;
        }
    }

    /// <summary>到点播放预备铃。</summary>
    public void PlayBell()
    {
        try
        {
            if (!_settings.EnableBellSound) return;
            PlayCurrent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "播放预备铃失败，已忽略。");
        }
    }

    /// <summary>设置页试听。</summary>
    public void PlayTest()
    {
        try
        {
            PlayCurrent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "试听预备铃失败，已忽略。");
        }
    }

    private void PlayCurrent()
    {
        var path = _settings.BellSoundPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogInformation("未配置有效的铃声文件，跳过播放。");
            return;
        }
        if (_audio == null)
        {
            if (!_warnedMissingService)
            {
                _warnedMissingService = true;
                _logger.LogWarning("宿主未提供 IAudioService，预备铃静音。");
            }
            return;
        }

        var volume = Math.Clamp(_settings.BellVolume, 0f, 1f);
        var audio = _audio; // 局部变量避免闭包捕获可空字段导致的 CS8602
        _ = Task.Run(async () =>
        {
            try
            {
                await using var stream = File.OpenRead(path);
                await audio.PlayAudioAsync(stream, volume).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "预备铃播放失败，已忽略。");
            }
        });
    }
}
