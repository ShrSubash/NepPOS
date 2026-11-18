using System.Security.Cryptography;
using System.Text;

namespace NepPOS.HeadOffice.Api.Security;

/// <summary>
/// The 6-digit codes from an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password):
/// RFC 6238, HMAC-SHA1, 30-second steps. A code from one step either side is accepted, for clocks a little out.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new secret: 160 random bits as Base32, the form authenticator apps take.</summary>
    public static string NewSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    /// <summary>The link a QR code carries: the app shows "NepPOS Portal (you@example.com)".</summary>
    public static string OtpAuthUri(string secret, string account, string issuer = "NepPOS Portal") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one time step (public for the RFC test vectors).</summary>
    public static string CodeAt(byte[] key, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(new string('0', digits));
    }

    /// <summary>
    /// Checks a typed code. Returns the time step it matched, so the caller can refuse the same code
    /// twice (a code seen over someone's shoulder can't be reused), or null if it doesn't match.
    /// </summary>
    public static long? Verify(string secret, string? code, DateTimeOffset now, long lastUsedStep = -1)
    {
        var typed = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (typed.Length != Digits)
            return null;

        byte[] key;
        try { key = FromBase32(secret); }
        catch (FormatException) { return null; }

        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step <= lastUsedStep)
                continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeAt(key, step)), Encoding.ASCII.GetBytes(typed)))
                return step;
        }
        return null;
    }

    public static string ToBase32(byte[] data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var clean = text.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException($"'{c}' is not Base32.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
