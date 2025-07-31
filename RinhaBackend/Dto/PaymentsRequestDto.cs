using System.ComponentModel.DataAnnotations;

namespace RinhaBackend.Dto;

public record PaymentsRequestDto(
    [Required] Guid CorrelationId,
    [Required, Range(0.01, (double)decimal.MaxValue)]
    decimal Amount);

public record PaymentsSummaryResponse(ProcessorSummary Default, ProcessorSummary Fallback);

public record ProcessorSummary(long TotalRequests, decimal TotalAmount);

public record PaymentSummaryDbResult(string Source, long TotalRequests, decimal TotalAmount);