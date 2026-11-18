using ClassExland.Contracts;

namespace ClassExland.Infra;

public sealed class PluginPaths : IPluginPaths
{
    public PluginPaths(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    public string ConfigDirectory { get; }

    public string BackupDirectory => Path.Combine(ConfigDirectory, "Backups");

    public string AuditLogPath => Path.Combine(ConfigDirectory, "audit.log");

    public string CacheDirectory => Path.Combine(ConfigDirectory, "Cache");

    public string Get(string relativeFileName)
    {
        var path = Path.Combine(ConfigDirectory, relativeFileName);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return path;
    }
}
