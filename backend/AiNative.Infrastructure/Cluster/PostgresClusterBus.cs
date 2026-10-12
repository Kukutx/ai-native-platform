using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AiNative.Infrastructure.Cluster;

/// <summary>
/// 实例之间广播的信封。正文超过 NOTIFY 上限时存进 cluster_messages，信封里只带 <see cref="Ref"/>。
/// </summary>
internal sealed record ClusterEnvelope(Guid Origin, string? Topic = null, JsonElement? Payload = null, Guid? Ref = null);

/// <summary>
/// 基于 PostgreSQL LISTEN/NOTIFY 的实例间广播，不引入额外的消息中间件。
/// 发送走连接池、批量提交；接收占用一条不进连接池的长连接，断开后退避重连（断开期间的消息会丢失，见 <see cref="IClusterBus"/>）。
/// 只有一个实例时不发送，单实例部署没有额外开销。
/// </summary>
internal sealed class PostgresClusterBus : BackgroundService, IClusterBus
{
    public const string Channel = "ainative_cluster";

    /// <summary>
    /// 超长正文在表里保留的时间，过期后由实例登记服务清理。
    /// </summary>
    public static readonly TimeSpan MessageRetention = TimeSpan.FromMinutes(10);

    // NOTIFY 载荷上限是 8000 字节，留出余量。
    private const int MaxInlineBytes = 7000;
    private const int MaxBatch = 200;

    private static readonly TimeSpan[] ReconnectDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly string _connectionString;
    private readonly string _listenConnectionString;
    private readonly ClusterNode _node;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostgresClusterBus> _logger;
    private readonly Channel<ClusterEnvelope> _outbox = System.Threading.Channels.Channel.CreateBounded<ClusterEnvelope>(
        new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<string> _inbox = System.Threading.Channels.Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });
    private Dictionary<string, IClusterMessageHandler[]>? _handlers;
    private int _dropping;

    public PostgresClusterBus(string connectionString, ClusterNode node, IServiceProvider services, TimeProvider clock, ILogger<PostgresClusterBus> logger)
    {
        _connectionString = connectionString;
        _listenConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 15 }.ConnectionString;
        _node = node;
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    public void Publish<T>(string topic, T payload)
    {
        if (!_node.HasPeers && topic != ClusterTopics.InstanceJoined)
        {
            return;
        }

        if (!_outbox.Writer.TryWrite(new ClusterEnvelope(_node.InstanceId, topic, PlatformJson.ToElement(payload))))
        {
            if (Interlocked.Exchange(ref _dropping, 1) == 0)
            {
                _logger.LogWarning("Cluster outbox is full; dropping broadcasts until the database is reachable again");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listen = Task.Run(() => ListenAsync(stoppingToken), CancellationToken.None);
        var dispatch = Task.Run(() => DispatchAsync(stoppingToken), CancellationToken.None);
        await SendLoopAsync(stoppingToken).ConfigureAwait(false);
        await Task.WhenAll(listen, dispatch).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ClusterEnvelope>(MaxBatch);
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await SendPendingAsync(batch, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // 停止前把已经排队的消息发出去（例如调度器停止时放回队列的运行），尽量不丢。
        using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        try
        {
            await SendPendingAsync(batch, flush.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush cluster broadcasts on shutdown");
        }
    }

    private async Task SendPendingAsync(List<ClusterEnvelope> batch, CancellationToken cancellationToken)
    {
        while (_outbox.Reader.TryPeek(out _))
        {
            batch.Clear();
            while (batch.Count < MaxBatch && _outbox.Reader.TryRead(out var envelope))
            {
                batch.Add(envelope);
            }

            try
            {
                await SendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _dropping, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // 尽力而为：数据库不可达时丢弃这一批，接收方有缓存过期和定时轮询兜底。
                _logger.LogWarning(ex, "Failed to broadcast {Count} cluster messages", batch.Count);
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendBatchAsync(List<ClusterEnvelope> batch, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlBatch(connection);
        foreach (var envelope in batch)
        {
            var json = JsonSerializer.Serialize(envelope, PlatformJson.Options);
            if (Encoding.UTF8.GetByteCount(json) > MaxInlineBytes)
            {
                var id = Guid.CreateVersion7();
                command.BatchCommands.Add(Command(
                    "INSERT INTO cluster_messages (id, payload, created_at) VALUES ($1, $2, now())",
                    new NpgsqlParameter { Value = id },
                    new NpgsqlParameter { Value = json }));
                json = JsonSerializer.Serialize(new ClusterEnvelope(envelope.Origin, Ref: id), PlatformJson.Options);
            }

            command.BatchCommands.Add(Command($"SELECT pg_notify('{Channel}', $1)", new NpgsqlParameter { Value = json }));
        }

        // 一批在同一个隐式事务里执行：超长正文与通知一起提交，接收方收到通知时一定读得到。
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlBatchCommand Command(string sql, params NpgsqlParameter[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        return command;
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_listenConnectionString);
                connection.Notification += (_, e) => _inbox.Writer.TryWrite(e.Payload);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                await using (var command = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                }

                attempt = 0;
                while (true)
                {
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var delay = ReconnectDelays[Math.Min(attempt++, ReconnectDelays.Length - 1)];
                _logger.LogWarning(ex, "Cluster listener disconnected; reconnecting in {Delay}", delay);
                try
                {
                    await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var payload in _inbox.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(payload, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Failed to handle cluster message");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<ClusterEnvelope>(payload, PlatformJson.Options);
        if (envelope is null || envelope.Origin == _node.InstanceId)
        {
            return;
        }

        // 收到别的实例的消息，说明有人在线，本实例也要开始广播。
        _node.SetHasPeers(true);
        if (envelope.Ref is { } id)
        {
            envelope = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return;
            }
        }

        if (envelope.Topic is not { } topic || envelope.Payload is not { } body || !Handlers().TryGetValue(topic, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers)
        {
            await handler.HandleAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ClusterEnvelope?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT payload FROM cluster_messages WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<ClusterEnvelope>(json, PlatformJson.Options)
            : null;
    }

    // 处理器在首次收到消息时才解析：它们依赖的实时推送等服务可能反过来依赖本总线。
    private Dictionary<string, IClusterMessageHandler[]> Handlers()
        => _handlers ??= _services.GetServices<IClusterMessageHandler>()
            .GroupBy(h => h.Topic, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
}
