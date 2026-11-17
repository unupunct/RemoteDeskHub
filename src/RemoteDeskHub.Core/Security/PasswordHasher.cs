using System.Security.Cryptography;
using System.Text;

namespace RemoteDeskHub.Core.Security;

/// <summary>PBKDF2-SHA256 password hashing with per-account salt and high iteration count.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (string Hash, string Salt, int Iterations) HashPassword(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length < 1)
        {
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    public static bool VerifyPassword(string password, string hashBase64, string saltBase64, int iterations)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Generates a URL-safe, 256-bit secret suitable for enrollment tokens.</summary>
    public static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string GenerateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        return token.Length <= 10
            ? new string('*', token.Length)
            : $"{token[..4]}...{token[^4..]}";
    }

    public static bool IsPasswordStrong(string password, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
        {
            error = "Password must contain at least 10 characters.";
            return false;
        }

        if (password.Length > 256)
        {
            error = "Password must be 256 characters or fewer.";
            return false;
        }

        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
        {
            error = "Password must include upper-case, lower-case and numeric characters.";
            return false;
        }

        return true;
    }
}
