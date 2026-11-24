using AuthBaseXiHan.Operations;
using XiHan.Framework.Kernel;
using XiHan.Framework.Kernel.Hosting;
using XiHan.Framework.Primitives;
using XiHan.Framework.Primitives.Manifests;

namespace AuthBaseXiHan.Features;

/// <summary>
/// 认证特性：内核装配的最小单元。
/// 清单声明依赖（宿主能力），Configure 登记操作与其 JSON Schema
/// </summary>
public sealed class AuthFeature : IFeature
{
    public Manifest Manifest { get; } = new()
    {
        Id = "xihan.auth.users",
        Package = "AuthBaseXiHan",
        Version = "1.0.0",
        Primitives = 1,
        Stability = Stability.Experimental,
        Kind = ManifestKind.Capability,
        Requires = [new CapabilityRequirement(KernelCapabilities.Hosting, KernelCapabilities.Version)],
        Entry = typeof(AuthFeature).FullName,
    };

    public void Configure(FeatureContext context)
    {
        // whoami / logout / logout-all 为单接口双轨通用（挂 authenticated 策略，
        // 轨道从 token_kind claim 推导），本特性无需注册任何会话策略
        context.AddGeneratedOperations();
    }
}
