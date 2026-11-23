using AuthBaseXiHan.Operations;
using XiHan.Framework.Kernel;
using XiHan.Framework.Kernel.Hosting;
using XiHan.Framework.Primitives;
using XiHan.Framework.Primitives.Manifests;

namespace AuthBaseXiHan;

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

    public void Configure(FeatureContext context) => context.AddGeneratedOperations();
}
