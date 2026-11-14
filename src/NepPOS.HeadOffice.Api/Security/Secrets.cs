using System.Security.Cryptography;
using System.Text;

namespace NepPOS.HeadOffice.Api.Security;

/// <summary>Makes and checks the secrets Head Office hands out: activation codes and register API keys.</summary>
public static class Secrets
{
    // No 0/O or 1/I/L, so a code read out over the phone can't be mistyped.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>"K7QM-3XRT-9PWD": 12 characters, about 59 bits, valid for a few days and once only.</summary>
    public static string NewActivationCode()
    {
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }

    /// <summary>Codes are typed by people: ignore case, spaces and dashes.</summary>
    public static string NormaliseCode(string code) =>
        new string((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>"npk_" + 256 random bits. Shown once; only its hash is stored.</summary>
    public static string NewApiKey() =>
        "npk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// SHA-256 as hex. Enough for these secrets: they are long and random, so they can't be guessed
    /// from the hash (a slow password hash is only needed for things people choose).
    /// </summary>
    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>Compares in constant time, so the time taken says nothing about how much matched.</summary>
    public static bool SameSecret(string? given, string expected) =>
        given is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}
