using System.Security.Cryptography;
using System.Text;

namespace NepPOS.Infrastructure.Database.Authentication;

public static class PinHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 100_000;

    public static (string Hash, string Salt) Hash(string pin)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hashBytes = Derive(pin, saltBytes);
        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public static bool Verify(string pin, string storedSalt, string storedHash)
    {
        var saltBytes = Convert.FromBase64String(storedSalt);
        var computedHash = Derive(pin, saltBytes);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(storedHash), computedHash);
    }

    private static byte[] Derive(string pin, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(HashSizeBytes);
    }
}