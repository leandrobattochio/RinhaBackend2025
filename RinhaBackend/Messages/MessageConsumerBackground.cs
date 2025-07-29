using Microsoft.EntityFrameworkCore;
using RinhaBackend.Api;
using RinhaBackend.Database;
using RinhaBackend.Database.Models;
using RinhaBackend.Dto;

namespace RinhaBackend.Messages;

public class MessageConsumerBackground(
    IMemoryPublisher memoryPublisher,
    IServiceProvider serviceProvider,
    IPaymentProcessorApi defaultProcessor,
    ILogger<MessageConsumerBackground> logger,
    
    IPaymentProcessorFallbackApi fallbackProcessor) : BackgroundService
{
    private readonly int _workerCount = 4;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private async Task Consume(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var message in memoryPublisher.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessPayment(message);
            }
            catch (Exception ex)
            {
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = Enumerable.Range(0, _workerCount)
            .Select(workerId => Task.Run(() => Consume(workerId, stoppingToken), stoppingToken));

        return Task.WhenAll(tasks);
    }

    private async Task ProcessPayment(PaymentsRequestDto message)
    {
        try
        {
            // using (var scope = serviceProvider.CreateScope())
            // {
            //     var context = scope.ServiceProvider.GetRequiredService<PaymentProcessorDbContext>();
            //     var exists = await context.PaymentRequests.AnyAsync(c => c.CorrelationId == message.CorrelationId);
            //     if (exists) return;
            // }

            var requestedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var processorRequest =
                new PaymentProcessorRequest(message.CorrelationId, message.Amount, requestedAt.ToString("O"));

            var response = await defaultProcessor.ProcessPaymentRequestAsync(processorRequest);
            if (response.IsSuccessStatusCode)
            {
                await SavePaymentToDatabase("default", message, requestedAt);
                return;
            }

            var fallbackResponse = await fallbackProcessor.ProcessPaymentRequestAsync(processorRequest);
            if (fallbackResponse.IsSuccessStatusCode)
            {
                await SavePaymentToDatabase("fallback", message, requestedAt);
                return;
            }

            await memoryPublisher.PublishAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing payment request - resent to queue");
            await memoryPublisher.PublishAsync(message);
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