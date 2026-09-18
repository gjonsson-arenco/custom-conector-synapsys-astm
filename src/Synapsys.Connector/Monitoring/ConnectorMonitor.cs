using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Synapsys.Connector.Monitoring;

/// <summary>
/// Bus de monitoreo: recibe comunicacion cruda y eventos semanticos y los difunde a todos los
/// suscriptores (los WebSockets del front). Guarda un buffer reciente para poblar la vista al
/// conectarse. Todo en memoria; no persiste.
/// </summary>
public interface IConnectorMonitor
{
    void PublishComms(CommsMessage message);

    void PublishEvent(ConnectorEvent connectorEvent);

    IMonitorSubscription<CommsMessage> SubscribeComms();

    IMonitorSubscription<ConnectorEvent> SubscribeEvents();
}

/// <summary>Suscripcion a un stream del monitor. Al liberarla se da de baja del bus.</summary>
public interface IMonitorSubscription<T> : IDisposable
{
    ChannelReader<T> Reader { get; }

    IReadOnlyList<T> Recent { get; }
}

public sealed class ConnectorMonitor : IConnectorMonitor
{
    private readonly Broadcaster<CommsMessage> _comms = new(recentCapacity: 200);
    private readonly Broadcaster<ConnectorEvent> _events = new(recentCapacity: 200);

    public void PublishComms(CommsMessage message) => _comms.Publish(message);

    public void PublishEvent(ConnectorEvent connectorEvent) => _events.Publish(connectorEvent);

    public IMonitorSubscription<CommsMessage> SubscribeComms() => _comms.Subscribe();

    public IMonitorSubscription<ConnectorEvent> SubscribeEvents() => _events.Subscribe();

    /// <summary>Difusor generico: buffer reciente acotado + canales bounded por suscriptor (descarta el mas viejo).</summary>
    private sealed class Broadcaster<T>
    {
        private readonly ConcurrentDictionary<Guid, Channel<T>> _subscribers = new();
        private readonly Queue<T> _recent = new();
        private readonly int _recentCapacity;
        private readonly Lock _recentLock = new();

        public Broadcaster(int recentCapacity) => _recentCapacity = recentCapacity;

        public void Publish(T item)
        {
            lock (_recentLock)
            {
                _recent.Enqueue(item);
                while (_recent.Count > _recentCapacity)
                {
                    _recent.Dequeue();
                }
            }

            foreach (var channel in _subscribers.Values)
            {
                channel.Writer.TryWrite(item);
            }
        }

        public IMonitorSubscription<T> Subscribe()
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(500)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

            _subscribers[id] = channel;

            T[] recent;
            lock (_recentLock)
            {
                recent = _recent.ToArray();
            }

            return new Subscription(channel.Reader, recent, () =>
            {
                if (_subscribers.TryRemove(id, out var removed))
                {
                    removed.Writer.TryComplete();
                }
            });
        }

        private sealed class Subscription(ChannelReader<T> reader, IReadOnlyList<T> recent, Action onDispose)
            : IMonitorSubscription<T>
        {
            public ChannelReader<T> Reader { get; } = reader;

            public IReadOnlyList<T> Recent { get; } = recent;

            public void Dispose() => onDispose();
        }
    }
}
