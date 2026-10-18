using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Enppi.Workload.Api.Auth;

public static class AuthModes
{
    /// <summary>Production: Azure App Service Authentication with Microsoft Entra ID signs the user in and injects
    /// X-MS-CLIENT-PRINCIPAL; App Service strips that header from incoming requests, so it cannot be forged.</summary>
    public const string EasyAuth = "EasyAuth";
    /// <summary>Local runs and tests only: pick a seeded user (cookie dev_user or header X-Dev-User).</summary>
    public const string Development = "Development";
}

public static class WorkloadClaims
{
    public const string Department = "enppi:dept";
    public const string Upn = "enppi:upn";
}

/// <summary>Builds the application principal: role and department scope come from Entra app roles when present,
/// otherwise from the Users table maintained by the administrator.</summary>
public class PrincipalFactory(WorkloadDbContext db)
{
    public async Task<ClaimsPrincipal?> CreateAsync(string upn, string? name, IEnumerable<string> entraRoles, string scheme)
    {
        upn = upn.Trim().ToLowerInvariant();
        var account = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Upn == upn);
        if (account is { IsActive: false }) return null;
        var roles = entraRoles.Where(r => Roles.All.Contains(r)).ToList();
        if (roles.Count == 0 && account is not null) roles.Add(account.Role);
        if (roles.Count == 0) roles.Add(Roles.Viewer);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, account?.DisplayName ?? name ?? upn),
            new(WorkloadClaims.Upn, upn),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (account is not null)
            claims.AddRange(account.Departments().Select(d => new Claim(WorkloadClaims.Department, d)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, ClaimTypes.Name, ClaimTypes.Role));
    }
}

public class EasyAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
                             PrincipalFactory factory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var header) || string.IsNullOrEmpty(header))
            return AuthenticateResult.NoResult();
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header!)));
            var claims = doc.RootElement.GetProperty("claims").EnumerateArray()
                .Select(c => (Type: c.GetProperty("typ").GetString() ?? "", Value: c.GetProperty("val").GetString() ?? "")).ToList();
            string? Find(params string[] types) => claims.FirstOrDefault(c => types.Contains(c.Type)).Value;
            var upn = Find("preferred_username", ClaimTypes.Upn, "upn", ClaimTypes.Email, "email")
                      ?? Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
            if (string.IsNullOrWhiteSpace(upn)) return AuthenticateResult.Fail("No user name in the Entra principal");
            var roles = claims.Where(c => c.Type is "roles" or ClaimTypes.Role).Select(c => c.Value);
            var principal = await factory.CreateAsync(upn, Find("name"), roles, Scheme.Name);
            return principal is null ? AuthenticateResult.Fail("User is disabled")
                                     : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return AuthenticateResult.Fail("Invalid X-MS-CLIENT-PRINCIPAL header");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers["X-Login-Url"] = "/.auth/login/aad";
        return Task.CompletedTask;
    }
}

public class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
                            PrincipalFactory factory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Cookie = "dev_user";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var upn = Request.Headers["X-Dev-User"].FirstOrDefault() ?? Request.Cookies[Cookie];
        if (string.IsNullOrWhiteSpace(upn)) return AuthenticateResult.NoResult();
        var principal = await factory.CreateAsync(upn, null, Array.Empty<string>(), Scheme.Name);
        return principal is null ? AuthenticateResult.Fail("Unknown or disabled user")
                                 : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers["X-Login-Url"] = "/login";
        return Task.CompletedTask;
    }
}

/// <summary>The signed-in user's role and department scope, checked on every API call.</summary>
public class CurrentUser(IHttpContextAccessor accessor)
{
    private ClaimsPrincipal P => accessor.HttpContext?.User ?? new ClaimsPrincipal();
    public string Upn => P.FindFirstValue(WorkloadClaims.Upn) ?? "anonymous";
    public string Name => P.Identity?.Name ?? Upn;
    public IReadOnlyList<string> RolesList => P.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
    public IReadOnlyList<string> Departments => P.FindAll(WorkloadClaims.Department).Select(c => c.Value).ToList();
    public bool Is(string role) => P.IsInRole(role);
    public bool IsCorporatePlanning => Is(Roles.CorporatePlanning);
    public bool CanSeeAll => IsCorporatePlanning || Is(Roles.Viewer) || Is(Roles.Admin);
    public bool CanReview(string dept) => IsCorporatePlanning || (Is(Roles.DisciplineManager) && Departments.Contains(dept));
    public bool CanSeeDept(string dept) => CanSeeAll || Is(Roles.Planner) || Departments.Contains(dept);
}
