namespace NepPOS.Domain.Enums;

/// <summary>
/// Supported sign-in methods. Only Pin is wired up today;
/// Fingerprint and Rfid are exposed in Settings for future enablement.
/// </summary>
public enum AuthenticationMethod
{
    Pin = 0,
    Fingerprint = 1,
    Rfid = 2
}