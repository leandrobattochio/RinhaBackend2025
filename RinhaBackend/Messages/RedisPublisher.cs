using System.Globalization;
using RinhaBackend.Dto;
using StackExchange.Redis;

namespace RinhaBackend.Messages;

public class RedisPublisher(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisPublisher> logger)
    : IRedisPublisher
{
    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();

    public async Task PublishAsync(PaymentsRequestDto message)
    {
        try
        {
            await _database.StreamAddAsync("payments-stream", [
                new NameValueEntry("correlation-id", message.CorrelationId.ToString()),
                new NameValueEntry("amount", message.Amount.ToString(CultureInfo.InvariantCulture))
            ]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error publishing payments");
        }
    }
}