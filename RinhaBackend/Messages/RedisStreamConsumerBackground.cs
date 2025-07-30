using System.Globalization;
using RinhaBackend.Api;
using RinhaBackend.Database;
using RinhaBackend.Database.Models;
using RinhaBackend.Dto;
using RinhaBackend.Factory;
using StackExchange.Redis;

namespace RinhaBackend.Messages;

public class RedisConsumerBackground(
    IRedisPublisher memoryPublisher,
    IConnectionMultiplexer connectionMultiplexer,
    IServiceProvider serviceProvider,
    IPaymentProcessorFactory paymentProcessorFactory,
    ILogger<MessageConsumerBackground> logger) : BackgroundService
{
    private readonly TimeProvider _timeProvider = TimeProvider.System;
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
                position: position, 250);

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

                await ProcessPayment(msg);
                await db.StreamAcknowledgeAsync(StreamName, GroupName, entry.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
                // NÃO dá ACK → permanece pendente
            }
        }
    }

    private async Task<bool> ProcessPayment(PaymentsRequestDto message)
    {
        try
        {
            var requestedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var processorRequest =
                new PaymentProcessorRequest(message.CorrelationId, message.Amount, requestedAt.ToString("O"));

            var bestProcessor = await paymentProcessorFactory.GetProcessor();

            // Os dois estão falhando
            if (bestProcessor.Item1 == null)
            {
                await memoryPublisher.PublishAsync(message);
                return false;
            }

            var response = await bestProcessor.Item1.ProcessPaymentRequestAsync(processorRequest);
            if (response.IsSuccessStatusCode)
            {
                await SavePaymentToDatabase(bestProcessor.Item2, message, requestedAt);
                return true;
            }

            await memoryPublisher.PublishAsync(message);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing payment request - resent to queue");
            await memoryPublisher.PublishAsync(message);
            return false;
        }
    }


    private async Task SavePaymentToDatabase(string source, PaymentsRequestDto requestDto, DateTime requestedAt)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentProcessorDbContext>();

        var entity = new PaymentRequest()
        {
            Amount = requestDto.Amount,
            CorrelationId = requestDto.CorrelationId,
            RequestedAt = requestedAt,
            Source = source
        };

        await context.PaymentRequests.AddAsync(entity);
        await context.SaveChangesAsync();
    }
}