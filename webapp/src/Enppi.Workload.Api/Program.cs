using System.Text.Json.Serialization;
using Enppi.Workload.Api.Api;
using Enppi.Workload.Api.Auth;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Enppi.Workload.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ------------------------------------------------------------------ database: Azure SQL in production, SQLite locally
var provider = config["Database:Provider"] ?? "Sqlite";
var connection = config.GetConnectionString("Workload") ?? "Data Source=workload.db";
builder.Services.AddDbContext<WorkloadDbContext>(o =>
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
    else
        o.UseSqlite(connection);
});

// ------------------------------------------------------------------ sign-in: Entra ID through App Service Authentication
var authMode = config["Auth:Mode"] ?? AuthModes.EasyAuth;
// pick-a-user sign-in is for a developer's PC and the automated tests only
if (authMode == AuthModes.Development && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("Auth:Mode Development is allowed only when ASPNETCORE_ENVIRONMENT is Development.");
// The Entra principal header is only trustworthy when App Service Authentication is switched on (the platform then strips
// any incoming X-MS-* header). Refuse to start otherwise, unless a trusted reverse proxy is declared explicitly.
if (authMode == AuthModes.EasyAuth && !builder.Environment.IsEnvironment("Testing") &&
    !string.Equals(Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED"), "True", StringComparison.OrdinalIgnoreCase) &&
    !config.GetValue("Auth:TrustPrincipalHeader", false))
    throw new InvalidOperationException("Auth:Mode EasyAuth requires App Service Authentication (WEBSITE_AUTH_ENABLED=True) " +
                                        "or Auth:TrustPrincipalHeader=true behind a trusted proxy that sets X-MS-CLIENT-PRINCIPAL.");
builder.Services.AddScoped<PrincipalFactory>();
builder.Services.AddAuthentication(authMode)
    .AddScheme<AuthenticationSchemeOptions, EasyAuthHandler>(AuthModes.EasyAuth, _ => { })
    .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(AuthModes.Development, _ => { });
builder.Services.AddAuthorization(o =>
{
    foreach (var role in Roles.All) o.AddPolicy(role, p => p.RequireRole(role));
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();

builder.Services.AddScoped<WorkloadService>();
builder.Services.AddScoped<WorkflowService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddSingleton<INotificationSender, SmtpNotificationSender>();
builder.Services.AddHostedService<ReminderService>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // enums travel as names only: an integer such as kind 3 or category 9 is refused, not stored
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks().AddDbContextCheck<WorkloadDbContext>();

var app = builder.Build();

// ------------------------------------------------------------------ schema and seed
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WorkloadDbContext>();
    if (db.Database.IsSqlite()) await db.Database.EnsureCreatedAsync();
    else if (config.GetValue("Database:MigrateOnStartup", true)) await db.Database.MigrateAsync();
    if (config.GetValue("Seed:Demo", false)) await DemoSeed.SeedDemoAsync(db);
    else await DemoSeed.SeedReferenceAsync(db);
}

// ------------------------------------------------------------------ errors: rule breaks are 400, scope breaks 403
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new { error = "Unexpected error - the details are in the application log." });
}));
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is RuleException or ForbiddenException or KeyNotFoundException or BadHttpRequestException)
    {
        (int code, string message) = ex switch
        {
            ForbiddenException => (403, ex.Message),
            KeyNotFoundException => (404, ex.Message),
            _ => (400, ex.Message),
        };
        app.Logger.LogInformation("{Method} {Path} refused ({Code}): {Message}", ctx.Request.Method, ctx.Request.Path, code, message);
        ctx.Response.StatusCode = code;
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }
});

if (app.Environment.IsDevelopment() || config.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// browser hardening: no framing (click-jacking), no MIME sniffing, scripts only from this site
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Frame-Options"] = "DENY";
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "same-origin";
    if (!ctx.Request.Path.StartsWithSegments("/swagger"))
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                                       "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
// cross-site request forgery: every state-changing API call must carry the X-Requested-With header that the screens
// send; a browser adds a custom header to a cross-site request only after a CORS pre-flight, which this API never allows
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) &&
        !HttpMethods.IsOptions(ctx.Request.Method) && string.IsNullOrEmpty(ctx.Request.Headers["X-Requested-With"]))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-Requested-With header." });
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
if (authMode == AuthModes.Development) app.MapDevelopmentLogin();
app.MapWorkloadApi();
app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "No such API endpoint." }));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
