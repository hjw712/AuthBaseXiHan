using Microsoft.EntityFrameworkCore;
using XiHan.Framework.Persistence.EntityFrameworkCore;

namespace AuthBaseXiHan.Persistence;

/// <summary>
/// 应用自己的 DbContext。
/// 由 XiHan 的 EF Core Provider 注册进容器（AddDbContext），
/// 同一作用域里仓储、工作单元与本类注入拿到的是同一个实例。
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    /// <summary>用户表</summary>
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            // 表名要在 ApplyPersistenceConventions 之前定好：索引名按表名生成
            entity.ToTable("Users");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.UserName).HasMaxLength(20).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(100).IsRequired();
            entity.Property(u => u.CreatedAt).IsRequired();
        });

        // 框架约定：[EntityIndex] → HasIndex、软删除唯一索引过滤条件、并发戳等
        modelBuilder.ApplyPersistenceConventions(Database);
    }
}
