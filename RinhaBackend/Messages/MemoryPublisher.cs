using System.Threading.Channels;
using RinhaBackend.Dto;

namespace RinhaBackend.Messages;

public class MemoryPublisher : IMemoryPublisher
{

    private readonly Channel<PaymentsRequestDto> _channel = Channel.CreateUnbounded<PaymentsRequestDto>();
    public ChannelReader<PaymentsRequestDto> Reader => _channel.Reader;

    public async Task PublishAsync(PaymentsRequestDto message)
    {
        await _channel.Writer.WriteAsync(message);
    }
}