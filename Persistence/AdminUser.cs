using XiHan.Framework.Domain.Entities;
using XiHan.Framework.Persistence.Abstractions;

namespace AuthBaseXiHan.Persistence;

/// <summary>
/// 管理员实体（纯 POCO，不依赖 EF Core）。与普通用户（<see cref="User"/>）完全独立的表：
/// 管理账号不走注册，只由超级管理员开通；主键独立自增，与会话轨道互不干扰。
/// 实现框架的 <see cref="IEntity{TKey}"/> 协议才能登记仓储；
/// 唯一索引由 [EntityIndex] 声明，Provider 在建模时映射为 HasIndex —— 换 Provider 不用改这里。
/// </summary>
[EntityIndex(nameof(UserName), IsUnique = true, Name = "UX_AdminUsers_UserName")]
public sealed class AdminUser : IEntity<long>
{
    /// <summary>主键：整数主键按 EF Core 约定由数据库自增生成</summary>
    public long Id { get; set; }

    /// <summary>登录账号，全表唯一</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>BCrypt 加盐哈希，绝不存明文</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>是否超级管理员：超级管理员可开通/管理其他管理员账号</summary>
    public bool IsSuperAdmin { get; set; }

    /// <summary>是否启用：停用后不能再登录（已有会话在管理操作时被吊销）</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>开通时间（UTC）</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最近更新时间（UTC）</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
