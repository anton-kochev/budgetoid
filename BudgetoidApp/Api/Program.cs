using Api.Endpoints;
using Api.Infrastructure;
using Application;
using Infrastructure;
using Application.Abstractions;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ServiceDefaults;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Registered non-pooled (AddDbContext, scoped) because BudgetoidDbContext depends on the scoped
// IUserContext for its transaction query filter, and pooled contexts can't take scoped
// dependencies. Aspire's AddNpgsqlDbContext pools contexts; EnrichNpgsqlDbContext re-applies
// Aspire's retry/health/telemetry defaults here.
builder.Services.AddDbContext<BudgetoidDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("budgetoid")));
builder.EnrichNpgsqlDbContext<BudgetoidDbContext>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IUserContext, HttpContextUserContext>();
// Read and validate required auth config at startup so a missing value fails fast on boot
// instead of throwing lazily inside the JwtBearer options factory on the first authenticated
// request (which surfaces as an opaque 500). Set via user-secrets, environment, or appsettings.
string googleClientId = builder.Configuration["Authentication:Google:ClientId"]
    ?? throw new InvalidOperationException("Authentication:Google:ClientId is required.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://accounts.google.com";
        options.Audience = googleClientId;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()));
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages(async statusCodeContext =>
{
    HttpContext httpContext = statusCodeContext.HttpContext;
    if (!httpContext.Response.HasStarted &&
        (httpContext.Response.StatusCode == StatusCodes.Status401Unauthorized ||
         httpContext.Response.StatusCode == StatusCodes.Status403Forbidden))
    {
        await Results.Problem(statusCode: httpContext.Response.StatusCode)
            .ExecuteAsync(httpContext);
    }
});
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<UserProvisioningMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();

    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    BudgetoidDbContext db = scope.ServiceProvider.GetRequiredService<BudgetoidDbContext>();
    await db.Database.MigrateAsync();
}

app.MapDefaultEndpoints();
app.MapTransactionEndpoints();

await app.RunAsync();
