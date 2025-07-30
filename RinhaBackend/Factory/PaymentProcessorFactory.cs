using Medallion.Threading;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using RinhaBackend.Api;
using StackExchange.Redis;

namespace RinhaBackend.Factory;

public class PaymentProcessorFactory(
    IDistributedCache distributedCache,
    ILogger<PaymentProcessorFactory> logger,
    IDistributedLockProvider distributedLockProvider,
    IPaymentDefaultProcessorApi defaultProcessor,
    IPaymentFallbackProcessorApi fallbackProcessor,
    IConnectionMultiplexer connectionMultiplexer) : IPaymentProcessorFactory
{
    private readonly IDatabase db = connectionMultiplexer.GetDatabase();

    public async Task<(IPaymentProcessorApi?, string)> GetProcessor()
    {
        var lockKey = "lock:health-check";
        var lastRunKey = "health:last-run";
        var interval = TimeSpan.FromSeconds(5);

        await using var handle = await distributedLockProvider.TryAcquireLockAsync(lockKey, TimeSpan.FromSeconds(10));
        if (handle is null)
        {
            return (defaultProcessor, "default");
        }

        PaymentServiceHealth defaultHealth;
        PaymentServiceHealth fallbackHealth;

        // Dentro do lock: verifique se já foi executado recentemente
        var lastRunStr = await db.StringGetAsync(lastRunKey);
        if (lastRunStr.HasValue && DateTimeOffset.TryParse(lastRunStr, out var lastRun))
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastRun < interval)
            {
                defaultHealth =
                    JsonConvert.DeserializeObject<PaymentServiceHealth>(await db.StringGetAsync("health:default"));
                fallbackHealth =
                    JsonConvert.DeserializeObject<PaymentServiceHealth>(await db.StringGetAsync("health:fallback"));

                return GetBestProcessor(defaultHealth, fallbackHealth);
            }
        }

        // Executa lógica de health check
        var currentTime = DateTimeOffset.UtcNow;

        defaultHealth = await defaultProcessor.GetServiceHealth();
        fallbackHealth = await fallbackProcessor.GetServiceHealth();

        await db.StringSetAsync("health:default", JsonConvert.SerializeObject(defaultHealth));
        await db.StringSetAsync("health:fallback", JsonConvert.SerializeObject(fallbackHealth));
        await db.StringSetAsync(lastRunKey, currentTime.ToString("O")); // RFC3339


        return GetBestProcessor(defaultHealth, fallbackHealth);
    }

    private (IPaymentProcessorApi?, string) GetBestProcessor(PaymentServiceHealth defaultHealth,
        PaymentServiceHealth fallbackHealth)
    {
        if (defaultHealth.Failing && fallbackHealth.Failing)
        {
            logger.LogInformation("os dois estão falhando");
            return (null, "none");
        }
            

        if (defaultHealth.Failing)
            return (fallbackProcessor, "fallback");
        
        return (defaultProcessor, "default");
    }
}