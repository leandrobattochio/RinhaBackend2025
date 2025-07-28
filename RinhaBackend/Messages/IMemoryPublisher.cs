using System.Threading.Channels;
using RinhaBackend.Dto;

namespace RinhaBackend.Messages;

public interface IMemoryPublisher
{
    Task PublishAsync(PaymentsRequestDto message);
    ChannelReader<PaymentsRequestDto> Reader { get; }
}