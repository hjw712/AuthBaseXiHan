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
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(LogoutResponse))]
[JsonSerializable(typeof(LogoutAllRequest))]
[JsonSerializable(typeof(LogoutAllResponse))]
[JsonSerializable(typeof(WhoAmIRequest))]
[JsonSerializable(typeof(WhoAmIResponse))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
