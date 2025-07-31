using RinhaBackend.Services;

namespace RinhaBackend.Messages;

public class MessageConsumerBackground(
    IMemoryPublisher memoryPublisher,
    PaymentService paymentService) : BackgroundService
{
    private readonly int _workerCount = 4;

    private async Task Consume(int workerId, CancellationToken stoppingToken)
    {
        await foreach (var message in memoryPublisher.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await paymentService.ProcessPayment(message);
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
}