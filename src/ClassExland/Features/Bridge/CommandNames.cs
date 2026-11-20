namespace ClassExland.Features.Bridge;

/// <summary>
/// CLI 与远程指令队列共用的命令名常量。
/// 远程模块构造 CommandRequest 时应直接使用这里的常量，保证寻址一致。
/// </summary>
public static class CommandNames
{
    public const string Status = "status";
    public const string Version = "version";
    public const string Migrate = "migrate";

    public static class Bell
    {
        public const string List = "bell.list";
        public const string Set = "bell.set";
        public const string Import = "bell.import";
        public const string Export = "bell.export";
        public const string Preview = "bell.preview";
    }

    public static class Duty
    {
        public const string Show = "duty.show";
        public const string Edit = "duty.edit";
        public const string Import = "duty.import";
        public const string Export = "duty.export";
        public const string Rotate = "duty.rotate";
        public const string Complete = "duty.complete";
    }

    public static class Activity
    {
        public const string List = "activity.list";
        public const string Add = "activity.add";
        public const string Edit = "activity.edit";
        public const string Test = "activity.test";
    }

    public static class Layout
    {
        public const string Switch = "layout.switch";
        public const string Current = "layout.current";
    }

    public static class Config
    {
        public const string Get = "config.get";
        public const string Set = "config.set";
        public const string Diff = "config.diff";
        public const string Doctor = "config.doctor";
    }

    public static class Remote
    {
        public const string Status = "remote.status";
        public const string Sync = "remote.sync";
        public const string Poll = "remote.poll";
        public const string Upload = "remote.upload";
        public const string Enable = "remote.enable";
        public const string Disable = "remote.disable";
        public const string Server = "remote.server";
    }

    public static class Backup
    {
        public const string List = "backup.list";
        public const string Create = "backup.create";
        public const string Restore = "backup.restore";
    }

    public static class Audit
    {
        public const string List = "audit.list";
        public const string Export = "audit.export";
    }

    public static class Web
    {
        public const string Start = "web.start";
        public const string Stop = "web.stop";
    }

    public static class ClassState
    {
        public const string Set = "class.set";
    }

    public static class Profile
    {
        public const string List = "profile.list";
        public const string Switch = "profile.switch";
        public const string Current = "profile.current";
    }

    /// <summary>所有会修改状态的命令，必须显式 --yes（Apply=true）。</summary>
    public static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        Bell.Set, Bell.Import,
        Duty.Edit, Duty.Import, Duty.Rotate, Duty.Complete,
        Activity.Add, Activity.Edit, Activity.Test,
        Layout.Switch,
        Config.Set,
        Remote.Sync, Remote.Enable, Remote.Disable, Remote.Server,
        Backup.Create, Backup.Restore,
        Migrate,
        Web.Start, Web.Stop,
        ClassState.Set,
        Profile.Switch
    };

    /// <summary>高危命令，除 --yes 外还需 --confirm yes 二次确认。</summary>
    public static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        Backup.Restore,
        Migrate,
        Remote.Server,
        Config.Set,
        ClassState.Set,
        Web.Start,
        Profile.Switch
    };

    public static bool IsWrite(string command) => WriteCommands.Contains(command);
    public static bool IsDangerous(string command) => DangerousCommands.Contains(command);
}
