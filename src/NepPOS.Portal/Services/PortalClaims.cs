using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using NepPOS.Contracts.Admin;

namespace NepPOS.Portal.Services;

public static class PortalClaims
{
    /// <summary>"required" until the person has set up an authenticator app; every other page is closed until then.</summary>
    public const string TwoFactorSetup = "two_factor_setup";

    /// <summary>Signs the person in to the Portal with what Head Office returned.</summary>
    public static Task SignInAsync(HttpContext http, StaffSignInResponse staff, bool needsTwoFactorSetup)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, staff.Id.ToString()),
            new(ClaimTypes.Email, staff.Email),
            new(ClaimTypes.Name, staff.Name),
            new(ClaimTypes.Role, staff.Role)
        };
        if (needsTwoFactorSetup)
            claims.Add(new Claim(TwoFactorSetup, "required"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    }

    public static Guid? StaffId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

public static class PortalPolicies
{
    public const string SignedIn = "SignedIn";
    public const string Owner = "Owner";
}
