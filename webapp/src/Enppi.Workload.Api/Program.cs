using System.Text.Json.Serialization;
using Enppi.Workload.Api.Api;
using Enppi.Workload.Api.Auth;
using Enppi.Workload.Api.Data;
using Enppi.Workload.Api.Domain;
using Enppi.Workload.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
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
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
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
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    (int code, string message) = ex switch
    {
        RuleException r => (400, r.Message),
        ForbiddenException f => (403, f.Message),
        KeyNotFoundException k => (404, k.Message),
        BadHttpRequestException b => (400, b.Message),
        _ => (500, "Unexpected error - the details are in the application log."),
    };
    if (code == 500) app.Logger.LogError(ex, "Unhandled error");
    ctx.Response.StatusCode = code;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

if (app.Environment.IsDevelopment() || config.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
if (authMode == AuthModes.Development) app.MapDevelopmentLogin();
app.MapWorkloadApi();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
