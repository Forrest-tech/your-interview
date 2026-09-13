using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace YourInterview.Services.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id 密码哈希。为什么不是 PBKDF2/bcrypt(面试常问):
///  - Argon2id 是 PHC 竞赛冠军,同时抗 GPU/ASIC(内存硬)与侧信道(混合模式)
///  - OWASP 2024 首选推荐;PBKDF2 抗 GPU 弱,bcrypt 有 72 字节截断
///  - 每个密码独立 16 字节 salt,参数随哈希一起存 → 未来提升参数可平滑升级
/// 存储格式:$argon2id$v=19$m=65536,t=3,p=1$&lt;b64salt&gt;$&lt;b64hash&gt;
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string encodedHash);
    bool NeedsRehash(string encodedHash);
}

public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int MemoryKb = 65536;   // 64 MB
    private const int Iterations = 3;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKb, Iterations, Parallelism, HashBytes);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        if (!TryParse(encodedHash, out var salt, out var expected, out var m, out var t, out var p))
            return false;

        var actual = Derive(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected); // 恒定时间比较,防时序攻击
    }

    public bool NeedsRehash(string encodedHash)
    {
        if (!TryParse(encodedHash, out _, out _, out var m, out var t, out var p)) return true;
        return m < MemoryKb || t < Iterations || p < Parallelism;
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int outputBytes)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        return argon2.GetBytes(outputBytes);
    }

    private static bool TryParse(string encoded, out byte[] salt, out byte[] hash, out int memory, out int iterations, out int parallelism)
    {
        salt = []; hash = []; memory = 0; iterations = 0; parallelism = 0;
        var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id") return false;

        foreach (var kv in parts[2].Split(','))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2) continue;
            switch (pair[0])
            {
                case "m": memory = int.Parse(pair[1]); break;
                case "t": iterations = int.Parse(pair[1]); break;
                case "p": parallelism = int.Parse(pair[1]); break;
            }
        }

        try
        {
            salt = Convert.FromBase64String(Pad(parts[3]));
            hash = Convert.FromBase64String(Pad(parts[4]));
            return salt.Length > 0 && hash.Length > 0;
        }
        catch (FormatException) { return false; }
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');
    private static string Pad(string s) => s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
}

/// <summary>刷新令牌:只存 SHA-256 哈希,原值只在响应里出现一次(等同于密码的处置方式)。</summary>
public static class TokenHasher
{
    public static string Hash(string rawToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    public static string CreateRaw(int bytes = 48)
        => Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));
}

internal static class Base64UrlTextEncoder
{
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
