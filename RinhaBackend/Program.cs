using System.Data;
using System.Net;
using Medallion.Threading;
using Medallion.Threading.Redis;
using MichelOliveira.Com.ReactiveLock.Distributed.Redis;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Refit;
using RinhaBackend.Api;
using Scalar.AspNetCore;
using Polly.Extensions.Http;
using RinhaBackend;
using RinhaBackend.Dto;
using RinhaBackend.Messages;
using RinhaBackend.Services;
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

builder.Services.InitializeDistributedRedisReactiveLock(Dns.GetHostName());

builder.Services.AddDistributedRedisReactiveLock(LockName.HTTP_LOCK);
builder.Services.AddDistributedRedisReactiveLock(LockName.POSTGRES_LOCK);
builder.Services.AddDistributedRedisReactiveLock(LockName.SUMMARY_LOCK, [
    async(sp) => {
        var summary = sp.GetRequiredService<PaymentSummaryService>();
        await summary.FlushWhileGateBlockedAsync();
    }
]);

builder.Services.AddSingleton<IMemoryPublisher, MemoryPublisher>();
builder.Services.AddTransient<CountingHandler>();
builder.Services.AddSingleton<PaymentSummaryService>();
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddSingleton<PaymentBatchInserter>();

builder.Services.AddTransient<IDbConnection>(_ =>
    new NpgsqlConnection(builder.Configuration.GetValue<string>("DB_CONNECTION_STRING")));

AddRefit(builder);

builder.Services.AddHostedService<MessageConsumerBackground>();
// builder.Services.AddHostedService<RedisConsumerBackground>();

builder.Services.AddHealthChecks();

var app = builder.Build();
await app.UseDistributedRedisReactiveLockAsync();
app.MapHealthChecks("/healthz");

// Configure the HTTP request pipeline.
app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/payments-summary",
    async ([FromQuery(Name = "from")] DateTime? from, [FromQuery(Name = "to")] DateTime? to,
            [FromServices] PaymentSummaryService paymentSummaryService) =>
        await paymentSummaryService.GetPaymentSummary(from, to));

app.MapPost("/payments",
    (HttpContext context, [FromServices] PaymentService paymentService) =>
    paymentService.PublishAsync(context));

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
        medianFirstRetryDelay: TimeSpan.FromSeconds(1),
        retryCount: 3);

    var fallbackBackoffDelay = Backoff.DecorrelatedJitterBackoffV2(
        medianFirstRetryDelay: TimeSpan.FromSeconds(1.5),
        retryCount: 1);

    var retryPolicy = HttpPolicyExtensions.HandleTransientHttpError().WaitAndRetryAsync(backoffDelay);
    var fallBackRetryPolicy = HttpPolicyExtensions.HandleTransientHttpError().WaitAndRetryAsync(fallbackBackoffDelay);

    // Refit Payment Processor
    webApplicationBuilder.Services.AddRefitClient<IPaymentDefaultProcessorApi>()
        .ConfigureHttpClient(c => c.BaseAddress = new Uri(paymentProcessorUrlDefault))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            MaxConnectionsPerServer = int.MaxValue,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        .AddHttpMessageHandler<CountingHandler>();
    //.AddPolicyHandler(retryPolicy);

    webApplicationBuilder.Services.AddRefitClient<IPaymentFallbackProcessorApi>()
        .ConfigureHttpClient(c => c.BaseAddress = new Uri(paymentProcessorUrlFallback))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            MaxConnectionsPerServer = int.MaxValue,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        .AddHttpMessageHandler<CountingHandler>()
        .AddPolicyHandler(fallBackRetryPolicy);
}