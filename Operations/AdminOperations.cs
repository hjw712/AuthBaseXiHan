using System.Text.RegularExpressions;
using AuthBaseXiHan.Persistence;
using AuthBaseXiHan.Sessions.Admin;
using AuthBaseXiHan.Sessions.Common;
using AuthBaseXiHan.Sessions.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Persistence.Abstractions.Repositories;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Operations;

// 管理员模块：与 auth 同款流程但没有注册接口——管理账号只由超级管理员开通。
// 会话双轨复用（JWT 验签 + Redis 吊销），用户索引键带 admin: 前缀与会话轨道隔离，
// 避免 AdminUsers 与 Users 两张表的 long 主键撞号导致强制下线误吊销。
// 角色声明：所有管理员登录带 role=admin；超级管理员另带 admin.super 与 llm.admin
//（超级管理员天然可管理模型凭据，无需再配 Ai:AdminUsers）。

#region 管理员登录
/// <summary>管理员登录请求（与 auth.login 的契约各自独立，互不引用）</summary>
/// <param name="CaptchaId">图形验证码 ID（GET /common:captcha 返回，两轨共用同一验证码服务）</param>
/// <param name="CaptchaCode">用户输入的验证码字符（忽略大小写），一次有效</param>
/// <param name="UserName">账号</param>
/// <param name="Password">密码（明文，HTTPS 传输）</param>
public sealed record AdminLoginRequest(string CaptchaId, string CaptchaCode, string UserName, string Password);

/// <summary>管理员登录响应</summary>
/// <param name="Token">会话令牌，有效期 7 天（token_kind=admin）</param>
/// <param name="ExpiresIn">有效期秒数</param>
/// <param name="Roles">签发到令牌的角色（admin 恒有；超管另有 admin.super 与 llm.admin，是否超管以含 admin.super 判断）</param>
public sealed record AdminLoginResponse(string Token, int ExpiresIn, IReadOnlyList<string> Roles);

/// <summary>
/// 管理员登录操作：契约 ID admin.login → HTTP 适配器自动映射 POST /admin:login。
/// 独立完整实现（与 auth.login 分开写，各自持有契约与实现，不共用基类）：
/// 流程调用 <see cref="CredentialAuthenticator"/>（验证码 + 防时序比对 + BCrypt + 会话登记），
/// 本轨差异：SessionScope.Admin 轨道、查 AdminUsers 表投影账号档案（含角色）、ADMIN_* 错误码映射。
/// 验证码复用 GET /common:captcha（同一 Redis 存储）；签发的令牌带 token_kind=admin，
/// 管理接口由 AdminPolicy / AdminSuperPolicy 校验轨道与角色
/// </summary>
[Operation(AdminLoginOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http)]
public sealed class AdminLoginOperation(AuthDbContext db, CredentialAuthenticator authenticator)
    : IOperation<AdminLoginRequest, AdminLoginResponse>
{
    public const string Id = "admin.login";

    /// <summary>模型管理角色名：与 LlmAdminPolicy 的角色判定一致（字符串常量，避免循环依赖程序集内部类型）</summary>
    internal const string LlmAdminRoleClaim = "llm.admin";

    public async ValueTask<Result<AdminLoginResponse>> HandleAsync(
        AdminLoginRequest request, InvocationContext context)
    {
        var result = await authenticator.LoginAsync(
            request.CaptchaId, request.CaptchaCode, request.UserName.Trim(), request.Password,
            SessionScope.Admin, LookupAsync, context.CancellationToken);

        if (result.Outcome is { } outcome)
        {
            return Result.Success(new AdminLoginResponse(outcome.Token, outcome.ExpiresIn, outcome.Account.Roles));
        }

        // Outcome 为空时 Failure 必有值（LoginResult 的不变式）
        return Result.Failure<AdminLoginResponse>(MapFailure(result.Failure!.Value));
    }

    // 账号来源：AdminUsers 表。角色在查表时一并投影：普通管理员 admin；
    // 超级管理员追加 admin.super 与 llm.admin（模型配置管理一并接管）
    private async ValueTask<CredentialAuthenticator.AccountRecord?> LookupAsync(
        string userName, CancellationToken cancellationToken)
    {
        var admin = await db.AdminUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);
        if (admin is null)
        {
            return null;
        }

        var roles = new List<string> { AdminPolicy.PolicyName };
        if (admin.IsSuperAdmin)
        {
            roles.Add(AdminSuperPolicy.PolicyName);
            roles.Add(LlmAdminRoleClaim);
        }

        return new CredentialAuthenticator.AccountRecord(admin.Id, admin.UserName, admin.PasswordHash, roles, admin.IsActive);
    }

    // 失败种类 → ADMIN_* 错误码。停用账号文案与密码错误同码（ADMIN_DENIED_004），
    // 不给攻击者「账号存在但停用」的额外信号；文档里保留 005 供内部区分日志
    private static Error MapFailure(CredentialAuthenticator.FailureKind failure) => failure switch
    {
        CredentialAuthenticator.FailureKind.CaptchaInvalid =>
            Error.Validation("ADMIN_VALIDATION_003", "admin.captcha_invalid")
                .WithText("验证码错误或已过期，请刷新后重试"),
        _ => Error.Denied("ADMIN_DENIED_004", "admin.credentials_invalid").WithText("账号或密码错误"),
    };
}
#endregion

#region 管理员列表
/// <summary>
/// 管理员列表：契约 ID admin.users → GET /admin:users（ReadOnly，分页 + 关键字筛选）。
/// 请求/响应复用通用分页契约（Operations/Paging.cs）：SessionPagedRequest / SessionPagedResponse&lt;AdminUserItem&gt;，
/// 分页参数由 Paging.Validate 按 "Paging" 配置节校验（DefaultPageSize / MaxPageSize / MaxPage 全局可配）。
/// Query 筛选：对账号做包含匹配（通配符转义按字面处理）。
/// 只回账号元信息，密码哈希永不外泄
/// </summary>
[Operation(AdminUsersOperation.Id, Expose = Expose.Http, ReadOnly = true,
    Stability = Stability.Experimental, Policy = AdminSuperPolicy.PolicyName)]
public sealed class AdminUsersOperation(AuthDbContext db, IOptions<PagingOptions> paging)
    : IOperation<SessionPagedRequest, SessionPagedResponse<AdminUserItem>>
{
    public const string Id = "admin.users";

    public async ValueTask<Result<SessionPagedResponse<AdminUserItem>>> HandleAsync(
        SessionPagedRequest request, InvocationContext context)
    {
        // 分页参数校验与规范化（默认页大小/上限来自 Paging 配置）
        var pagingResult = Paging.Validate(request, paging.Value);
        if (!pagingResult.TryGetValue(out var pagingValues))
        {
            return Result.Failure<SessionPagedResponse<AdminUserItem>>(pagingResult.Error!);
        }
        var (page, pageSize) = pagingValues;

        var query = db.AdminUsers.AsNoTracking().AsQueryable();

        // Query 关键字筛选：账号包含匹配（SQL Server 默认排序规则不区分大小写）
        var keyword = request.Query?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            var pattern = "%" + Paging.EscapeLike(keyword) + "%";
            query = query.Where(u => EF.Functions.Like(u.UserName, pattern));
        }

        // 总数单独查（筛选后、不带排序与投影），items 再 Skip/Take——前端需要 total 渲染页码
        var total = await query.LongCountAsync(context.CancellationToken);
        var items = await query
            .OrderBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminUserItem(u.Id, u.UserName, u.IsSuperAdmin, u.IsActive, u.CreatedAt, u.UpdatedAt))
            .ToListAsync(context.CancellationToken);

        return Result.Success(new SessionPagedResponse<AdminUserItem>(items, total, page, pageSize));
    }
}

/// <summary>管理员项（不含凭据）</summary>
/// <param name="AdminId">管理员 ID</param>
/// <param name="UserName">账号</param>
/// <param name="IsSuperAdmin">是否超级管理员</param>
/// <param name="IsActive">是否启用</param>
/// <param name="CreatedAt">开通时间</param>
/// <param name="UpdatedAt">最近更新时间</param>
public sealed record AdminUserItem(long AdminId, string UserName, bool IsSuperAdmin, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
#endregion

#region 开通账号
/// <summary>
/// 开通管理员账号：契约 ID admin.createuser → POST /admin:createuser。
/// 管理账号的唯一入口（没有注册接口），由超级管理员操作；账号格式与密码强度规则与 auth.register 一致
/// </summary>
[Operation(AdminCreateUserOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = AdminSuperPolicy.PolicyName)]
public sealed class AdminCreateUserOperation(AuthDbContext db, IRepository<AdminUser, long> admins)
    : IOperation<AdminCreateUserRequest, AdminCreateUserResponse>
{
    public const string Id = "admin.createuser";

    public async ValueTask<Result<AdminCreateUserResponse>> HandleAsync(
        AdminCreateUserRequest request, InvocationContext context)
    {
        var cancellationToken = context.CancellationToken;
        var userName = request.UserName.Trim();

        // 字段格式校验（与 auth.register 同规则，保证两套账号同一强度）
        if (!Regex.IsMatch(userName, @"^[a-zA-Z0-9_]{4,20}$"))
        {
            return Result.Failure<AdminCreateUserResponse>(
                Error.Validation("ADMIN_VALIDATION_001", "admin.username_invalid")
                    .WithText("账号须为 4-20 位字母、数字或下划线"));
        }
        if (!Regex.IsMatch(request.Password, @"^(?=.*[A-Za-z])(?=.*\d).{8,32}$"))
        {
            return Result.Failure<AdminCreateUserResponse>(
                Error.Validation("ADMIN_VALIDATION_002", "admin.password_invalid")
                    .WithText("密码须为 8-32 位，且同时包含字母和数字"));
        }

        // 先查重给友好提示；并发下的唯一性仍由数据库唯一索引兜底
        var exists = await db.AdminUsers.AsNoTracking()
            .AnyAsync(u => u.UserName == userName, cancellationToken);
        if (exists)
        {
            return Result.Failure<AdminCreateUserResponse>(
                Error.Conflict("ADMIN_CONFLICT_001", "admin.account_exists").WithText("账号已存在"));
        }

        var now = DateTimeOffset.UtcNow;
        var admin = new AdminUser
        {
            UserName = userName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsSuperAdmin = request.IsSuperAdmin,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // 仓储写入在事务阶段的工作单元内执行，唯一索引冲突以失败结果返回
        var added = await admins.AddAsync(admin, cancellationToken);
        if (added.IsFailure)
        {
            return Result.Failure<AdminCreateUserResponse>(
                Error.Conflict("ADMIN_CONFLICT_001", "admin.account_exists").WithText("账号已存在"));
        }

        // 新管理员 ID 由框架工作单元提交后才生成；响应里的 AdminId 为占位 0，以 admin.users 列表为准
        return Result.Success(new AdminCreateUserResponse(0, userName, request.IsSuperAdmin));
    }
}

/// <summary>开通管理员账号请求</summary>
/// <param name="UserName">账号：4-20 位字母、数字或下划线</param>
/// <param name="Password">密码：8-32 位，须同时包含字母和数字</param>
/// <param name="IsSuperAdmin">是否授予超级管理员权限，默认 false</param>
public sealed record AdminCreateUserRequest(string UserName, string Password, bool IsSuperAdmin = false);

/// <summary>开通账号响应</summary>
/// <param name="AdminId">新管理员的数据库 ID（框架工作单元提交后才生成，此处为占位 0）</param>
/// <param name="UserName">账号</param>
/// <param name="IsSuperAdmin">是否超级管理员</param>
public sealed record AdminCreateUserResponse(long AdminId, string UserName, bool IsSuperAdmin);
#endregion

#region 重置密码
/// <summary>
/// 重置管理员密码：契约 ID admin.resetpassword → POST /admin:resetpassword。
/// 成功后立即吊销该管理员全部会话——拿到旧令牌的端必须重新登录，防「改密码不停用旧会话」的漏洞
/// </summary>
[Operation(AdminResetPasswordOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = AdminSuperPolicy.PolicyName)]
public sealed class AdminResetPasswordOperation(AuthDbContext db, SessionStore sessions)
    : IOperation<AdminResetPasswordRequest, AdminResetPasswordResponse>
{
    public const string Id = "admin.resetpassword";

    public async ValueTask<Result<AdminResetPasswordResponse>> HandleAsync(
        AdminResetPasswordRequest request, InvocationContext context)
    {
        if (!Regex.IsMatch(request.NewPassword, @"^(?=.*[A-Za-z])(?=.*\d).{8,32}$"))
        {
            return Result.Failure<AdminResetPasswordResponse>(
                Error.Validation("ADMIN_VALIDATION_002", "admin.password_invalid")
                    .WithText("密码须为 8-32 位，且同时包含字母和数字"));
        }

        var admin = await db.AdminUsers
            .FirstOrDefaultAsync(u => u.Id == request.AdminId, context.CancellationToken);
        if (admin is null)
        {
            return Result.Failure<AdminResetPasswordResponse>(
                Error.NotFound("ADMIN_NOTFOUND_001", "admin.account_not_found").WithText("管理员不存在"));
        }

        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        admin.UpdatedAt = DateTimeOffset.UtcNow;

        // 密码已变，旧会话全部作废（含该管理员的所有设备）
        await sessions.RevokeAllAsync(SessionScope.Admin, admin.Id.ToString(), context.CancellationToken);
        return Result.Success(new AdminResetPasswordResponse(true));
    }
}

/// <summary>重置管理员密码请求</summary>
/// <param name="AdminId">管理员 ID</param>
/// <param name="NewPassword">新密码：8-32 位，须同时包含字母和数字</param>
public sealed record AdminResetPasswordRequest(long AdminId, string NewPassword);

/// <summary>重置密码响应</summary>
/// <param name="Updated">是否已重置（该管理员全部会话已被吊销）</param>
public sealed record AdminResetPasswordResponse(bool Updated);
#endregion

#region 启用/停用
/// <summary>
/// 启用或停用管理员账号：契约 ID admin.setstatus → POST /admin:setstatus。
/// 停用后该账号不能再登录，已有会话立即吊销；不允许停用自己（防误操作把自己锁死）
/// </summary>
[Operation(AdminSetStatusOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = AdminSuperPolicy.PolicyName)]
public sealed class AdminSetStatusOperation(AuthDbContext db, SessionStore sessions)
    : IOperation<AdminSetStatusRequest, AdminSetStatusResponse>
{
    public const string Id = "admin.setstatus";

    public async ValueTask<Result<AdminSetStatusResponse>> HandleAsync(
        AdminSetStatusRequest request, InvocationContext context)
    {
        var principal = context.GetSecurity().Principal;

        var admin = await db.AdminUsers
            .FirstOrDefaultAsync(u => u.Id == request.AdminId, context.CancellationToken);
        if (admin is null)
        {
            return Result.Failure<AdminSetStatusResponse>(
                Error.NotFound("ADMIN_NOTFOUND_001", "admin.account_not_found").WithText("管理员不存在"));
        }

        // 不允许停用自己：超级管理员把自己停掉后没有任何人能再管理账号
        if (!request.IsActive && principal.Id == admin.Id.ToString())
        {
            return Result.Failure<AdminSetStatusResponse>(
                Error.Validation("ADMIN_VALIDATION_004", "admin.self_undisable")
                    .WithText("不能停用当前登录的账号"));
        }

        admin.IsActive = request.IsActive;
        admin.UpdatedAt = DateTimeOffset.UtcNow;

        // 停用即清场：旧会话立即失效，不等 7 天 TTL
        if (!request.IsActive)
        {
            await sessions.RevokeAllAsync(SessionScope.Admin, admin.Id.ToString(), context.CancellationToken);
        }

        return Result.Success(new AdminSetStatusResponse(admin.Id, request.IsActive));
    }
}

/// <summary>启用/停用管理员请求</summary>
/// <param name="AdminId">管理员 ID</param>
/// <param name="IsActive">true 启用 / false 停用（停用同时吊销其全部会话）</param>
public sealed record AdminSetStatusRequest(long AdminId, bool IsActive);

/// <summary>启用/停用响应</summary>
/// <param name="AdminId">管理员 ID</param>
/// <param name="IsActive">生效后的状态</param>
public sealed record AdminSetStatusResponse(long AdminId, bool IsActive);
#endregion

#region 删除账号
/// <summary>
/// 删除管理员账号：契约 ID admin.deleteuser → POST /admin:deleteuser（物理删除）。
/// 不允许删除自己；不允许删除最后一个超级管理员（保证系统始终有开通入口）；
/// 删除同时吊销该账号全部会话
/// </summary>
[Operation(AdminDeleteUserOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = AdminSuperPolicy.PolicyName)]
public sealed class AdminDeleteUserOperation(AuthDbContext db, SessionStore sessions)
    : IOperation<AdminDeleteUserRequest, AdminDeleteUserResponse>
{
    public const string Id = "admin.deleteuser";

    public async ValueTask<Result<AdminDeleteUserResponse>> HandleAsync(
        AdminDeleteUserRequest request, InvocationContext context)
    {
        var cancellationToken = context.CancellationToken;
        var principal = context.GetSecurity().Principal;

        var admin = await db.AdminUsers
            .FirstOrDefaultAsync(u => u.Id == request.AdminId, cancellationToken);
        if (admin is null)
        {
            return Result.Failure<AdminDeleteUserResponse>(
                Error.NotFound("ADMIN_NOTFOUND_001", "admin.account_not_found").WithText("管理员不存在"));
        }

        // 不允许删除自己（与停用同理由：防误操作锁死系统）
        if (principal.Id == admin.Id.ToString())
        {
            return Result.Failure<AdminDeleteUserResponse>(
                Error.Validation("ADMIN_VALIDATION_005", "admin.self_undelete")
                    .WithText("不能删除当前登录的账号"));
        }

        // 删除超级管理员前确认还有其他在任超管：保证开通/管理入口永不消失
        if (admin.IsSuperAdmin)
        {
            var otherSupers = await db.AdminUsers.AsNoTracking()
                .CountAsync(u => u.IsSuperAdmin && u.Id != admin.Id, cancellationToken);
            if (otherSupers == 0)
            {
                return Result.Failure<AdminDeleteUserResponse>(
                    Error.Validation("ADMIN_VALIDATION_006", "admin.last_super")
                        .WithText("不能删除最后一个超级管理员"));
            }
        }

        db.AdminUsers.Remove(admin);
        await sessions.RevokeAllAsync(SessionScope.Admin, admin.Id.ToString(), cancellationToken);
        return Result.Success(new AdminDeleteUserResponse(true));
    }
}

/// <summary>删除管理员请求</summary>
/// <param name="AdminId">管理员 ID</param>
public sealed record AdminDeleteUserRequest(long AdminId);

/// <summary>删除账号响应</summary>
/// <param name="Deleted">是否已删除（该管理员全部会话已被吊销）</param>
public sealed record AdminDeleteUserResponse(bool Deleted);
#endregion
