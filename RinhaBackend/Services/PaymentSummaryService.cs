using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using RinhaBackend.Database;
using RinhaBackend.Dto;

namespace RinhaBackend.Services;

public class PaymentSummaryService(IServiceProvider serviceProvider)
{
    public async Task<Ok<PaymentsSummary>> GetPaymentSummary(DateTime? from, DateTime? to)
    {
        if (from == null && to == null)
        {
            return TypedResults.Ok(new PaymentsSummary(new R(0, 0), new R(0, 0)));
        }
        
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentProcessorDbContext>();
        
        var logs = await db.PaymentRequests
            .Where(p => p.RequestedAt >= from && p.RequestedAt <= to)
            .ToListAsync();

        if (logs.Count == 0)
        {
            return TypedResults.Ok(new PaymentsSummary(new R(0, 0), new R(0, 0)));
        }

        var grouped = logs
            .GroupBy(e => e.Source)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    totalRequests = g.Count(),
                    totalAmount = g.Sum(x => x.Amount)
                });

        if (grouped.Count == 0)
        {
            return TypedResults.Ok(new PaymentsSummary(new R(0, 0), new R(0, 0)));
        }

        var def = grouped.ContainsKey("default")
            ? new R(grouped["default"].totalAmount, grouped["default"].totalRequests)
            : new R(0, 0);

        var f = grouped.ContainsKey("fallback")
            ? new R(grouped["fallback"].totalAmount, grouped["fallback"].totalRequests)
            : new R(0, 0);

        return TypedResults.Ok(new PaymentsSummary(def, f));
    }
}