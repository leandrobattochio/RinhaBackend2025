using MichelOliveira.Com.ReactiveLock.Core;
using MichelOliveira.Com.ReactiveLock.DependencyInjection;
using Microsoft.AspNetCore.Http.HttpResults;
using RinhaBackend.Api;
using RinhaBackend.Database.Models;
using RinhaBackend.Dto;
using RinhaBackend.Messages;
using StackExchange.Redis;

namespace RinhaBackend.Services;

public class PaymentService(
    PaymentBatchInserter paymentBatchInserter,
    IMemoryPublisher memoryPublisher,
    IReactiveLockTrackerFactory lockFactory,
    IConnectionMultiplexer connectionMultiplexer,
    IPaymentDefaultProcessorApi defaultProcessorApi,
    ILogger<PaymentService> logger)
{
    private readonly IDatabase _database = connectionMultiplexer.GetDatabase();
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private readonly IReactiveLockTrackerState _reactiveLockTrackerState =
        lockFactory.GetTrackerState(LockName.SUMMARY_LOCK);

    public async Task<Ok> PublishAsync(PaymentsRequestDto requestDto)
    {
        try
        {
            await memoryPublisher.PublishAsync(requestDto);

            // await _database.StreamAddAsync("payments-stream", [
            //     new NameValueEntry("correlation-id", requestDto.CorrelationId.ToString()),
            //     new NameValueEntry("amount", requestDto.Amount.ToString(CultureInfo.InvariantCulture))
            // ], flags: CommandFlags.FireAndForget);
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

            await _reactiveLockTrackerState.WaitIfBlockedAsync().ConfigureAwait(false);
            var response = await defaultProcessorApi.ProcessPaymentRequestAsync(processorRequest);
            if (response.IsSuccessStatusCode)
            {
                await SavePaymentToDatabase("default", message, requestedAt);
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
        var entity = new PaymentRequest()
        {
            Amount = requestDto.Amount,
            CorrelationId = requestDto.CorrelationId,
            RequestedAt = requestedAt,
            Source = source
        };

        await paymentBatchInserter.Enqueue(entity);
    }
}