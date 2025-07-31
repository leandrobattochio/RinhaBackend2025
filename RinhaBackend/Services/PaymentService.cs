using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using RinhaBackend.Api;
using RinhaBackend.Database;
using RinhaBackend.Database.Models;
using RinhaBackend.Dto;
using RinhaBackend.Factory;
using StackExchange.Redis;

namespace RinhaBackend.Services;

public class PaymentService(
    IServiceProvider serviceProvider,
    IConnectionMultiplexer connectionMultiplexer,
    IPaymentProcessorFactory paymentProcessorFactory,
    ILogger<PaymentService> logger)
{
    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    public async Task<Ok> PublishAsync(PaymentsRequestDto requestDto)
    {
        try
        {
            await _database.StreamAddAsync("payments-stream", [
                new NameValueEntry("correlation-id", requestDto.CorrelationId.ToString()),
                new NameValueEntry("amount", requestDto.Amount.ToString(CultureInfo.InvariantCulture))
            ], flags: CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error publishing payments");
        }

        return TypedResults.Ok();
    }

    public async Task<bool> ProcessPayment(PaymentsRequestDto message)
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
                await PublishAsync(message);
                return false;
            }

            var response = await bestProcessor.Item1.ProcessPaymentRequestAsync(processorRequest);
            if (response.IsSuccessStatusCode)
            {
                await SavePaymentToDatabase(bestProcessor.Item2, message, requestedAt);
                return true;
            }

            await PublishAsync(message);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing payment request - resent to queue");
            await PublishAsync(message);
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