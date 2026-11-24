using System.Text.Json.Serialization;
using AuthBaseXiHan.Operations;

namespace AuthBaseXiHan;

/// <summary>
/// 源生成序列化上下文：内核无反射，所有请求/响应类型必须登记于此，
/// 缺登记编译报 XHG002；string[] 供 SessionStore 的会话索引经缓存序列化
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CaptchaRequest))]
[JsonSerializable(typeof(CaptchaResponse))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(RegisterResponse))]

// 会话契约：登录各轨自持（LoginRequest/Response、AdminLoginRequest/Response），
// whoami / logout / logout-all 单接口双轨通用（CommonOperations）
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(AdminLoginRequest))]
[JsonSerializable(typeof(AdminLoginResponse))]
[JsonSerializable(typeof(SessionWhoAmIRequest))]
[JsonSerializable(typeof(SessionWhoAmIResponse))]
[JsonSerializable(typeof(SessionLogoutRequest))]
[JsonSerializable(typeof(SessionLogoutResponse))]
[JsonSerializable(typeof(SessionLogoutAllRequest))]
[JsonSerializable(typeof(SessionLogoutAllResponse))]
[JsonSerializable(typeof(string[]))]

// AI 模块：对话 + 模型配置管理
[JsonSerializable(typeof(AiChatRequest))]
[JsonSerializable(typeof(AiChatResponse))]
[JsonSerializable(typeof(SessionPagedResponse<LlmConfigItem>))]
[JsonSerializable(typeof(LlmConfigUpsertRequest))]
[JsonSerializable(typeof(LlmConfigUpsertResponse))]
[JsonSerializable(typeof(LlmConfigTestRequest))]
[JsonSerializable(typeof(LlmConfigTestResponse))]
[JsonSerializable(typeof(LlmConfigDeleteRequest))]
[JsonSerializable(typeof(LlmConfigDeleteResponse))]

// 提示词管理：列表（通用分页契约）/新增更新/删除
[JsonSerializable(typeof(SessionPagedRequest))]
[JsonSerializable(typeof(SessionPagedResponse<PromptItem>))]
[JsonSerializable(typeof(PromptUpsertRequest))]
[JsonSerializable(typeof(PromptUpsertResponse))]
[JsonSerializable(typeof(PromptDeleteRequest))]
[JsonSerializable(typeof(PromptDeleteResponse))]

// 管理员模块：账号管理（登录/登出/身份已并入通用会话契约；列表用通用分页契约）
[JsonSerializable(typeof(SessionPagedResponse<AdminUserItem>))]
[JsonSerializable(typeof(AdminCreateUserRequest))]
[JsonSerializable(typeof(AdminCreateUserResponse))]
[JsonSerializable(typeof(AdminResetPasswordRequest))]
[JsonSerializable(typeof(AdminResetPasswordResponse))]
[JsonSerializable(typeof(AdminSetStatusRequest))]
[JsonSerializable(typeof(AdminSetStatusResponse))]
[JsonSerializable(typeof(AdminDeleteUserRequest))]
[JsonSerializable(typeof(AdminDeleteUserResponse))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
