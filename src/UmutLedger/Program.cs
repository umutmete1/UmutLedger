using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using UmutLedger.Api.Errors;
using UmutLedger.Api.Telemetry;
using UmutLedger.Application.Accounts;
using UmutLedger.Application.Transfers;
using UmutLedger.Infrastructure.Persistence;
using UmutLedger.Infrastructure.Telemetry;

const string apiReferenceTitle = "UmutLedger API";

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = InvalidModelStateResponse.Create;
        options.SuppressMapClientErrors = true;
    })
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<LedgerDbContext>((services, options) =>
    options.UseNpgsql(LedgerConnectionPool.ApplyDefaultLimit(
        services.GetRequiredService<IConfiguration>().GetConnectionString(LedgerConstraints.ConnectionStringName))));
builder.Services.AddSingleton<LedgerMetrics>();
builder.Services.AddSingleton<ILedgerMetrics>(services => new FailSafeLedgerMetrics(
    services.GetRequiredService<LedgerMetrics>(),
    services.GetRequiredService<ILogger<FailSafeLedgerMetrics>>()));
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<ITransferService>(services => new InstrumentedTransferService(
    services.GetRequiredService<TransferService>(),
    services.GetRequiredService<ILedgerMetrics>(),
    services.GetRequiredService<ILogger<InstrumentedTransferService>>()));

builder.Services.AddLedgerTelemetry(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages(StatusCodeProblem.WriteAsync);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle(apiReferenceTitle));
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
