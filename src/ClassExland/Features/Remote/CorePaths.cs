using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>
/// 定位 ClassIsland 的 data 目录（Settings.json 与 Profiles 所在）。
/// 从插件配置目录向上查找同时包含 Settings.json 与 Profiles 的目录，
/// 不依赖任何写死绝对路径，也不引用 SDK 内部服务类型。
/// </summary>
public sealed class CorePaths
{
    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "Settings.json");
    public string ProfilesDirectory => Path.Combine(DataDirectory, "Profiles");
    public string ComponentLayoutsDirectory => Path.Combine(DataDirectory, "Config", "ComponentLayouts");
    public bool IsValid => !string.IsNullOrEmpty(DataDirectory)
                           && File.Exists(SettingsPath)
                           && Directory.Exists(ProfilesDirectory);

    public CorePaths(IPluginPaths paths)
    {
        DataDirectory = FindDataDirectory(paths.ConfigDirectory) ?? "";
    }

    private static string? FindDataDirectory(string configDirectory)
    {
        try
        {
            var dir = new DirectoryInfo(configDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Settings.json")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "Profiles")))
                    return dir.FullName;
            }
        }
        catch { /* 忽略 */ }
        return null;
    }
}
