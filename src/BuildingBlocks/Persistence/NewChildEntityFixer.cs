using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace YourInterview.BuildingBlocks.Persistence;

/// <summary>
/// 修正「客户端生成 Guid 主键的子实体被 EF 误判为 Modified」的问题。
///
/// 背景:聚合根的 Guid 主键在构造函数里就赋值了。当父聚合被跟踪、
/// 我们往它的子集合里 new 一个新子实体时,EF 的默认判断会出错:
///   - 判成 Modified → 生成 UPDATE,而行还不存在 → DbUpdateConcurrencyException(影响 0 行)
///   - 强行判成 Added 但行已存在 → PK 冲突(23505)
///
/// ⚠️ 不要用启发式去猜(主键是否为 Empty、原值是否为空、"状态是 Modified 就转 Added"等)——
/// 这些在「已有实体被改字段」与「新实体刚加入」两种情况下长得一模一样,猜错就是 500:
/// Jobs 服务曾用"Modified 一律转 Added"的粗暴版,结果"修改既有轮次"全被变成
/// INSERT → 主键冲突 → 消息重试三次进死信(2026-09-24 M1.5 实测)。
///
/// 最终可靠做法:直接问数据库这行在不在。子实体数量极少,不会 N+1。
/// </summary>
public static class NewChildEntityFixer
{
    /// <summary>
    /// 把「EF 误判成 Modified 的新子实体」标回 Added。真被修改的既有实体保持 Modified。
    /// defaultSchema:表元数据没写 schema 时的兜底(各服务 schema 不同,由调用方传入)。
    /// </summary>
    public static void MarkNewChildrenAsAdded(this DbContext context, string defaultSchema)
    {
        foreach (var entry in context.ChangeTracker.Entries()
                     .Where(e => e.State == EntityState.Modified))
        {
            if (entry.Metadata.FindPrimaryKey() is null) continue;

            // 只处理单列 Guid 主键(我们的实体全部如此;其它形态原样放过)
            var keyProps = entry.Metadata.FindPrimaryKey()!.Properties;
            if (keyProps.Count != 1) continue;
            if (keyProps[0].ClrType != typeof(Guid)) continue;

            var id = (Guid)entry.Property(keyProps[0].Name).CurrentValue!;
            if (id == Guid.Empty) continue;

            // 直接查库:这一行到底存不存在?
            if (!RowExists(context, entry, keyProps[0].Name, id, defaultSchema))
            {
                // 库里没有这行 → 它是本次新建的(被 EF 误判成 Modified)
                entry.State = EntityState.Added;
            }
            // 库里已经有这行 → 真的是改字段,保持 Modified
        }
    }

    /// <summary>
    /// 用原生 SQL 查存在性 —— 不经过 EF 实体验证,避免触发递归跟踪。
    /// 查询失败时当已存在(保持原状态):宁可报并发错,也不要错误地插重复行。
    /// </summary>
    private static bool RowExists(DbContext context, EntityEntry entry, string keyName,
        Guid id, string defaultSchema)
    {
        var table = entry.Metadata.GetTableName();
        var schema = entry.Metadata.GetSchema() ?? defaultSchema;
        if (table is null) return true;

        try
        {
            var conn = context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM \"{schema}\".\"{table}\" WHERE \"{keyName}\" = @id LIMIT 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "@id";
            p.Value = id;
            cmd.Parameters.Add(p);

            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return true;
        }
    }
}
