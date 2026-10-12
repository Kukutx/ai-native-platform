using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AiNative.Application.Integrations;
using AiNative.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AiNative.Infrastructure.Integrations;

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>
    /// 加密空间密钥用的主密钥：32 字节随机数的 Base64（openssl rand -base64 32）。
    /// 不设置时由令牌签名密钥派生，能用，但换签名密钥会让已保存的密钥全部解不开，生产环境建议单独设置。
    /// </summary>
    public string? MasterKey { get; set; }

    /// <summary>
    /// 换主密钥后保留的旧主密钥，只用来解密还没重新保存过的密钥。
    /// </summary>
    public List<string> PreviousMasterKeys { get; set; } = [];
}

/// <summary>
/// AES-256-GCM 加密空间密钥。密文格式：版本(1) + 主密钥指纹(4) + 随机数(12) + 校验标签(16) + 密文；
/// 附加认证数据是空间 ID 和密钥名，密文换到别的空间或别的名字下都解不开。
/// 主密钥不进数据库：拿到数据库备份也读不出密钥。指纹用来在多把主密钥里挑出加密时用的那把，便于轮换。
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const byte Version = 1;
    private const int KeyIdSize = 4;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + KeyIdSize + NonceSize + TagSize;

    private readonly byte[] _current;
    private readonly Dictionary<uint, byte[]> _keys = [];

    public AesGcmSecretProtector(IOptions<SecretsOptions> options, IOptions<AuthOptions> auth)
    {
        var configured = new List<byte[]>();
        if (!string.IsNullOrWhiteSpace(options.Value.MasterKey))
        {
            configured.Add(Decode(options.Value.MasterKey, "Secrets:MasterKey"));
        }

        // 由签名密钥派生的那把总在列表里：先用派生密钥、后来才单独设置主密钥的部署，旧密钥照样能解开。
        configured.Add(HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(auth.Value.SigningKey), 32,
            Encoding.UTF8.GetBytes("ainative-tenant-secrets"), Encoding.UTF8.GetBytes("v1")));
        configured.AddRange(options.Value.PreviousMasterKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => Decode(k, "Secrets:PreviousMasterKeys")));

        _current = configured[0];
        foreach (var key in configured)
        {
            _keys.TryAdd(KeyId(key), key);
        }
    }

    public byte[] Protect(Guid tenantId, string name, string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var output = new byte[HeaderSize + plain.Length];
        output[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1, KeyIdSize), KeyId(_current));
        var nonce = output.AsSpan(1 + KeyIdSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_current, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(HeaderSize), output.AsSpan(1 + KeyIdSize + NonceSize, TagSize), AssociatedData(tenantId, name));
        return output;
    }

    public string Unprotect(Guid tenantId, string name, byte[] cipherText)
    {
        if (cipherText.Length < HeaderSize || cipherText[0] != Version)
        {
            throw Unreadable(name);
        }

        var keyId = BinaryPrimitives.ReadUInt32BigEndian(cipherText.AsSpan(1, KeyIdSize));
        if (!_keys.TryGetValue(keyId, out var key))
        {
            throw Unreadable(name);
        }

        var plain = new byte[cipherText.Length - HeaderSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                cipherText.AsSpan(1 + KeyIdSize, NonceSize),
                cipherText.AsSpan(HeaderSize),
                cipherText.AsSpan(1 + KeyIdSize + NonceSize, TagSize),
                plain,
                AssociatedData(tenantId, name));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw Unreadable(name);
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] AssociatedData(Guid tenantId, string name) => [.. tenantId.ToByteArray(), .. Encoding.UTF8.GetBytes(name)];

    private static uint KeyId(byte[] key) => BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(key));

    private static byte[] Decode(string value, string setting)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{setting} 不是合法的 Base64。请用 openssl rand -base64 32 生成。");
        }

        return key.Length == 32 ? key : throw new InvalidOperationException($"{setting} 必须是 32 字节（Base64 后 44 个字符）。请用 openssl rand -base64 32 生成。");
    }

    private static SecretUnavailableException Unreadable(string name)
        => new($"密钥 {name} 无法解密：服务器的主密钥可能被更换过（Secrets:MasterKey 或令牌签名密钥），请重新填写它的值。");
}

/// <summary>
/// 启动时检查主密钥格式，配错了宁可起不来，也不要等到第一次读密钥时才报错。
/// </summary>
internal sealed class SecretsOptionsValidator : IValidateOptions<SecretsOptions>
{
    public ValidateOptionsResult Validate(string? name, SecretsOptions options)
    {
        var keys = new List<(string Setting, string Value)>();
        if (!string.IsNullOrWhiteSpace(options.MasterKey))
        {
            keys.Add(("Secrets:MasterKey", options.MasterKey));
        }

        keys.AddRange(options.PreviousMasterKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => ("Secrets:PreviousMasterKeys", k)));
        Span<byte> buffer = stackalloc byte[64];
        foreach (var (setting, value) in keys)
        {
            if (!Convert.TryFromBase64String(value.Trim(), buffer, out var length) || length != 32)
            {
                return ValidateOptionsResult.Fail($"{setting} 必须是 32 字节随机数的 Base64（44 个字符），请用 openssl rand -base64 32 生成。");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
