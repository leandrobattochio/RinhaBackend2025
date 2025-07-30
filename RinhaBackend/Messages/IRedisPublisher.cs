using RinhaBackend.Dto;

namespace RinhaBackend.Messages;

public interface IRedisPublisher
{
    Task PublishAsync(PaymentsRequestDto message);
}