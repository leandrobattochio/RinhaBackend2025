using MichelOliveira.Com.ReactiveLock.Core;
using MichelOliveira.Com.ReactiveLock.DependencyInjection;

namespace RinhaBackend;

public class CountingHandler(IReactiveLockTrackerFactory reactiveLockTrackerController) : DelegatingHandler
{
    private readonly IReactiveLockTrackerController _reactiveLockTrackerController = reactiveLockTrackerController.GetTrackerController(LockName.HTTP_LOCK);
    
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _reactiveLockTrackerController.IncrementAsync().ConfigureAwait(false);

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _reactiveLockTrackerController.DecrementAsync().ConfigureAwait(false);
        }
    }
}