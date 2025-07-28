using System.ComponentModel.DataAnnotations;

namespace RinhaBackend.Dto;

public record PaymentsRequestDto(
    [Required] Guid CorrelationId,
    [Required, Range(0.01, (double)decimal.MaxValue)]
    decimal Amount);
    
public record PaymentsSummary(R Default, R Fallback);

public record R(decimal TotalAmount, int TotalRequests);