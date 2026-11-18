using System.Reflection;

namespace ClassExland.Features.Remote;

/// <summary>
/// 多班级档案切换。通过反射弱依赖宿主官方 IPublicProfileService，
/// 读取/设置其 CurrentProfilePath（官方档案切换入口）；绝不自行改 Settings.json 的 SelectedProfile。
///
/// 重要：SwitchProfileAsync 只带 profileName 一个参数，且【不再调用 ICommandDispatcher】
/// —— Bridge 的 profile.switch 会反射调用本服务，若再回调命令层会无限递归。
/// 切换前后做路径穿越校验 + 备份 + 失败回滚。
/// </summary>
public sealed class ProfileSwitchService : IProfileService
{
    private const string PublicProfileTypeName =
        "ClassIsland.Shared.IPC.Abstractions.Services.IPublicProfileService";

    private readonly CorePaths _paths;
    private readonly RemoteAuditLogger _audit;
    private readonly SafeConfigWriter _writer;
    private readonly object _gate = new();

    private Type? _profileType;
    private object? _profileService;

    public ProfileSwitchService(CorePaths paths, RemoteAuditLogger audit, SafeConfigWriter writer)
    {
        _paths = paths;
        _audit = audit;
        _writer = writer;
    }

    public string CurrentProfile
    {
        get
        {
            try
            {
                var (service, prop) = Resolve();
                if (service is null || prop is null) return "";
                var value = prop.GetValue(service) as string;
                return string.IsNullOrEmpty(value) ? "" : Path.GetFileName(value);
            }
            catch
            {
                return "";
            }
        }
    }

    public IReadOnlyList<string> ListProfiles()
    {
        try
        {
            if (!_paths.IsValid) return Array.Empty<string>();
            return Directory.GetFiles(_paths.ProfilesDirectory, "*.json")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public Task<bool> SwitchProfileAsync(string profileName)
    {
        try
        {
            if (!IsSafeProfileName(profileName))
            {
                _audit.Log("profile-switch", $"非法档案名或路径穿越：{profileName}", ok: false);
                return Task.FromResult(false);
            }

            var (service, prop) = Resolve();
            if (service is null || prop is null || !prop.CanWrite)
            {
                _audit.Log("profile-switch", "未解析到可写的宿主 IPublicProfileService.CurrentProfilePath", ok: false);
                return Task.FromResult(false);
            }

            var targetPath = Path.Combine(_paths.ProfilesDirectory, profileName);
            if (!File.Exists(targetPath))
            {
                _audit.Log("profile-switch", $"档案文件不存在：{profileName}", ok: false);
                return Task.FromResult(false);
            }

            var oldValue = prop.GetValue(service) as string;
            var backup = _writer.BackupFile(targetPath);

            try
            {
                // 官方入口：设置当前档案路径（文件名），由宿主完成档案加载与保存。
                prop.SetValue(service, profileName);

                var actual = prop.GetValue(service) as string;
                if (!string.Equals(Path.GetFileName(actual ?? ""), profileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"切换后当前档案仍为 {Path.GetFileName(actual ?? "<空>")}");

                _audit.Log("profile-switch", $"已通过 IPublicProfileService.CurrentProfilePath 切换：{profileName}（备份 {(string.IsNullOrEmpty(backup) ? "失败" : backup)}）");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                // 失败回滚：恢复原 CurrentProfilePath，并用备份还原目标档案文件。
                TryRestore(prop, service, oldValue);
                if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                    _writer.RollbackAll(new[] { new ApplyFileResult { Path = targetPath, BackupPath = backup, Ok = true } });

                _audit.Log("profile-switch-rollback", $"{profileName}: {ex.GetType().Name}: {ex.Message}", ok: false);
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            _audit.Log("profile-switch-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
            return Task.FromResult(false);
        }
    }

    private static void TryRestore(PropertyInfo prop, object service, string? oldValue)
    {
        try { if (oldValue is not null) prop.SetValue(service, oldValue); } catch { }
    }

    private static bool IsSafeProfileName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name == Path.GetFileName(name)
           && name.IndexOf("..", StringComparison.Ordinal) < 0
           && Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private (object? Service, PropertyInfo? Property) Resolve()
    {
        lock (_gate)
        {
            try
            {
                _profileType ??= AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .FirstOrDefault(t =>
                        t.FullName == PublicProfileTypeName ||
                        (t.Name == "IPublicProfileService" && t.Namespace?.Contains("Shared.IPC") == true));

                if (_profileType is null)
                {
                    _audit.Log("profile-resolve", "未找到 IPublicProfileService 类型", ok: false);
                    return (null, null);
                }

                _profileService ??= TryResolve(_profileType);
                if (_profileService is null)
                {
                    _audit.Log("profile-resolve", "IAppHost 未注册 IPublicProfileService", ok: false);
                    return (null, null);
                }

                var prop = _profileService.GetType()
                    .GetProperty("CurrentProfilePath", BindingFlags.Public | BindingFlags.Instance);
                return (_profileService, prop);
            }
            catch (Exception ex)
            {
                _audit.Log("profile-resolve-exception", $"{ex.GetType().Name}: {ex.Message}", ok: false);
                return (null, null);
            }
        }
    }

    private static object? TryResolve(Type serviceType) => HostServiceResolver.TryGet(serviceType);

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
        catch { return Array.Empty<Type>(); }
    }
}
