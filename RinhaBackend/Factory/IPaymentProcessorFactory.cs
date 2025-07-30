using RinhaBackend.Api;

namespace RinhaBackend.Factory;

public interface IPaymentProcessorFactory
{
    Task<(IPaymentProcessorApi?, string)> GetProcessor();
}