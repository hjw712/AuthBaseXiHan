# AuthBaseXiHan —— XiHan.Framework 版通用基座

> 基于 [XiHan.Framework](https://github.com/XiHanFun/XiHan.Framework) 5.0.0-rc.6 的注册/登录基座。
> 旧版原生 ASP.NET Core 实现在 `../src/`（保留作对照，功能相同）。

## 项目结构（对照官方 Quickstart）

```
src-xihan/AuthBaseXiHan/
├── Program.cs                    # 入口：AddRepository + 装配 Minimal Profile / HTTP 协议 / EF Core 持久化 / Redis 缓存 / AuthFeature
├── Operations/
│   └── AuthOperations.cs         # 六个操作：注册/图形验证码/登录/查询当前身份/登出/强制下线 + 各自的请求/响应 record
├── Sessions/
│   ├── JwtTokenIssuer.cs         # JWT 签发（HS256，与 Security.Jwt 同一密钥；框架只校验不签发）
│   ├── SessionStore.cs           # Redis 会话白名单：jti 登记 / 吊销 / 强制下线
│   └── SessionGateHandler.cs     # 会话门禁：验签通过后再查 Redis 会话存在才放行
├── Persistence/
│   ├── User.cs                   # 用户实体（IEntity<long> + [EntityIndex] 唯一索引声明）
│   └── AuthDbContext.cs          # 应用自己的 DbContext + ApplyPersistenceConventions
│                                  # （不用迁移：EnsureCreated 启动时自动建表）
├── AppJsonContext.cs             # 源生成序列化上下文（无反射内核，类型必须登记，漏登编译报 XHG002）
└── AuthFeature.cs                # 特性清单：xihan.auth.users，依赖宿主能力
```

无需手写 Controller 和路由——操作契约 `[Operation("auth.register")]` 由源生成器自动登记，
HTTP 协议适配器自动映射路由（**资源与动词用冒号分隔**：`POST /auth:register`、`POST /auth:login`）。

## 运行

**必须先启动 Redis，再启动应用**——这是硬性前提，不是可选项。框架的 `RedisCachingFeature.StartAsync` 会显式检查连接状态，Redis 没连上就直接抛 `Redis is not connected` 让整个应用启动失败。连接串里的 `abortConnect=false` **只能避免首次连接抛 `RedisConnectionException`，救不了这个检查**（实测：Redis 断线时即使带 `abortConnect=false`，应用仍 500.30 崩溃）：

```bash
cd D:/Redis-8.10.3 && ./redis-server.exe redis.conf    # 本机 Redis（127.0.0.1:6379，<你的 Redis 密码>，注意用相对路径）
```

> **开机自启（推荐）**：本机 Redis 未注册成服务，重启机器后需手动执行上面命令。可用目录内自带的 `RedisService.exe` 注册为 Windows 服务一劳永逸（需管理员）：`RedisService.exe install` 后设为自动启动。

然后：

```bash
cd src-xihan/AuthBaseXiHan
dotnet run --urls http://localhost:5300
```

- 健康检查：`/health/startup`、`/health/ready`、`/health/live`
- 健康明细：`/health/details`（各特性状态与健康条目）
- 接口文档（OpenAPI 3.1）：`http://localhost:5300/docs/index.html`（Scalar 可视化查看器）
- 原始文档：`GET /openapi.json`，按 `Accept-Language` 返回中/英文版本，可直接喂给 NSwag/Kiota 生成客户端代码

## 接口文档（OpenAPI）

`MapXiHanOpenApi()` 与 `MapXiHanOperations()` 读同一份操作登记表和编译期 JSON Schema，契约改了文档自动跟着变，不会脱节。三个组成部分：

| 组成 | 位置 | 说明 |
|---|---|---|
| 文档端点 | `Program.cs` 的 `MapXiHanOpenApi()` | 发布 `/openapi.json`，OpenAPI 3.1，带 `x-xihan-stability` 等扩展 |
| 字段说明 | DTO 的 XML 注释（`<param>`） | csproj 开 `GenerateDocumentationFile`，Kernel.Generators 折进 Schema `description` |
| 接口文案 | `Localization/zh-CN.json` / `en-US.json` | 键 `<操作ID>.title` / `.description`；经 `AddMessageCatalog` 登记嵌入资源，按 `Accept-Language` 协商 |

查看器是放在 `wwwroot/docs/index.html` 的静态 Scalar 页面（`UseStaticFiles` 必须在 `UseXiHan` 之前，页面不经内核管道）。换 Swagger UI/Redoc 只需改该页面引用的脚本，文档地址不变。

注意：文档在**第一次请求时生成并按区域缓存**，改了消息目录或 DTO 注释后要重启应用才能看到新文案。

### 错误返回的文档说明

接口文档里 `default` 响应（ProblemDetails）的**字段级说明暂不可配置**：rc.6 的 `WriteProblemDetails` 是硬编码写出的，不走 `openapi.schemas.*` 消息目录覆盖（覆盖只对操作契约自己的 Schema 生效，`Localization` 里已预留 problem_details 的覆盖键，上游修复后自动生效）。

当前做法：把错误契约写进每个操作的 `description`（消息目录的 `<操作ID>.description`），Scalar 接口页展开即可看到——包含 ProblemDetails 各字段含义（code/status/title/detail/errors/details/traceId/instance）与该操作可能返回的错误码清单（如 `AUTH_CONFLICT_001` 409）。新增操作时按同样格式补文案。

## 数据库（SQL Server + EF Core Provider）

连接串配置在 `appsettings.json` 的 `XiHan:Bindings:db`（框架的连接绑定，不是 `ConnectionStrings`）：

```json
{
  "XiHan": {
    "Bindings": {
      "db": "Server=localhost;Database=AuthBaseXiHanDb;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False"
    },
    "Persistence": { "EntityFrameworkCore": { "EnsureCreated": true } }
  }
}
```

**不用 EF Core 迁移**：`EnsureCreated: true`，应用启动时自动建库建表（含 `[EntityIndex]` 声明的唯一索引）。
已删除 `Persistence/Migrations/` 与设计时工厂，`Microsoft.EntityFrameworkCore.Design` 包也一并移除。
空库场景已实测验证：删掉数据库后启动应用，表结构与唯一索引自动创建，接口全通。

注意：`EnsureCreated` 不做增量变更——**改了实体模型后需要手动删库重建**（框架文档明确它只用于开发/测试；将来上生产再切回迁移）。

### 数据访问方式

- **写**：注入 `IRepository<User, long>`，`AddAsync` 在事务阶段的工作单元内执行；
  唯一索引冲突以失败 `Result` 返回（不抛异常），已映射为 `AUTH_CONFLICT_001`
- **读**：直接注入 `AuthDbContext` 用 LINQ 查询（框架文档推荐做法，与仓储共享同一实例与事务）
- 仓储要求**活动的工作单元**：EF 特性已把 `UnitOfWorkMiddleware` 挂到管道 `transaction` 阶段，不要绕过管道直接调仓储

## 缓存（Redis + Caching.Redis Provider）

装配 `XiHan.Framework.Caching.Redis` 后，框架的 `caching.distributed` 由 Redis 承担：
`ISharedCache`、`ICacheStore`、`IDistributedLock`、`IDistributedCache`、幂等中间件全部走 Redis。
登录令牌已改为 JWT（Redis 不再存会话），Redis 保留给幂等、分布式锁、缓存等框架能力使用。

配置：

```json
{
  "XiHan": {
    "Caching": {
      "Redis": {
        "Configuration": "127.0.0.1:6379,<你的 Redis 密码>,abortConnect=false",
        "KeyPrefix": "authbase:",
        "DefaultTimeToLiveSeconds": 604800
      }
    }
  }
}
```

- `Configuration` 是 StackExchange.Redis 连接串；也可用绑定 `XiHan:Bindings:cache` 覆盖。**建议带 `abortConnect=false`**：避免 Redis 短暂抖动时 `ConnectionMultiplexer.Connect` 直接抛 `RedisConnectionException`；但它**不能**替代"先起 Redis"——框架 `StartAsync` 仍检查连接状态，Redis 离线照样启动失败（`Redis is not connected`）。它是韧性补充，不是免死金牌
- `KeyPrefix` 隔离不同应用的键（键实际形态：`authbase:auth:session:<token>`）
- `DefaultTimeToLiveSeconds` 是未指定过期时的默认存活时间；会话令牌显式设了 7 天（604800 秒）绝对过期

用法：

```csharp
// 写入：绝对过期 7 天
await cache.SetAsync(new CacheKey("auth:session:" + token), true,
    CacheEntryOptions.Expires(TimeSpan.FromSeconds(604800)), ct);

// 读取：未命中为 Maybe 无值
var entry = await cache.GetAsync<bool>(new CacheKey("auth:session:" + token), ct);
```

验证方式（本机）：

```bash
D:/Redis-8.10.3/redis-cli.exe --scan --pattern "authbase:*"          # 看键
D:/Redis-8.10.3/redis-cli.exe ttl "authbase:auth:session:<token>"    # 看剩余秒数
```

注意：

- **应用启动强依赖 Redis 在线**：`RedisCachingFeature.StartAsync` 检查连接状态，Redis 没起来就抛 `Redis is not connected` 直接启动失败。`abortConnect=false` 只解决 Connect 阶段的 `RedisConnectionException`，救不了这个检查——**必须先起 Redis 再起 API**
- IIS 场景的启动顺序陷阱：应用被 IIS 拉起时若 Redis 还没就绪就会失败并进入快速失败状态，即便 Redis 后来起来也不会自愈，需回收应用池才恢复（非管理员无法 `iisreset`，可改 `web.config` 内容触发 IIS 自动回收）
- 值前有 1 字节头（`0` 绝对过期 / `1` 滑动过期），所以 redis-cli 看到的是 `"\x00true"`，正常

## 接口契约

错误按 RFC 9457 Problem Details 返回（`type/title/status/detail/code/traceId`）。

| 场景 | 结果 |
|---|---|
| `GET /auth:captcha` | `200 {"captchaId":"…","image":"data:image/svg+xml;base64,…","expiresIn":120}`（SVG 验证码，前端直接 `<img src>`） |
| `POST /auth:register` 成功 | `200 {"userId":1}` |
| 重复注册 | `409` code=`AUTH_CONFLICT_001`「账号已存在」 |
| `POST /auth:login` 成功 | `200 {"token":"<JWT>","expiresIn":604800}` |
| 验证码错误/过期/重放 | `400` code=`AUTH_VALIDATION_003`「验证码错误或已过期」 |
| 账号/密码错误 | `403` code=`AUTH_DENIED_001`（不区分具体原因，防账号枚举，恒定一次 BCrypt 耗时防时序枚举） |
| 账号格式非法 | `400` code=`AUTH_VALIDATION_001`（显式校验） |
| 缺必填字段 | `400` code=`KERNEL_VALIDATION_003`（框架按 JSON Schema 自动拒绝） |
| `GET /auth:whoami` 带有效 `Authorization: Bearer <JWT>` | `200 {"isAuthenticated":true,"id":"1","scheme":"Bearer","displayName":"…"}` |
| `GET /auth:whoami` 不带令牌 | `401` code=`SECURITY_DENIED_001`，带 `WWW-Authenticate: Bearer` |
| `GET /auth:whoami` 令牌伪造/过期/签名不符 | `401` code=`SECURITY_DENIED_003`（不区分原因，细节只进日志） |

校验规则与旧版一致：账号 4-20 位字母/数字/下划线；密码 8-32 位含字母数字；BCrypt 加盐哈希。

**登录流程（须先取验证码）**：`GET /auth:captcha` 取 `captchaId` 与图像 → 用户输入 4 位字符 → `POST /auth:login` 带 `captchaId + captchaCode + userName + password`。验证码存 Redis（键 `auth:captchas:{id}`，TTL 2 分钟），**读后即删一次有效**（对错都作废，防重试爆破）；比对忽略大小写；字符集去掉了 0/O、1/I/l 等易混字符。验证码为 SVG 纯字符串生成（`Captcha/CaptchaStore.cs`），无 System.Drawing 原生依赖，AOT 安全。

## 认证（JWT + Redis 会话双轨）

登录签发 HS256 JWT（三段式 `header.payload.signature`），后续请求带 `Authorization: Bearer <token>`。
**双轨设计：JWT 负责验签（无状态），Redis 负责吊销（有状态）**——纯 JWT 无法主动吊销，
会话双轨让登出/强制下线立即生效：令牌验签仍通过，但 Redis 里查不到会话即 401。

| 环节 | 实现 |
|---|---|
| 签发 | `Sessions/JwtTokenIssuer.cs`（HS256，`sub`=用户 ID、`name`=账号、`jti`=会话 ID（随机 GUID）、7 天过期，iss/aud 与配置一致） |
| 会话登记 | 登录时 `Sessions/SessionStore.SaveAsync` 写 Redis：`auth:sessions:{jti}` → 用户 ID，TTL 与令牌一致（7 天）；另维护 `auth:sessions:user:{userId}` → jti 列表（强制下线索引用） |
| 验签 | `JwtSecurityFeature`（内核管道 Authenticate 阶段，`XiHan:Security:Jwt` 配置） |
| 会话门禁 | `Sessions/SessionGateHandler` 装饰框架 JWT 处理器：验签通过后查 Redis 会话存在才放行；不存在即 401（`SECURITY_DENIED_003`，带 WWW-Authenticate） |
| 授权 | `InMemorySecurityFeature`（装配 JWT 后让出认证、只保留 RBAC 授权；`Policy="authenticated"` 由它判定） |

会话相关接口：

| 接口 | 说明 |
|---|---|
| `POST /auth:logout` | 登出：吊销当前令牌的会话（请求体 `{}`），调用后当前令牌立即失效 |
| `POST /auth:logout-all` | 强制下线：吊销该账号在所有设备的会话，返回吊销数（改密码/疑似被盗时用） |

配置（`appsettings.json`）：

```json
{
  "XiHan": {
    "Security": {
      "Jwt": {
        "Secret": "<至少 32 字节的对称密钥>",
        "Issuer": "authbase",
        "Audience": "authbase-api",
        "ClockSkewSeconds": 60
      }
    }
  }
}
```

要点：

- **生产环境把 `Secret` 换成新随机密钥**（当前是开发用密钥），放用户机密/环境变量，不进仓库
- 校验强制项：签名、`exp`、有效期；`Issuer` / `Audience` 配了就校验；主体 ID 取 `sub`（`NameClaim` 缺省 `sub`）
- 401 统一 `SECURITY_DENIED_001`（未认证）/ `SECURITY_DENIED_003`（凭据无效，含会话已吊销），不区分原因对外暴露
- 会话 TTL 与令牌同步（都 7 天）：令牌过期会话键自动消失，无需清理任务
- 每个受保护请求多一次 Redis 读（亚毫秒级）换立即吊销能力；Redis 挂了会话门禁不可用（应用启动本来就要求 Redis 在线）
- 旧格式令牌（无 `jti`，切换双轨前签发的）一律拒绝，客户端重新登录即可
- OpenAPI 文档自动发布 `components.securitySchemes.bearer` 与全局 `security`，Scalar 有「Authorize」按钮
- `GET /auth:whoami` 是受保护接口示例（`ReadOnly=true, Policy="authenticated"`），新受保护操作照此声明

## 部署：卷影复制与无感更新

IIS 上运行的 ASP.NET Core 应用会**锁定自己的 DLL**，导致发布时覆盖不了、报「文件被进程占用」。
ANCM（ASP.NET Core Module v2）的**应用卷影复制（Shadow Copy）**让 IIS 先把程序集复制到独立目录再加载，
源目录的 DLL 因此不再被占用，可以随时覆盖——这就是「无感更新」的核心。

> **概念区分（别走弯路）**：Windows 的 `vssadmin` / 卷影复制服务（VSS）是**磁盘备份快照**技术，
> 跟 DLL 解锁无关，且必须管理员权限，不用它。这里用的是 **ANCM 的应用级卷影复制**，
> 只需在 `web.config` 配属性 + 给目录授权，无需系统级快照。

### 工作原理与边界

- ANCM 启动时把站点程序集复制到 `shadowCopyDirectory`，从副本运行；源目录 DLL 解锁
- 检测到 web.config / DLL 变化后，ANCM 自动复制到新目录并回收应用，加载新版本
- **卷影复制 ≠ 零停机**：微软明确说明它仍会回收应用，**瞬间可能有 503**；
  本应用会话在 Redis、数据用 `EnsureCreated`，重启不丢登录态和数据，体感接近无感。
  要严格零停机需蓝绿部署 / 部署槽（两个站点目录 + ARR 或负载均衡切换）

### 配置（web.config）

项目根的 `web.config` 已预置以下节点，`dotnet publish` 会以此为模板合并（保留这些设置）：

```xml
<aspNetCore processPath="dotnet" arguments=".\AuthBaseXiHan.dll"
            stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout"
            hostingModel="inprocess">
  <handlerSettings>
    <handlerSetting name="enableShadowCopy" value="true" />
    <handlerSetting name="shadowCopyDirectory" value="..\AuthBaseXiHan_ShadowCopy\" />
    <handlerSetting name="cleanShadowCopyDirectory" value="true" />
  </handlerSettings>
</aspNetCore>
```

**注意写法**：三个键是 `<handlerSettings>` 下的 `<handlerSetting name="..." value="..." />` **子元素**，
**不是** `<aspNetCore>` 的 XML 属性。写成属性 ANCM 不会识别，卷影复制静默失效（本机曾踩过这个坑）。

| handlerSetting name | 作用 |
|---|---|
| `enableShadowCopy` | `true` 开启卷影复制 |
| `shadowCopyDirectory` | 副本目录，用 `..` 放到**站点目录之外**，避免发布时被同步/删除；应用池身份需有「修改」权限 |
| `cleanShadowCopyDirectory` | 启动时先清理再复制，避免升级 .NET 后残留旧程序集导致 `500.30`（默认 `false`，建议开） |

**版本要求**：属性名取决于服务器 **Hosting Bundle** 版本（与本项目的 net10.0 无关）：
`>= 7.0` 用 `enableShadowCopy`（本机 10.x/11.x 适用）；`6.0` 用 `experimentalEnableShadowCopy`。

### 部署步骤（首次需管理员）

`deploy-shadowcopy.ps1` 完成三件事：创建副本目录 → 给**应用程序池身份**授予该目录「修改」权限（读写删，缺了会 500）→ 发布并校验 web.config。

```powershell
# 以管理员身份运行 PowerShell，在項目根目录执行
cd D:\个人项目\admin-template\src-xihan\AuthBaseXiHan
.\deploy-shadowcopy.ps1 -AppPoolName "<你的应用池名>"     # 不传则尝试自动探测，失败回退 AuthBaseXiHan
```

- 副本目录默认 `<发布目录上一级>\AuthBaseXiHan_ShadowCopy`（即 `D:\个人发布项目\AuthBaseXiHan_ShadowCopy`），
  **不要**放在站点目录里，也别往里写日志/数据（会频繁触发重启）
- 发布目录默认 `D:\个人发布项目\AuthBaseXiHan`，可用 `-PublishDir` 改
- 只授权不发布：`-SkipPublish`；发布后手动回收应用池：`-Recycle`（通常不需要，ANCM 会自动检测）
- IIS Express（端口 44359）主要用于开发调试，卷影复制针对完整 IIS；本地 `dotnet run` 不受这些配置影响

### 验证

```powershell
# 1. 发布的 web.config 含 handlerSettings 三个 handlerSetting（脚本会自动校验）
Select-String -Path D:\个人发布项目\AuthBaseXiHan\web.config -Pattern "handlerSetting name=`"enableShadowCopy`""

# 2. 站点能访问、登录流程正常
curl https://localhost:44359/health/live

# 3. 无感更新实测：站点运行中再次执行 .\deploy-shadowcopy.ps1，
#    不手动停应用池，观察 DLL 能被直接覆盖、应用自动加载新版本（可能有 1 次短暂 503）
```

## 开发要点（踩坑记录）

1. **错误码格式强制** `<域名>_<种类>_<编号>`（如 `AUTH_CONFLICT_001`），不合规直接抛异常
2. **Error 工厂第二参数是本地化消息键**（小写点分，如 `auth.account_exists`），中文展示文本用 `.WithText("…")` 附加
3. **业务失败用 `Result.Failure<T>(Error)` 返回**，不抛异常；HTTP 状态由错误种类推导（Conflict→409、Denied→403、Validation→400）
4. **实体必须实现 `XiHan.Framework.Domain.Entities.IEntity<TKey>`**，否则 `AddRepository<TEntity,TKey>` 编译不过
5. **唯一索引用 `[EntityIndex(..., IsUnique = true)]` 声明**，不要写 `HasIndex`——换 SqlSugar Provider 时索引名与建法由框架统一
6. **`ToTable` 要在 `ApplyPersistenceConventions(Database)` 之前**：索引名按表名生成
7. **EF SqlServer 驱动会带进有漏洞的 Pkcs 传递依赖**：显式引用 `System.Security.Cryptography.Pkcs 9.0.21` 以上覆盖
8. **Redis 键要过 `CacheKey` 校验**：非空、不含空白与控制字符、不超长，`new CacheKey("...")` 不合法直接抛 `ArgumentException`
9. **Redis 特性只提供 `caching.distributed`**：进程内 `ICache`（caching.local）仍由 Caching.InMemory/Hybrid 提供，本基座没装，业务只用 `ISharedCache`
10. 源生成器排查方法：csproj 加 `EmitCompilerGeneratedFiles` 落盘查看，记得 `DefaultItemExcludes` 排除产物目录避免二次编译
11. **JSON 响应里的中文显示为 `\uXXXX` 转义**：框架写 JSON（ProblemDetails 的 `Utf8JsonWriter`、操作响应的源生成上下文）都用默认 Encoder，非 ASCII 一律转义——这是合法 JSON（客户端 `JSON.parse` 后内容正确），只是 Scalar/curl 等原始视图不可读，rc.6 未暴露 Encoder 配置口。解法见 `ReadableJsonMiddleware`：在响应边缘把 JSON 解析后用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 重写直出中文。实现有两个关键点：①框架用 `Response.HasStarted` 判定端点是否已写过响应（成功只写一次、失败才写 ProblemDetails），缓冲流会让它恒为 false、成功响应被写两遍，故首写时直接 flush 原始流发送响应头（`HttpResponse.StartAsync` 内部也是靠 flush 触发的，换成缓冲流后走不到 Kestrel）；②显式 Content-Length 是按转义字节数算的，重写后长度变化，先清掉改用分块
12. **操作 ID 文法是全小写 + 连字符**：`^[a-z][a-z0-9]*(-[a-z0-9]+)*\.[a-z][a-z0-9]*(-[a-z0-9]+)*$`（如 `auth.logout-all`），驼峰（`logoutAll`）直接编译报 XHG003
13. **消息目录键只允许小写字母/数字/下划线**（`^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`），连字符不合法（启动抛 FrameworkException）；操作文档键由框架把操作 ID 的连字符转成下划线推导（`auth.logout-all` → `auth.logout_all.title`）
14. **装饰框架认证处理器（会话门禁）**：内核 Authenticate 阶段按方案名取 DI 里「第一个」匹配的 `IAuthenticationHandler`，装饰器必须先于 `AddXiHan` 登记；内层处理器按具体类型（`JwtAuthenticationHandler`）显式注册后注入，不能注入 `IEnumerable<IAuthenticationHandler>`（包含自身，循环依赖）
15. **自定义类型进 Redis 缓存**：缓存序列化链按源生成元数据解析，类型登记进 `AppJsonContext` 后调 `CacheSerializerRegistration.AddTypeInfo(services, AppJsonContext.Default)`；框架的 `IInvocationAuthenticationFilter` 只对绕过管道的端点生效，管道内请求的会话门禁必须走装饰认证处理器传输
16. **无感更新用 ANCM 应用卷影复制，不是 `vssadmin` 系统卷影**：`vssadmin` 是磁盘备份快照、需管理员且不解决 DLL 锁定；正解是 `web.config` 里 `<aspNetCore>` 下加 `<handlerSettings>` 子元素，配 `enableShadowCopy` / `shadowCopyDirectory` / `cleanShadowCopyDirectory` 三个 `<handlerSetting name="..." value="..." />`（Hosting Bundle ≥ 7.0）。**必须写成 handlerSetting 子元素，写成 `<aspNetCore>` 的 XML 属性不会生效**。副本目录放站点外、给应用池身份授「修改」权限（缺权限启动 500）；它只减少停机、**不能严格零停机**，回收瞬间仍可能 503
17. **预置 `web.config` 让发布自动生成卷影配置**：项目根放 `web.config`（含 `<handlers>` 与带 `<handlerSettings>` 的 `<aspNetCore>`），`dotnet publish` 会以它为模板合并环境变量等，不会用 SDK 默认模板覆盖——已实测发布产物保留 handlerSettings，CI/CD 无需发布后改服务器文件
18. **IIS 500.30 启动失败且堆栈是 `RedisConnectionException` → 连接串加 `abortConnect=false`（但仅此不够）**：StackExchange.Redis 默认首次连接失败即抛异常；加 `abortConnect=false` 后 Connect 阶段不抛了，但框架 `RedisCachingFeature.StartAsync` 仍显式检查连接状态，Redis 真离线照样抛 `Redis is not connected` 崩溃。**结论：Redis 必须先于应用启动，`abortConnect=false` 只是韧性补充**。IIS 更隐蔽：拉起时 Redis 未就绪就失败并卡快速失败状态，Redis 起来也不自愈，须回收应用池（非管理员可改 web.config 内容触发自动回收）。本机 Redis 是手动进程（Cygwin 版须 `cd D:/Redis-8.10.3 && ./redis-server.exe redis.conf`，绝对路径会被误解析），建议用 `RedisService.exe` 注册成服务开机自启
19. **卷影复制 + 环境变量注入的启动排障链**：`web.config` 用 `<environmentVariables><environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Development" /></environmentVariables>` 注入环境（本机真实配置在 `appsettings.Development.json`，Production 读占位符会连不上 SQL）；改 `web.config` 会触发 ANCM 重建卷影目录（`cleanShadowCopyDirectory=true` 清旧建新，子目录序号从 `0` 递增到 `1`）——所以「站点 200 但卷影目录文件数暂时为 0」是正常的重建中间态，等十几秒即可

## 已知问题（框架 rc.6）

- `/health/ready` 的 `status` 是 `pass`（HTTP 200），但 `state` 会一直显示 `Degraded`：
  启动时健康条目完成 `Started → Ready` 后，特性的状态仍按旧值置为 `Degraded`，且之后不再重新计算。
  `/health/details` 里条目 `persistence.entityframeworkcore.database` 与 `caching.redis.connection` 实际都是 `Ready`，
  只有 `xihan.persistence.entityframeworkcore` 这一个特性显示 `Degraded`。
  这是 rc.6 的状态聚合时序问题，不影响读写与探针判定（探针看 HTTP 状态码），等上游修复。

## 当前状态与后续

- 存储：**SQL Server + EF Core（EnsureCreated 自动建表，不用迁移）**，数据持久化（重启后账号仍在）
- 认证：**JWT + Redis 会话双轨（JWT 验签 + Redis 吊销，HS256，7 天有效期）**——`JwtTokenIssuer` 签发（含 `jti`）+ `JwtSecurityFeature` 验签 + `SessionGateHandler` 会话门禁 + `InMemorySecurityFeature` RBAC 授权；`GET /auth:whoami` 为受保护接口示例；`POST /auth:logout` 登出、`POST /auth:logout-all` 强制下线均已可用
- Redis：承载会话白名单（`auth:sessions:{jti}` + 用户会话索引）、幂等/分布式锁等框架能力
- 单设备登录（新登录踢掉旧端）：在 `SessionStore.SaveAsync` 里吊销该用户旧会话即可扩展
- 接入 gRPC/MCP/CLI 只需给 `[Operation]` 加 `Expose.Grpc` 等并安装对应协议包
