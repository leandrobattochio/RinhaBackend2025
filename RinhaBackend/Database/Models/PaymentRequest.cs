using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RinhaBackend.Database.Models;

[Table("payment_request")]
public class PaymentRequest : BaseEntity
{
    [Column("correlationid")]
    public required Guid CorrelationId { get; init; }
    [Column("amount")]
    public required decimal Amount { get; init; }
    [Column("requestedat")]
    public required DateTime RequestedAt { get; init; }

    [StringLength(20), Column("source")] public required string Source { get; init; }
}

public abstract class BaseEntity
{
    [Column("id")]
    public int Id { get; private set; }
}