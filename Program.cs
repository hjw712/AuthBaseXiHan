using AuthBaseXiHan;
using AuthBaseXiHan.Captcha;
using AuthBaseXiHan.Operations;
using AuthBaseXiHan.Persistence;
using AuthBaseXiHan.Sessions;
using Microsoft.EntityFrameworkCore;
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

var builder = WebApplication.CreateBuilder(args);

// 会话双轨（JWT 验签 + Redis 吊销）：
// ① 会话存储与门禁处理器——门禁装饰框架的 JwtAuthenticationHandler（验签后查 Redis 会话），
//    必须先于 AddXiHan 登记：内核 Authenticate 阶段按方案名取「第一个」登记的处理器，
//    DI 顺序决定门禁在外层、框架原始处理器成为内层；
//    框架处理器按具体类型显式注册一次，供装饰器注入（接口登记由 AddXiHan 完成）
builder.Services.AddSingleton<JwtAuthenticationHandler>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<CaptchaStore>();
builder.Services.AddSingleton<IAuthenticationHandler, SessionGateHandler>();
// ② 自定义类型进缓存序列化器：会话索引 string[] 的源生成元数据交给缓存序列化链
CacheSerializerRegistration.AddTypeInfo(builder.Services, AppJsonContext.Default);

// 登记实体：持久化 Provider 据此注册闭合泛型仓储 IRepository<User, long>
// 必须在 AddXiHan 之前或特性的 Configure 里调用
builder.Services.AddRepository<User, long>();

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
    .UseFeature<AuthFeature>());

var app = builder.Build();

// 静态文件（Scalar 文档查看器页面）不经内核管道，必须放在 UseXiHan 之前
app.UseStaticFiles();

// JSON 响应中文直出：框架 rc.6 把非 ASCII 写成 \uXXXX 转义（合法 JSON 但调试视图不可读），
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
