using Medallion.Threading;
using Medallion.Threading.Redis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Refit;
using RinhaBackend.Api;
using RinhaBackend.Database;
using Scalar.AspNetCore;
using Polly.Extensions.Http;
using RinhaBackend.Dto;
using RinhaBackend.Factory;
using RinhaBackend.Messages;
using StackExchange.Redis;


var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AllowSynchronousIO = false;
    options.Limits.MaxConcurrentConnections = 1000;
    options.Limits.MaxConcurrentUpgradedConnections = 1000;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
});

builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(); // <- por último, sobrescreve tudo

builder.Services.AddOpenApi();

// Redis config
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetValue<string>("REDIS") ??
                                  throw new NullReferenceException("REDIS")));

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration =
        builder.Configuration.GetValue<string>("REDIS") ?? throw new NullReferenceException("REDIS");
});
builder.Services.AddSingleton<IDistributedLockProvider>(sp =>
{
    var redis = sp.GetRequiredService<IConnectionMultiplexer>();
    return new RedisDistributedSynchronizationProvider(redis.GetDatabase());
});

// Queue stuff
builder.Services.AddSingleton<IMemoryPublisher, MemoryPublisher>();
builder.Services.AddSingleton<IRedisPublisher, RedisPublisher>();

builder.Services.AddTransient<IPaymentProcessorFactory, PaymentProcessorFactory>();

AddEntityFrameworkCore(builder);
AddRefit(builder);

// builder.Services.AddHostedService<MessageConsumerBackground>();
builder.Services.AddHostedService<RedisConsumerBackground>();

// builder.Services.AddHostedService<PaymentProcessorHealthCheck>();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/healthz");

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/payments-summary",
        async ([FromQuery(Name = "from")] DateTime from, [FromQuery(Name = "to")] DateTime to,
            [FromServices] PaymentProcessorDbContext db) =>
        {
            var logs = await db.PaymentRequests
                .Where(p => p.RequestedAt >= from && p.RequestedAt <= to)
                .ToListAsync();

            if (logs.Count == 0)
            {
                return TypedResults.Ok(new PaymentsSummary(new R(0, 0), new R(0, 0)));
            }

            var grouped = logs
                .GroupBy(e => e.Source)
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        totalRequests = g.Count(),
                        totalAmount = g.Sum(x => x.Amount)
                    });

            if (grouped.Count == 0)
            {
                return TypedResults.Ok(new PaymentsSummary(new R(0, 0), new R(0, 0)));
            }

            var def = grouped.ContainsKey("default")
                ? new R(grouped["default"].totalAmount, grouped["default"].totalRequests)
                : new R(0, 0);

            var f = grouped.ContainsKey("fallback")
                ? new R(grouped["fallback"].totalAmount, grouped["fallback"].totalRequests)
                : new R(0, 0);

            return TypedResults.Ok(new PaymentsSummary(def, f));
        })
    .WithName("payments-summary");

app.MapPost("/payments", async (PaymentsRequestDto requestDto, [FromServices] IRedisPublisher publisher) =>
    {
        await publisher.PublishAsync(requestDto);
        return TypedResults.Ok();
    })
    .WithName("Payments");

app.Run();
return;


void AddRefit(WebApplicationBuilder webApplicationBuilder)
{
    var paymentProcessorUrlDefault =
        webApplicationBuilder.Configuration.GetValue<string>("PAYMENT_PROCESSOR_URL_DEFAULT")
        ?? throw new ArgumentException("invalid payment processor url");

    var paymentProcessorUrlFallback =
        webApplicationBuilder.Configuration.GetValue<string>("PAYMENT_PROCESSOR_URL_FALLBACK") ??
        throw new ArgumentException("invalid payment processor url fallback");

    var backoffDelay = Backoff.DecorrelatedJitterBackoffV2(
        medianFirstRetryDelay: TimeSpan.FromSeconds(1.5),
        retryCount: 3);

    var fallbackBackoffDelay = Backoff.DecorrelatedJitterBackoffV2(
        medianFirstRetryDelay: TimeSpan.FromSeconds(1.5),
        retryCount: 1);

    var retryPolicy = HttpPolicyExtensions.HandleTransientHttpError().WaitAndRetryAsync(backoffDelay);
    var fallBackRetryPolicy = HttpPolicyExtensions.HandleTransientHttpError().WaitAndRetryAsync(fallbackBackoffDelay);

    // Refit Payment Processor
    webApplicationBuilder.Services.AddRefitClient<IPaymentDefaultProcessorApi>()
        .ConfigureHttpClient(c => c.BaseAddress = new Uri(paymentProcessorUrlDefault))
        .AddPolicyHandler(retryPolicy);

    webApplicationBuilder.Services.AddRefitClient<IPaymentFallbackProcessorApi>()
        .ConfigureHttpClient(c => c.BaseAddress = new Uri(paymentProcessorUrlFallback))
        .AddPolicyHandler(fallBackRetryPolicy);
}

void AddEntityFrameworkCore(WebApplicationBuilder webApplicationBuilder)
{
    webApplicationBuilder.Services.AddDbContext<PaymentProcessorDbContext>(opt =>
    {
        opt.UseNpgsql(webApplicationBuilder.Configuration.GetValue<string>("DB_CONNECTION_STRING"));
    });
}