using System.Data;
using Dapper;
using MichelOliveira.Com.ReactiveLock.DependencyInjection;
using Microsoft.AspNetCore.Http.HttpResults;
using RinhaBackend.Dto;

namespace RinhaBackend.Services;

public class PaymentSummaryService(
    IReactiveLockTrackerFactory lockFactory,
    PaymentBatchInserter batchInserter,
    IDbConnection dbConnection)
{
    public async Task<Ok<PaymentsSummaryResponse>> GetPaymentSummary(DateTime? from, DateTime? to)
    {
        from ??= DateTimeOffset.UtcNow.AddMinutes(-1).UtcDateTime;
        to ??= DateTimeOffset.UtcNow.UtcDateTime;

        var paymentsLock = lockFactory.GetTrackerController(LockName.SUMMARY_LOCK);
        await paymentsLock.IncrementAsync().ConfigureAwait(false);

        var postgresChannelBlockingGate = lockFactory.GetTrackerState(LockName.POSTGRES_LOCK);
        var channelBlockingGate = lockFactory.GetTrackerState(LockName.HTTP_LOCK);

        try
        {
            await WaitWithTimeoutAsync(async () =>
            {
                await postgresChannelBlockingGate.WaitIfBlockedAsync().ConfigureAwait(false);
                await channelBlockingGate.WaitIfBlockedAsync().ConfigureAwait(false);
            }, timeout: TimeSpan.FromSeconds(1.3)).ConfigureAwait(false);


            const string sql = """

                               SELECT source,
                               COUNT(*) AS TotalRequests,
                               SUM(amount) AS TotalAmount
                               FROM payment_request
                               WHERE (@from IS NULL OR requestedat >= @from)
                               AND (@to IS NULL OR requestedat <= @to)
                               GROUP BY source;
                                           
                               """;
            List<PaymentSummaryDbResult> result =
                [.. await dbConnection.QueryAsync<PaymentSummaryDbResult>(sql, new { from, to })];

            var defaultResult = result.FirstOrDefault(r => r.Source == "default") ??
                                new PaymentSummaryDbResult("default", 0, 0);
            var fallbackResult = result.FirstOrDefault(r => r.Source == "fallback") ??
                                 new PaymentSummaryDbResult("fallback", 0, 0);

            var response = new PaymentsSummaryResponse(
                new ProcessorSummary(defaultResult.TotalRequests, defaultResult.TotalAmount),
                new ProcessorSummary(fallbackResult.TotalRequests, fallbackResult.TotalAmount)
            );

            return TypedResults.Ok(response);
        }
        finally
        {
            await paymentsLock.DecrementAsync().ConfigureAwait(false);
        }
    }

    public async Task FlushWhileGateBlockedAsync()
    {
        var state = lockFactory.GetTrackerState(LockName.SUMMARY_LOCK);

        await state.WaitIfBlockedAsync(
            whileBlockedLoopDelay: TimeSpan.FromMilliseconds(10),
            whileBlockedAsync: async () =>
            {
                try
                {
                    await batchInserter.FlushQueueAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                }
            }).ConfigureAwait(false);
    }

    private async Task<bool> WaitWithTimeoutAsync(Func<Task> taskFactory, TimeSpan timeout)
    {
        var task = taskFactory();
        var timeoutTask = Task.Delay(timeout);
        var completedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

        if (completedTask == timeoutTask)
            return false;


        await task.ConfigureAwait(false);
        return true;
    }
}