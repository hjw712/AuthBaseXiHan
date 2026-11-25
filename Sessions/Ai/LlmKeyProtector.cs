using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>
/// 模型 API Key 的加密与掩码：全应用唯一一处处理明文 Key 的地方。
/// </summary>
/// <remarks>
/// 用 Data Protection 而不是自己搓 AES：密钥环由框架的基础设施托管（本项目持久化到 Redis），
/// 多实例部署时各实例能解同一份密文；轮换密钥环时旧密文仍可解，不用停机重录。
///
/// 专用用途串（purpose）是安全边界：不同用途派生出的密钥互不通用，
/// 一个用途的密钥泄漏不会牵连同进程的其他加密数据。
/// </remarks>
public sealed class LlmKeyProtector(IDataProtectionProvider protectionProvider)
{
    /// <summary>专用用途串：改动会让已存的密文全部解不开，等同于要求重新录入所有 Key</summary>
    private const string Purpose = "AuthBaseXiHan.LlmProviderConfig.ApiKey.v1";

    private IDataProtector Protector => protectionProvider.CreateProtector(Purpose);

    /// <summary>加密明文 Key（写入配置时调用）</summary>
    public byte[] Protect(string plainApiKey) =>
        Protector.Protect(Encoding.UTF8.GetBytes(plainApiKey));

    /// <summary>解密 Key（构建客户端时调用）；失败抛出，由调用方转成配置损坏的错误</summary>
    public string Unprotect(byte[] encrypted, long configId)
    {
        try
        {
            return Encoding.UTF8.GetString(Protector.Unprotect(encrypted));
        }
        catch (Exception ex)
        {
            // 密钥环丢失或跨实例未共享时报这个：不记录明文，只记配置主键便于排查
            throw new InvalidOperationException(
                $"模型配置 {configId} 的密钥解密失败，请检查密钥环持久化配置", ex);
        }
    }

    /// <summary>生成掩码串（列表接口回显用，明文绝不外泄）</summary>
    public static string Mask(string plainApiKey)
    {
        if (string.IsNullOrEmpty(plainApiKey)) return string.Empty;

        // 只保留厂商前缀（sk- / ofm- 等）与末 4 位：足够运营核对「是哪一把」，
        // 又不足以还原密钥本身
        var tail = plainApiKey.Length >= 4 ? plainApiKey[^4..] : plainApiKey;
        var dash = plainApiKey.IndexOf('-');
        var prefix = dash is > 0 and <= 6 ? plainApiKey[..(dash + 1)] : string.Empty;
        return $"{prefix}****{tail}";
    }
}