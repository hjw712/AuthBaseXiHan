using AuthBaseXiHan;
using AuthBaseXiHan.Features;
using AuthBaseXiHan.Operations;
using AuthBaseXiHan.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using XiHan.Framework.Caching.Abstractions.Serialization;
using XiHan.Framework.Caching.Redis;
using XiHan.Framework.Hosting.AspNetCore;
using XiHan.Framework.Security.Abstractions.Authentication;
using XiHan.Framework.Security.InMemory;
using XiHan.Framework.Security.Jwt;
using XiHan.Framework.Localization.Abstractions;
using XiHan.Framework.Localization.Abstractions.Messages;
using XiHan.Framework.Persistence.Abstractions.Repositories;
using XiHan.Framework.Persistence.EntityFrameworkCore;
using XiHan.Framework.Profiles.Minimal;
using XiHan.Framework.Protocols.Http;
using XiHan.Framework.Protocols.Http.OpenApi;
using XiHan.Framework.Protocols.Http.Routing;
using AuthBaseXiHan.Sessions.Admin;
using AuthBaseXiHan.Sessions.Ai;
using AuthBaseXiHan.Sessions.Captcha;
using AuthBaseXiHan.Sessions.Common;
using AuthBaseXiHan.Sessions.Jwt;
using AuthBaseXiHan.Sessions.Session;

var builder = WebApplication.CreateBuilder(args);

// 数据保护：为模型 API Key 加密提供密钥环。
// 持久化到 Redis（本机已在用），多实例部署时彼此能解同一份密文；
// 应用名隔离不同应用的密钥，避免同 Redis 下互相干扰。
var dpRedis = ConnectionMultiplexer.Connect(
    builder.Configuration["XiHan:Caching:Redis:Configuration"] ?? "127.0.0.1:6379,abortConnect=false");
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(dpRedis, "authbase:dpapi-keys")
    .SetApplicationName("AuthBaseXiHan");

// 会话双轨（JWT 验签 + Redis 吊销）：
// ① 会话存储与门禁处理器——门禁装饰框架的 JwtAuthenticationHandler（验签后查 Redis 会话），
//    必须先于 AddXiHan 登记：内核 Authenticate 阶段按方案名取「第一个」登记的处理器，
//    DI 顺序决定门禁在外层、框架原始处理器成为内层；
//    框架处理器按具体类型显式注册一次，供装饰器注入（接口登记由 AddXiHan 完成）
builder.Services.AddSingleton<JwtAuthenticationHandler>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<CaptchaStore>();
// 通用凭据登录流：auth.login 与 admin.login 共享（验证码 + 防时序比对 + 会话登记）
builder.Services.AddSingleton<CredentialAuthenticator>();
builder.Services.AddSingleton<IAuthenticationHandler, SessionGateHandler>();
// ② 自定义类型进缓存序列化器：会话索引 string[] 的源生成元数据交给缓存序列化链
CacheSerializerRegistration.AddTypeInfo(builder.Services, AppJsonContext.Default);

// 登记实体：持久化 Provider 据此注册闭合泛型仓储 IRepository<User, long> 等
// 必须在 AddXiHan 之前或特性的 Configure 里调用
builder.Services.AddRepository<User, long>();
builder.Services.AddRepository<AdminUser, long>();
builder.Services.AddRepository<PromptConfig, long>();

// 分页配置：列表接口的默认页大小与上限（Operations/Paging.cs 的 PagingOptions，绑定 "Paging" 配置节）
builder.Services.Configure<PagingOptions>(builder.Configuration.GetSection(PagingOptions.SectionName));

// AI 对话运行参数：单次调用超时等（Operations/AiChatOptions.cs，绑定 "Ai" 配置节，与管理员名单同节）
builder.Services.Configure<AiChatOptions>(builder.Configuration.GetSection(AiChatOptions.SectionName));

// JWT 签发服务（Security.Jwt 只校验不签发，签发由应用自己完成）
builder.Services.AddSingleton<JwtTokenIssuer>();

// 消息目录：装载 Localization/<区域>.json（嵌入资源）
// 接口文档 summary/description、错误文案按 Accept-Language 从这里翻译
builder.Services.AddMessageCatalog(MessageCatalog.FromEmbeddedResources("authbase", typeof(Program)));

// 把特性交给内核：求解能力图谱，按拓扑顺序调用各特性的 Configure
builder.AddXiHan(xihan => xihan
    .UseMinimalProfile()
    .UseFeature<HttpProtocolFeature>()
    // EF Core 持久化 Provider：连接串取配置 XiHan:Bindings:db
    .UseFeature(new EntityFrameworkCorePersistenceFeature<AuthDbContext>(
        static (binding, options) =>
        {
            var connectionString = binding.ValueOrThrow().Value;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("缺少连接串：请在 appsettings 配置 XiHan:Bindings:db");
            }

            options.UseSqlServer(connectionString);
        }))
    // Redis 缓存 Provider：提供 caching.distributed（ISharedCache / ICacheStore / 分布式锁 / 幂等）
    // 连接串取配置 XiHan:Caching:Redis:Configuration
    .UseFeature<RedisCachingFeature>()
    // JWT 认证 Provider：校验 HS256 Bearer 令牌（XiHan:Security:Jwt 配置），顶替内存实现的认证部分
    .UseFeature<JwtSecurityFeature>()
    // 内存授予存储：装配 JWT 后它让出认证、只保留 RBAC 授权（security.authorization@1）
    .UseFeature<InMemorySecurityFeature>()
    // 框架 AI 子系统：OpenAIFeature 提供 Kind="openai" 的客户端工厂（真正建 IChatClient 的地方），
    // AIToolsFeature 提供 IAIChat 依赖的工具目录。模型凭据仍从数据库读——
    // 本项目的 IAIProviderCatalog 实现（DbAIProviderCatalog）在 AddXiHan 之后覆盖框架的配置驱动目录。
    .UseFeature<XiHan.Framework.AI.OpenAI.OpenAIFeature>()
    .UseFeature<XiHan.Framework.AI.Abstractions.AIToolsFeature>()
    .UseFeature<AuthFeature>()
    .UseFeature<AdminFeature>()
    .UseFeature<AiFeature>());

// 覆盖框架 AI 的默认 Provider 目录：框架在 AddXiHan 期间把 AIProviderCatalog（读 XiHan:AI:Providers 配置）
// 登记为 IAIProviderCatalog；本项目要「多租户 + Key 加密入库 + 运行期改配置立即生效」，
// 改用 DbAIProviderCatalog（数据来源是 LlmProviderConfig 表）。
// 必须放在 AddXiHan 之后、Build 之前：同一服务类型后登记的胜出，放前面会被框架的登记顶掉。
builder.Services.AddSingleton<XiHan.Framework.AI.Abstractions.Providers.IAIProviderCatalog>(
    sp => sp.GetRequiredService<DbAIProviderCatalog>());

var app = builder.Build();

// 表自愈：EnsureCreated 只对全新库建表，已有库新增实体（如 AI 模块）需要补齐。
// 在内核管道启动前执行，确保后续请求访问新表时不会报「对象名无效」。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await DbInitializer.EnsureTablesAsync(db);

    // 超管引导：管理账号没有注册接口，第一个超级管理员从 Admin:Bootstrap 配置落地
    //（账号已存在时什么都不做，运行期改动以管理接口为准）
    await AdminBootstrap.SeedAsync(db, app.Services.GetRequiredService<IConfiguration>());

    // 预热模型目录：IAIProviderCatalog 的方法都是同步的、不能查库，故启动时把这批配置
    // （含解密后的 Key）装进内存快照；此后由管理操作增量维护、并按 10 分钟有效期惰性重载。
    var catalog = app.Services.GetRequiredService<DbAIProviderCatalog>();
    await catalog.ReloadAsync(db, CancellationToken.None);

    // 诊断：确认框架 AI 子系统已装配，且目录用的是本项目的数据库实现而不是框架的配置驱动实现
    var aiChat = scope.ServiceProvider.GetService<XiHan.Framework.AI.Abstractions.Chat.IAIChat>();
    var activeCatalog = scope.ServiceProvider.GetService<XiHan.Framework.AI.Abstractions.Providers.IAIProviderCatalog>();
    app.Logger.LogInformation("AI 子系统：IAIChat={Chat}，IAIProviderCatalog={Catalog}，Provider 数={Count}",
        aiChat?.GetType().Name ?? "<未注册>",
        activeCatalog?.GetType().Name ?? "<未注册>",
        catalog.Providers.Count);
}

// 静态文件（Scalar 文档查看器页面）不经内核管道，必须放在 UseXiHan 之前
app.UseStaticFiles();

// JSON 响应中文直出：框架（rc.6 起，rc.7 仍如此）把非 ASCII 写成 \uXXXX 转义（合法 JSON 但调试视图不可读），
// 在响应边缘解析重写为 UTF-8 明文；须在 UseXiHan 之前挂上才能包住内核管道写出的响应
app.UseMiddleware<ReadableJsonMiddleware>();

app.UseXiHan();            // HTTP 请求进入内核管道
app.MapXiHanHealth();      // /health/startup、/health/ready、/health/live
app.MapXiHanHealthDetails("/health/details"); // 各特性与健康条目明细，排查降级原因用
app.MapXiHanOperations();  // 按操作登记表映射路由：POST /auth:register、POST /auth:login

// 发布 OpenAPI 3.1 文档：GET /openapi.json
// 与 MapXiHanOperations 同一操作登记表 + 编译期 JSON Schema，契约改了文档自动跟着变
app.MapXiHanOpenApi(new OpenApiOptions
{
    Title = "AuthBase 通用基座 API",
    Version = "v1",
});

app.Run();
