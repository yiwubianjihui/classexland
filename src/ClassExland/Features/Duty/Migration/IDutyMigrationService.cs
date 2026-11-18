namespace ClassExland.Features.Duty.Migration;

/// <summary>
/// 供 CLI/远程指令通过 IAppHost 解析的迁移入口（反射弱依赖，勿改名）。
/// </summary>
public interface IDutyMigrationService
{
    /// <summary>
    /// 从旧插件配置导入。from 可以是文件，也可以是旧插件配置目录；plan 为指定的目标计划名。
    /// 保留 2 参数重载以兼容当前 Bridge；3 参数重载供显式传入取消令牌的调用方。
    /// </summary>
    Task<string> MigrateAsync(string from, string plan);

    Task<string> MigrateAsync(string from, string plan, CancellationToken ct);
}
