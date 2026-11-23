using Microsoft.EntityFrameworkCore;
using NepPOS.Contracts.Licensing;
using NepPOS.HeadOffice.Api.Data;
using NepPOS.HeadOffice.Api.Security;

namespace NepPOS.HeadOffice.Api.Licensing;

/// <summary>
/// A website or app key works only while the business's licence is in force and includes the website.
/// Otherwise every website call gets 403 with the reason, which the website can show or log.
/// </summary>
public sealed class WebsiteLicenceFilter : IEndpointFilter
{
    // Made once per endpoint from the root services, so the database is taken from each request instead.
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var db = http.RequestServices.GetRequiredService<HeadOfficeDbContext>();
        var licences = http.RequestServices.GetRequiredService<LicenceService>();
        var tenantId = WebsiteApiKeyHandler.TenantOf(http.User);
        var name = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(http.RequestAborted) ?? "This business";

        var licence = await licences.GetOrStartTrialAsync(tenantId, http.RequestAborted);
        var state = LicenceService.StateOf(licence, name, DateTime.UtcNow);
        if (state.IsLocked)
            return Results.Problem($"{state.Headline}. The website can't read from NepPOS until it's renewed.", statusCode: 403, title: "Licence not active");
        if (!licence.FeatureList.Contains(LicenceFeatures.Website, StringComparer.OrdinalIgnoreCase))
            return Results.Problem($"{name}'s NepPOS plan doesn't include the website and app. Add it to the plan in NepPOS Portal.", statusCode: 403, title: "Not in plan");

        return await next(context);
    }
}
