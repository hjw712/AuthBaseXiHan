using AuthBaseXiHan.Sessions.Admin;
using XiHan.Framework.Kernel;
using XiHan.Framework.Kernel.Hosting;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;
using XiHan.Framework.Primitives.Manifests;

namespace AuthBaseXiHan.Features;

/// <summary>
/// 管理员特性：管理账号（独立表，无注册入口）的策略与门禁装配单元。
/// </summary>
/// <remarks>
/// 依赖宿主能力的顺序由 Manifest 声明；Configure 里登记两级管理策略：
/// - <see cref="AdminPolicy"/>（admin）：管理员登录后的常规操作；
/// - <see cref="AdminSuperPolicy"/>（admin.super）：开通/重置/停用/删除管理账号。
/// 超管引导种子（AdminBootstrap.SeedAsync）由 Program.cs 在表自愈之后调用。
/// 注意：不调用 AddGeneratedOperations()——该源生成方法登记的是「本程序集全部 [Operation]」，
/// AuthFeature 已调用过一次，重复调用会启动报 kernel.operation.duplicate。
/// </remarks>
public sealed class AdminFeature : IFeature
{
    public Manifest Manifest { get; } = new()
    {
        Id = "xihan.admin.users",
        Package = "AuthBaseXiHan",
        Version = "1.0.0",
        Primitives = 1,
        Stability = Stability.Experimental,
        Kind = ManifestKind.Capability,
        Requires = [new CapabilityRequirement(KernelCapabilities.Hosting, KernelCapabilities.Version)],
        Entry = typeof(AdminFeature).FullName,
    };

    public void Configure(FeatureContext context)
    {
        // 管理门禁策略：容器注册后 PolicyCatalog 按 Name 自动发现，契约 Policy 属性按名引用
        context.Services.AddSingleton<IPolicy, AdminPolicy>();
        context.Services.AddSingleton<IPolicy, AdminSuperPolicy>();
    }
}
