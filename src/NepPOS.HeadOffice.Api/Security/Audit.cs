using NepPOS.Contracts.Admin;
using NepPOS.HeadOffice.Api.Data;

namespace NepPOS.HeadOffice.Api.Security;

/// <summary>Adds a line to the audit log, saved with the change it describes (same SaveChanges).</summary>
public static class Audit
{
    /// <summary>The Portal says who is signed in; a script using the admin key directly shows as "admin key".</summary>
    public static string ActorOf(HttpContext http)
    {
        var user = http.Request.Headers[PortalHeaders.User].ToString().Trim();
        return string.IsNullOrEmpty(user) ? "admin key" : user[..Math.Min(256, user.Length)];
    }

    public static void Add(HeadOfficeDbContext db, HttpContext http, string action, Guid? tenantId, string summary) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Actor = ActorOf(http),
            Action = action,
            TenantId = tenantId,
            Summary = summary.Length > 500 ? summary[..500] : summary
        });
}
