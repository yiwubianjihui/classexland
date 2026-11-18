using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>记录上次成功应用的版本/etag，避免重复应用同一份配置。</summary>
public sealed class RemoteStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RemoteStateStore(IPluginPaths paths)
    {
        _path = paths.Get("remote-state.json");
    }

    public (string Version, string ETag) Load()
    {
        try
        {
            if (!File.Exists(_path)) return ("", "");
            var doc = JsonSerializer.Deserialize<StateDoc>(File.ReadAllText(_path), JsonOptions);
            return (doc?.Version ?? "", doc?.ETag ?? "");
        }
        catch
        {
            return ("", "");
        }
    }

    public bool SaveApplied(string version, string etag)
    {
        try
        {
            var doc = new StateDoc { Version = version, ETag = etag, AppliedAt = DateTimeOffset.Now };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class StateDoc
    {
        public string Version { get; set; } = "";
        public string ETag { get; set; } = "";
        public DateTimeOffset AppliedAt { get; set; }
    }
}
