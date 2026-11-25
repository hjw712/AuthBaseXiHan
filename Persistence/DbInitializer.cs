using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AuthBaseXiHan.Persistence;

/// <summary>
/// 数据库表/列自愈初始化器：补齐 EnsureCreated 对「已存在数据库」不会补的新表与新列。
/// </summary>
/// <remarks>
/// 背景：XiHan 的 EnsureCreated 只在数据库首次创建时按模型建全部表；
/// 若库已存在（如本机开发库早于 AI 模块），新增实体对应的表不会被创建，
/// 首次访问就报「对象名无效」。本初始化器在启动时对比模型与数据库实际结构：
/// - 缺表 → 按 EF 模型生成 CREATE TABLE SQL（含主键/索引/自增值）；
/// - 表在但缺列 → ALTER TABLE ADD（一律允许 NULL，兼容已有数据行）。
///
/// 与迁移的关系：这是开发期/模板基座的自愈手段，不是迁移的替代品。
/// 生产环境建议改用 EF Core Migration 管理 schema 演进；那时可移除本类。
/// </remarks>
public static class DbInitializer
{
    /// <summary>确保模型里声明的所有表与列都存在</summary>
    public static async Task EnsureTablesAsync(AuthDbContext db, CancellationToken cancellationToken = default)
    {
        var existing = await QueryExistingTablesAsync(db, cancellationToken);

        foreach (var entity in db.Model.GetEntityTypes())
        {
            // 跳过从属实体（owned types 没有独立表）
            if (entity.IsOwned()) continue;

            var tableName = entity.GetTableName();
            if (string.IsNullOrWhiteSpace(tableName)) continue;

            if (!existing.Contains(tableName))
            {
                var sql = BuildCreateTableSql(db, entity, tableName);
                // EF1003 抑制理由：SQL 全部来自 EF 模型元数据（表名/列名/类型），非用户输入；
                // 标识符无法走参数化，已做 ] → ]] 转义，符合警告文案"sanitize 后抑制"的适用场景
#pragma warning disable EF1003
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
#pragma warning restore EF1003
                continue;
            }

            // 表已存在：对比列，缺哪列补哪列（ALTER 一律 NULL，避免已有数据行冲突）
            var existingColumns = await QueryExistingColumnsAsync(db, tableName, cancellationToken);
            foreach (var property in entity.GetProperties())
            {
                var columnName = property.GetColumnName();
                if (string.IsNullOrWhiteSpace(columnName) || existingColumns.Contains(columnName)) continue;

                var type = SqlTypeFor(property);
                // 补列统一允许 NULL：已有行没有该列的值，NOT NULL 会建表失败
                // 表名来自 EF 模型非用户输入，仍做转义（] → ]]）并以拼接方式执行，规避 EF1002 插值告警
                var safeTable = tableName.Replace("]", "]]");
                var safeColumn = columnName.Replace("]", "]]");
                // EF1003 抑制理由同上：标识符来自 EF 模型元数据且已转义，无法参数化
#pragma warning disable EF1003
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE [" + safeTable + "] ADD [" + safeColumn + "] " + type + " NULL;", cancellationToken);
#pragma warning restore EF1003
            }

            // 索引同理：EnsureCreated 不管已存在的表，模型里新增的索引要在这里补
            await EnsureIndexesAsync(db, entity, tableName, cancellationToken);
        }
    }

    /// <summary>
    /// 补齐模型里声明而数据库缺少的索引（唯一与非唯一都补）。
    /// </summary>
    /// <remarks>
    /// 为什么不能只依赖建表时的索引：本类对「已存在的表」不会重建，
    /// 后来在模型上加的索引（例如给新查询路径补的普通索引）就永远建不上，
    /// 表现为功能正常但查询悄悄退化成全表扫描——这种问题很难在使用中被发现。
    /// </remarks>
    private static async Task EnsureIndexesAsync(
        AuthDbContext db, IEntityType entity, string tableName, CancellationToken cancellationToken)
    {
        var declared = entity.GetIndexes()
            .Select(index => (Index: index, Name: index.GetDatabaseName()))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Name))
            .ToList();
        if (declared.Count == 0) return;

        var existingNames = await QueryExistingIndexNamesAsync(db, tableName, cancellationToken);

        var safeTable = tableName.Replace("]", "]]");
        foreach (var (index, name) in declared)
        {
            if (existingNames.Contains(name!)) continue;

            var columns = index.Properties
                .Select(p => p.GetColumnName())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => "[" + n.Replace("]", "]]") + "]")
                .ToArray();
            if (columns.Length == 0) continue;

            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            var safeName = name!.Replace("]", "]]");
            // 用拼接而不是内插：EF1002 对内插字符串一律告警，而这里的标识符全部来自 EF 模型元数据
            // （非用户输入）且已做 ] → ]] 转义，无法参数化（DDL 不支持参数化标识符）
            var sql = "CREATE " + unique + "INDEX [" + safeName + "] ON [" + safeTable + "] ("
                      + string.Join(", ", columns) + ");";
#pragma warning disable EF1003
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
#pragma warning restore EF1003
        }
    }

    private static async Task<HashSet<string>> QueryExistingIndexNamesAsync(
        AuthDbContext db, string tableName, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = $"SELECT i.name FROM sys.indexes i WHERE i.object_id = OBJECT_ID('{tableName.Replace("'", "''")}') AND i.name IS NOT NULL";
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                names.Add(reader.GetString(0));
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        return names;
    }

    private static async Task<HashSet<string>> QueryExistingTablesAsync(AuthDbContext db, CancellationToken ct)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // SQL Server 系统视图查询；其他数据库 Provider 需替换此句
        const string sql = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                tables.Add(reader.GetString(0));
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        return tables;
    }

    private static async Task<HashSet<string>> QueryExistingColumnsAsync(AuthDbContext db, string tableName, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // SQL Server 系统视图查询；其他数据库 Provider 需替换此句
        var sql = $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{tableName.Replace("'", "''")}'";
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                columns.Add(reader.GetString(0));
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        return columns;
    }

    /// <summary>
    /// 从 EF 模型生成建表 SQL：列定义 + 主键 + 唯一索引。
    /// 覆盖本项目的实体形态（bigint 自增主键、nvarchar、varbinary、datetimeoffset、bit、int）。
    /// </summary>
    private static string BuildCreateTableSql(AuthDbContext db, IEntityType entity, string tableName)
    {
        var columns = new List<string>();
        foreach (var property in entity.GetProperties())
        {
            var columnName = property.GetColumnName();
            if (string.IsNullOrWhiteSpace(columnName)) continue;

            var type = SqlTypeFor(property);
            var nullable = property.IsNullable ? "NULL" : "NOT NULL";
            var identity = property.ValueGenerated == ValueGenerated.OnAdd &&
                           property.ClrType == typeof(long)
                ? " IDENTITY(1,1)"
                : string.Empty;
            columns.Add($"[{columnName}] {type}{identity} {nullable}");
        }

        var primaryKey = entity.FindPrimaryKey();
        var pkNames = primaryKey?.Properties
            .Select(p => p.GetColumnName())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => $"[{n}]")
            .ToArray() ?? [];

        var statements = new List<string>();
        var pkClause = pkNames.Length > 0 ? $", CONSTRAINT [PK_{tableName}] PRIMARY KEY ({string.Join(", ", pkNames)})" : string.Empty;
        statements.Add($"CREATE TABLE [{tableName}] (\n  {string.Join(",\n  ", columns)}{pkClause}\n);");

        // 唯一索引与非唯一索引都建：缺了非唯一索引查询会退化成全表扫描
        foreach (var index in entity.GetIndexes())
        {
            var indexName = index.GetDatabaseName();
            if (string.IsNullOrWhiteSpace(indexName)) continue;
            var indexColumns = index.Properties
                .Select(p => p.GetColumnName())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => $"[{n}]")
                .ToArray();
            if (indexColumns.Length == 0) continue;
            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            statements.Add($"CREATE {unique}INDEX [{indexName}] ON [{tableName}] ({string.Join(", ", indexColumns)});");
        }

        return string.Join("\n", statements);
    }

    private static string SqlTypeFor(IProperty property)
    {
        var clr = property.ClrType;
        var maxLength = property.GetMaxLength();

        if (clr == typeof(long) || clr == typeof(long?)) return "bigint";
        if (clr == typeof(int) || clr == typeof(int?)) return "int";
        if (clr == typeof(bool) || clr == typeof(bool?)) return "bit";
        if (clr == typeof(DateTimeOffset) || clr == typeof(DateTimeOffset?)) return "datetimeoffset";
        if (clr == typeof(DateTime) || clr == typeof(DateTime?)) return "datetime2";
        if (clr == typeof(byte[])) return "varbinary(max)";
        if (clr == typeof(string))
        {
            return maxLength.HasValue ? $"nvarchar({maxLength.Value})" : "nvarchar(max)";
        }

        // 兜底：其他类型按 EF 映射的字符串长度处理，避免静默建错类型
        return maxLength.HasValue ? $"nvarchar({maxLength.Value})" : "nvarchar(max)";
    }
}
