using Api.Endpoints;
using Api.Infrastructure;
using Application;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Registered non-pooled (AddDbContext, scoped) so the scoped IUserContext can be injected into
// the DbContext constructor for the per-user query filter. Aspire's AddNpgsqlDbContext pools the
// context, which forbids scoped ctor injection. EnrichNpgsqlDbContext re-applies Aspire's
// retry/health/telemetry defaults to this self-registered context.
builder.Services.AddDbContext<BudgetoidDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("budgetoid")));
builder.EnrichNpgsqlDbContext<BudgetoidDbContext>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();
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
app.UseStatusCodePages();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    BudgetoidDbContext db = scope.ServiceProvider.GetRequiredService<BudgetoidDbContext>();
    await db.Database.MigrateAsync();
}

app.MapDefaultEndpoints();
app.MapTransactionEndpoints();

await app.RunAsync();
