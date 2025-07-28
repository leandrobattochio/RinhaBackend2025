using RinhaBackend.Api;
using RinhaBackend.Database;
using RinhaBackend.Database.Models;
using RinhaBackend.Dto;

namespace RinhaBackend.Messages;

public class MessageConsumerBackground(
    IMemoryPublisher memoryPublisher,
    IServiceProvider serviceProvider,
    IPaymentProcessorApi defaultProcessor,
    IPaymentProcessorFallbackApi fallbackProcessor) : BackgroundService
{
    private readonly int _workerCount = 4;
    private readonly HashSet<Guid> _processedMessages = [];

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
            if (_processedMessages.Contains(message.CorrelationId)) return;

            var requestedAt = DateTime.UtcNow;
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
                await SavePaymentToDatabase("fallback", message, requestedAt);
        }
        catch (Exception ex)
        {
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

        _processedMessages.Add(requestDto.CorrelationId);
    }
}