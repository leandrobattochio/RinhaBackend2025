using Refit;

namespace RinhaBackend.Api;

public interface IPaymentFallbackProcessorApi : IPaymentProcessorApi
{

}

public interface IPaymentProcessorApi
{
    [Post("/payments")]
    Task<HttpResponseMessage> ProcessPaymentRequestAsync(PaymentProcessorRequest request);
    
    [Get("/payments/service-health")]
    Task<PaymentServiceHealth> GetServiceHealth();
}