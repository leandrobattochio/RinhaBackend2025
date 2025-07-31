using System.Globalization;
using RinhaBackend.Dto;
using RinhaBackend.Services;
using StackExchange.Redis;

namespace RinhaBackend.Messages;

public class RedisConsumerBackground(
    IConnectionMultiplexer connectionMultiplexer,
    PaymentService paymentService,
    ILogger<RedisConsumerBackground> logger) : BackgroundService
{
    private const string StreamName = "payments-stream";
    private const string GroupName = "payments-group";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = connectionMultiplexer.GetDatabase();

        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamName, GroupName, "0-0", true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = await ReadMessagesAsync(db, ">", stoppingToken);
                await HandleMessagesAsync(db, pending);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in consuming messages");
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task<StreamEntry[]> ReadMessagesAsync(IDatabase db, string position, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var entries = await db.StreamReadGroupAsync(
                key: StreamName,
                groupName: GroupName,
                consumerName: "_consumerName",
                position: position, 500);

            if (entries != null && entries.Length > 0)
                return entries;

            await Task.Delay(1000, ct);
        }

        return [];
    }

    private async Task HandleMessagesAsync(IDatabase db, StreamEntry[] entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                var fields = entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

                var msg = new PaymentsRequestDto(
                    CorrelationId: Guid.Parse(fields["correlation-id"]),
                    Amount: decimal.Parse(fields["amount"], CultureInfo.InvariantCulture)
                );

                await paymentService.ProcessPayment(msg);
                await db.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
                // NÃO dá ACK → permanece pendente
            }
        }
    }
}