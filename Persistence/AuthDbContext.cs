using AuthBaseXiHan.Sessions.Ai;
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

    /// <summary>管理员表（独立于用户表，无注册入口，账号由超管开通）</summary>
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    /// <summary>模型凭据表</summary>
    public DbSet<LlmProviderConfig> LlmConfigs => Set<LlmProviderConfig>();

    /// <summary>提示词表（与模型配置解耦，对话按 promptAlias 引用）</summary>
    public DbSet<PromptConfig> Prompts => Set<PromptConfig>();

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

        // 管理员表：账号全表唯一；独立于 Users，两套账号互不混用
        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.ToTable("AdminUsers");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.UserName).HasMaxLength(20).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(100).IsRequired();
            entity.Property(u => u.CreatedAt).IsRequired();
            entity.Property(u => u.UpdatedAt).IsRequired();
        });

        // 模型凭据：(租户, 别名) 唯一，保证一个租户下 Alias 不重复
        modelBuilder.Entity<LlmProviderConfig>(entity =>
        {
            entity.ToTable("LlmProviderConfigs");
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => new { c.TenantId, c.Alias }).IsUnique();
            entity.Property(c => c.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(c => c.Alias).HasMaxLength(50).IsRequired();
            entity.Property(c => c.Provider).HasMaxLength(40).IsRequired();
            entity.Property(c => c.Endpoint).HasMaxLength(500).IsRequired();
            entity.Property(c => c.ApiKeyEncrypted).IsRequired();
            entity.Property(c => c.Model).HasMaxLength(100).IsRequired();
        });

        // 提示词表：(租户, 别名) 唯一；内容 4000 字符与模型配置 SystemPrompt 上限一致
        modelBuilder.Entity<PromptConfig>(entity =>
        {
            entity.ToTable("Prompts");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.TenantId, p.Alias }).IsUnique();
            entity.Property(p => p.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(p => p.Alias).HasMaxLength(50).IsRequired();
            entity.Property(p => p.Content).HasMaxLength(4000).IsRequired();
            entity.Property(p => p.CreatedAt).IsRequired();
            entity.Property(p => p.UpdatedAt).IsRequired();
        });

        // 框架约定：[EntityIndex] → HasIndex、软删除唯一索引过滤条件、并发戳等
        modelBuilder.ApplyPersistenceConventions(Database);
    }
}
