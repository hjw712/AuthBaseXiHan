using AuthBaseXiHan.Sessions.Ai;
using XiHan.Framework.Kernel;
using XiHan.Framework.Kernel.Hosting;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;
using XiHan.Framework.Primitives.Manifests;

namespace AuthBaseXiHan.Features;

/// <summary>
/// AI 特性：模型凭据管理、动态客户端工厂与模型管理策略的装配单元。
/// </summary>
/// <remarks>
/// 依赖宿主能力的顺序由 Manifest 声明；Configure 里只做两件事：
/// ① 登记配置相关服务（工厂 + 策略）；
/// ② 登记本模块的操作（源生成器会扫描全部 [Operation]）。
/// 具体业务操作就近放在 Operations 目录，与 AuthFeature 风格保持一致。
/// </remarks>
public sealed class AiFeature : IFeature
{
    public Manifest Manifest { get; } = new()
    {
        Id = "xihan.ai.llm",
        Package = "AuthBaseXiHan",
        Version = "1.0.0",
        Primitives = 1,
        Stability = Stability.Experimental,
        Kind = ManifestKind.Capability,
        Requires = [new CapabilityRequirement(KernelCapabilities.Hosting, KernelCapabilities.Version)],
        Entry = typeof(AiFeature).FullName,
    };

    public void Configure(FeatureContext context)
    {
        // 动态客户端工厂：配置从数据库读、Key 加密解密、结果缓存进程内存
        context.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        // 模型管理策略：容器注册后 PolicyCatalog 按 Name 自动发现
        context.Services.AddSingleton<IPolicy, LlmAdminPolicy>();

        // 注意：不调用 AddGeneratedOperations()。
        // 该源生成方法登记的是「本程序集全部 [Operation]」，AuthFeature 已经调用过一次；
        // 再调用会导致所有操作重复登记，启动时报 kernel.operation.duplicate。
        // AI 模块的操作由 AuthFeature 的那次调用统一登记，归属不影响行为。
    }
}
