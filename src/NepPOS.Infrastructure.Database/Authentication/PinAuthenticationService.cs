using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Authentication;
using NepPOS.Domain.Entities;

namespace NepPOS.Infrastructure.Database.Authentication;

/// <summary>
/// PIN-based authentication against the local SQLite register database.
/// Runs entirely offline — consistent with the Slave Register's
/// offline-resilience requirement.
/// </summary>
public sealed class PinAuthenticationService : IAuthenticationService
{
    private readonly WorkforceDbContext _dbContext;
    private readonly ILogger<PinAuthenticationService> _logger;

    public PinAuthenticationService(WorkforceDbContext dbContext, ILogger<PinAuthenticationService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AuthenticationResult> AuthenticateByPinAsync(string pin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return AuthenticationResult.Failure("Enter a PIN.");
        }

        try
        {
            var candidates = await _dbContext.Employees
                .Where(e => e.IsActive)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            foreach (var employee in candidates)
            {
                if (PinHasher.Verify(pin, employee.PinSalt, employee.PinHash))
                {
                    return AuthenticationResult.Success(employee);
                }
            }

            return AuthenticationResult.Failure("Incorrect PIN.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PIN authentication failed unexpectedly.");
            return AuthenticationResult.Failure("Unable to sign in right now. Try again.");
        }
    }
}