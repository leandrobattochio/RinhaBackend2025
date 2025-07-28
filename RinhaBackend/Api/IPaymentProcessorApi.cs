using Refit;

namespace RinhaBackend.Api;

public interface IPaymentProcessorApi
{
    [Post("/payments")]
    Task<HttpResponseMessage> ProcessPaymentRequestAsync(PaymentProcessorRequest request);
}