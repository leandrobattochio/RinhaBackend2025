using System.Collections.Concurrent;
using System.Data;
using System.Text;
using Dapper;
using MichelOliveira.Com.ReactiveLock.Core;
using MichelOliveira.Com.ReactiveLock.DependencyInjection;
using Npgsql;
using RinhaBackend.Database.Models;

namespace RinhaBackend.Services;

public class PaymentBatchInserter
{
    private readonly ConcurrentQueue<PaymentRequest> _paymentQueue = new();
    private readonly IDbConnection _dbConnection;
    private readonly IReactiveLockTrackerController _reactiveLockTrackerController;

    public PaymentBatchInserter(IDbConnection dbConnection, IReactiveLockTrackerFactory lockFactory)
    {
        _reactiveLockTrackerController = lockFactory.GetTrackerController(LockName.POSTGRES_LOCK);
        _dbConnection = dbConnection;
    }

    public async Task<int> Enqueue(PaymentRequest paymentRequest)
    {
        await _reactiveLockTrackerController.IncrementAsync().ConfigureAwait(false);
        //await SingleInsert(paymentRequest);
        _paymentQueue.Enqueue(paymentRequest);

        if (_paymentQueue.Count >= 100)
        {
            return await FlushQueueAsync().ConfigureAwait(false);
        }

        return 0;
    }

    public async Task SingleInsert(PaymentRequest paymentRequest)
    {
        await _reactiveLockTrackerController.IncrementAsync().ConfigureAwait(false);

        if (_dbConnection is not NpgsqlConnection npgsqlConn)
            throw new InvalidOperationException("DbConnection must be an NpgsqlConnection.");

        var connectionString = npgsqlConn.ConnectionString;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = new StringBuilder();
        var parameters = new DynamicParameters();

        sql.AppendLine(
            "INSERT INTO payment_request (correlationid, amount, requestedat, source) VALUES (@CorrelationId, @Amount, @RequestedAt, @Source);");
        parameters.Add("CorrelationId", paymentRequest.CorrelationId);
        parameters.Add("Amount", paymentRequest.Amount);
        parameters.Add("RequestedAt", paymentRequest.RequestedAt);
        parameters.Add("Source", paymentRequest.Source);

        await conn.ExecuteAsync(sql.ToString(), parameters);

        await _reactiveLockTrackerController.DecrementAsync().ConfigureAwait(false);
    }

    public async Task<int> FlushQueueAsync()
    {
        if (_dbConnection is not NpgsqlConnection npgsqlConn)
            throw new InvalidOperationException("DbConnection must be an NpgsqlConnection.");

        var connectionString = npgsqlConn.ConnectionString;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var totalInserted = 0;
        while (!_paymentQueue.IsEmpty)
        {
            var batch = new List<PaymentRequest>(100);
            while (batch.Count < 100 && _paymentQueue.TryDequeue(out var item))
                batch.Add(item);

            if (batch.Count == 0)
                break;

            var sql = new StringBuilder();
            var parameters = new DynamicParameters();

            sql.AppendLine("INSERT INTO payment_request (correlationid, amount, requestedat, source) VALUES ");

            for (var i = 0; i < batch.Count; i++)
            {
                var p = batch[i];
                var suffix = i.ToString();

                sql.AppendLine(
                    $"(@CorrelationId{suffix}, @Amount{suffix}, @RequestedAt{suffix}, @Source{suffix}){(i < batch.Count - 1 ? "," : ";")}");

                parameters.Add($"CorrelationId{suffix}", p.CorrelationId);
                parameters.Add($"Source{suffix}", p.Source);
                parameters.Add($"Amount{suffix}", p.Amount);
                parameters.Add($"RequestedAt{suffix}", p.RequestedAt);
            }

            await conn.ExecuteAsync(sql.ToString(), parameters);
            totalInserted += batch.Count;
            await _reactiveLockTrackerController.DecrementAsync(batch.Count).ConfigureAwait(false);
        }

        return totalInserted;
    }
}