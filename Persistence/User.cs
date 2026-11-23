using XiHan.Framework.Domain.Entities;
using XiHan.Framework.Persistence.Abstractions;

namespace AuthBaseXiHan.Persistence;

/// <summary>
/// 用户实体（纯 POCO，不依赖 EF Core）。
/// 实现框架的 <see cref="IEntity{TKey}"/> 协议才能登记仓储；
/// 唯一索引由 [EntityIndex] 声明，Provider 在建模时映射为 HasIndex —— 换 Provider 不用改这里。
/// </summary>
[EntityIndex(nameof(UserName), IsUnique = true, Name = "UX_Users_UserName")]
public sealed class User : IEntity<long>
{
    /// <summary>主键：整数主键按 EF Core 约定由数据库自增生成</summary>
    public long Id { get; set; }

    /// <summary>登录账号，全表唯一</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>BCrypt 加盐哈希，绝不存明文</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>注册时间（UTC）</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
