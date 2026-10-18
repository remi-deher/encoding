using System.Threading.Channels;
using Encoding.Contracts.Logging;
using Encoding.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Logging;

/// <summary>
/// Récepteur asynchrone pour la persistance locale bornée des logs de niveau Warning et supérieur.
/// </summary>
public sealed class SystemLogDbSink : ISystemLogSink, IHostedService, IAsyncDisposable
{
    private readonly SystemLogRepository _repository;
    private readonly ILogger<SystemLogDbSink> _logger;
    private readonly Channel<SystemLogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;
    private int _writesSinceLastPrune;

    public SystemLogDbSink(SystemLogRepository repository, ILogger<SystemLogDbSink> logger)
    {
        _repository = repository;
        _logger = logger;
        _channel = Channel.CreateBounded<SystemLogEntry>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask EmitAsync(SystemLogEntry entry, CancellationToken ct = default)
    {
        if (entry.Level >= SystemLogLevel.Warning)
        {
            _channel.Writer.TryWrite(entry);
        }
        return ValueTask.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _processingTask = Task.Run(ProcessQueueAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        if (_processingTask is not null)
        {
            try
            {
                await _processingTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur lors de l'arrêt du service de persistance des logs");
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        var token = _cts.Token;

        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var entry))
            {
                try
                {
                    await _repository.InsertAsync(entry, token).ConfigureAwait(false);
                    _writesSinceLastPrune++;

                    if (_writesSinceLastPrune >= 100)
                    {
                        _writesSinceLastPrune = 0;
                        await _repository.PruneAsync(5000, TimeSpan.FromDays(7), token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Ne doit pas interrompre la boucle
                    _logger.LogError(ex, "Erreur lors de la persistance SQLite du log système {LogId}", entry.Id);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (_processingTask is not null)
        {
            try
            {
                await _processingTask;
            }
            catch { }
        }
    }
}
