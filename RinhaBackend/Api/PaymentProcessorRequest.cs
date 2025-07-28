namespace RinhaBackend.Api;

public record PaymentProcessorRequest(Guid CorrelationId, decimal Amount, string RequestedAt);