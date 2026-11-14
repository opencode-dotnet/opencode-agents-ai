using System.Threading.Channels;
using OpenCode.Agents.AI.Internal.Threading;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Agents.AI.Turns;

internal sealed class EventPump : IAsyncDisposable
{
    private readonly Channel<IEvent> _channel = Channel.CreateUnbounded<IEvent>();
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _read;

    private EventPump(EventsClient events, CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _read = ReadAsync(events);
    }

    public static EventPump Start(EventsClient events, CancellationToken cancellationToken) => new(events, cancellationToken);

    public ChannelReader<IEvent> Reader => _channel.Reader;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _lifetime.CancelOnWorkerAsync().ConfigureAwait(false);
            await _read.ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task ReadAsync(EventsClient events)
    {
        try
        {
            await foreach (var @event in events.SubscribeAsync(cancellationToken: _lifetime.Token).ConfigureAwait(false))
            {
                _channel.Writer.TryWrite(@event);
            }

            _channel.Writer.TryComplete();
        }
        catch (Exception error)
        {
            _channel.Writer.TryComplete(error);
        }
    }
}
