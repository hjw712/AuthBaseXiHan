using AuthBaseXiHan.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthBaseXiHan.Sessions.Admin;

/// <summary>
/// 超级管理员引导种子：管理账号没有注册接口，第一个超级管理员从配置落地。
/// </summary>
/// <remarks>
/// 配置键 Admin:Bootstrap:UserName / Admin:Bootstrap:Password（建议放用户机密或
/// appsettings.Development.json，不进仓库）。服务启动时执行：
/// - 账号不存在 → 按配置开通（IsSuperAdmin=true）；
/// - 账号已存在 → 什么都不做（运行期改动以管理接口为准，配置只负责「从无到有」）；
/// - 配置缺失 → 跳过（空表状态，首次调用管理接口前必须先补配置）。
/// </remarks>
public static class AdminBootstrap
{
    /// <summary>确保配置声明的超级管理员存在于 AdminUsers 表</summary>
    public static async Task SeedAsync(AuthDbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var userName = configuration["Admin:Bootstrap:UserName"]?.Trim();
        var password = configuration["Admin:Bootstrap:Password"];

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return; // 未配置引导账号：跳过，不算错误
        }

        var exists = await db.AdminUsers.AsNoTracking()
            .AnyAsync(u => u.UserName == userName, cancellationToken);
        if (exists)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        db.AdminUsers.Add(new AdminUser
        {
            UserName = userName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            IsSuperAdmin = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
