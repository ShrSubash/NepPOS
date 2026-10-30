using NepPOS.Domain.Entities;

namespace NepPOS.Application.Authentication;

/// <summary>
/// Abstraction over "how did this employee prove who they are".
/// Presentation layer depends only on this — never on a concrete
/// auth mechanism — so Fingerprint/Rfid can be added as new
/// implementations without touching Login.razor.
/// </summary>
public interface IAuthenticationService
{
    Task<AuthenticationResult> AuthenticateByPinAsync(string pin, CancellationToken cancellationToken = default);
}

public sealed record AuthenticationResult(bool Succeeded, Employee? Employee, string? ErrorMessage)
{
    public static AuthenticationResult Success(Employee employee) => new(true, employee, null);
    public static AuthenticationResult Failure(string errorMessage) => new(false, null, errorMessage);
}