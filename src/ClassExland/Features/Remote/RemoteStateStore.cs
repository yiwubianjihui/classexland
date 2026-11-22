using System.Text.Json;
using ClassExland.Contracts;

namespace ClassExland.Features.Remote;

/// <summary>
/// 记录上次成功应用的版本/etag，以及双向 diff 所需的两类指纹：
///  - HeadHash：服务端当前快照的内容哈希（上传时当 base）；
///  - LocalHashes：上次同步后本机各配置文件的内容哈希（用来发现本地改动）。
/// 老文件只有 Version/ETag/AppliedAt，缺字段按空处理，升级无感。
/// </summary>
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
        var doc = Read();
        return (doc?.Version ?? "", doc?.ETag ?? "");
    }

    public string LoadHead()
    {
        var doc = Read();
        return doc?.HeadHash ?? "";
    }

    /// <summary>上次成功应用的时间；没有记录时返回 null（供设置页展示）。</summary>
    public DateTimeOffset? LoadAppliedAt()
    {
        var doc = Read();
        return doc?.AppliedAt == default ? null : doc?.AppliedAt;
    }

    /// <summary>上次同步后本机各配置文件的内容哈希。</summary>
    public IReadOnlyDictionary<string, string> LoadLocalHashes()
    {
        var doc = Read();
        return doc?.LocalHashes ?? new Dictionary<string, string>();
    }

    public (string LastConflict, DateTimeOffset? LastUploadAt) LoadUploadInfo()
    {
        var doc = Read();
        return (doc?.LastConflict ?? "", doc?.LastUploadAt);
    }

    /// <summary>已执行过的指令 id（最近 200 条），防止同一条指令被重复执行。</summary>
    public IReadOnlyList<string> LoadAppliedCommandIds()
    {
        var doc = Read();
        return doc?.AppliedCommandIds ?? new List<string>();
    }

    public bool MarkCommandApplied(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            var doc = Read() ?? new StateDoc();
            doc.AppliedCommandIds.Remove(id);
            doc.AppliedCommandIds.Insert(0, id);
            if (doc.AppliedCommandIds.Count > 200)
                doc.AppliedCommandIds = doc.AppliedCommandIds.GetRange(0, 200);
            return Write(doc);
        }
        catch
        {
            return false;
        }
    }

    public bool SaveApplied(string version, string etag, string head = "", IReadOnlyDictionary<string, string>? localHashes = null)
    {
        try
        {
            var doc = Read() ?? new StateDoc();
            doc.Version = version;
            doc.ETag = etag;
            doc.AppliedAt = DateTimeOffset.Now;
            if (!string.IsNullOrEmpty(head)) doc.HeadHash = head;
            if (localHashes is not null) doc.LocalHashes = new Dictionary<string, string>(localHashes);
            return Write(doc);
        }
        catch
        {
            return false;
        }
    }

    public bool SaveUploaded(string head, IReadOnlyDictionary<string, string> localHashes)
    {
        try
        {
            var doc = Read() ?? new StateDoc();
            doc.HeadHash = head;
            doc.LocalHashes = new Dictionary<string, string>(localHashes);
            doc.LastUploadAt = DateTimeOffset.Now;
            doc.LastConflict = "";
            return Write(doc);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 冲突时记下「这次本地内容已经上报过」，避免每轮轮询都重复入队同一份待审上传。
    /// 服务端接受/发布新版本后，轮询会发现新版本并重新收敛。
    /// </summary>
    public bool SaveConflict(string message, string head = "", IReadOnlyDictionary<string, string>? localHashes = null)
    {
        try
        {
            var doc = Read() ?? new StateDoc();
            doc.LastConflict = message.Length > 300 ? message[..300] : message;
            if (!string.IsNullOrEmpty(head)) doc.HeadHash = head;
            if (localHashes is not null) doc.LocalHashes = new Dictionary<string, string>(localHashes);
            return Write(doc);
        }
        catch
        {
            return false;
        }
    }

    private StateDoc? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<StateDoc>(File.ReadAllText(_path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private bool Write(StateDoc doc)
    {
        try
        {
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
        public string HeadHash { get; set; } = "";
        public Dictionary<string, string> LocalHashes { get; set; } = new();
        public DateTimeOffset? LastUploadAt { get; set; }
        public string LastConflict { get; set; } = "";
        public List<string> AppliedCommandIds { get; set; } = new();
    }
}
