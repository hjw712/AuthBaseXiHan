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
        // Key 加解密与掩码：全应用唯一处理明文 Key 的地方
        context.Services.AddSingleton<LlmKeyProtector>();

        // 数据库驱动的模型目录（框架 IAIProviderCatalog 的实现）：
        // 决定「用哪些 Provider、凭据是什么」，真正建 IChatClient 交给框架的 Kind 工厂。
        // 注意：作为 IAIProviderCatalog 的注册放在 Program.cs 里 AddXiHan 之后，
        // 以免被框架 AI 特性登记默认（配置驱动）目录覆盖。
        context.Services.AddSingleton<DbAIProviderCatalog>();

        // 每用户配额：进程内计数，必须单例（状态就是计数桶本身）
        context.Services.AddSingleton<AiUserRateLimiter>();

        // 调用日志写入器：自己开作用域提交，与操作的工作单元成败解耦，故为单例
        context.Services.AddSingleton<AiCallLogger>();

        // 模型管理策略：容器注册后 PolicyCatalog 按 Name 自动发现
        context.Services.AddSingleton<IPolicy, LlmAdminPolicy>();

        // 注意：不调用 AddGeneratedOperations()。
        // 该源生成方法登记的是「本程序集全部 [Operation]」，AuthFeature 已经调用过一次；
        // 再调用会导致所有操作重复登记，启动时报 kernel.operation.duplicate。
        // AI 模块的操作由 AuthFeature 的那次调用统一登记，归属不影响行为。
    }
}
