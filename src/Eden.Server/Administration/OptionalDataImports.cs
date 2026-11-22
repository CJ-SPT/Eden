using System.Threading.Channels;
using Eden.Infrastructure.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Eden.Server.Administration;

internal enum OptionalDataset
{
    Market,
    Traffic,
}

internal sealed record OptionalDataImportStatus(bool Running, string Message, bool Failed = false);

internal sealed class OptionalDataImports(
    Func<OptionalDataset, Action<string>, CancellationToken, Task> import,
    string trafficSnapshotPath,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private readonly Channel<OptionalDataset> _queue = Channel.CreateBounded<OptionalDataset>(1);
    private readonly string _trafficRequest = trafficSnapshotPath + ".import.pending";
    private readonly Lock _gate = new();
    private OptionalDataset? _running;
    private OptionalDataImportStatus? _market;
    private OptionalDataImportStatus? _traffic;

    internal (OptionalDataImportStatus? Market, OptionalDataImportStatus? Traffic) Status()
    {
        lock (_gate)
        {
            return (_market, _traffic);
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (File.Exists(_trafficRequest))
            {
                Enqueue(OptionalDataset.Traffic, "Resuming the unfinished traffic import…");
            }
        }
        return base.StartAsync(cancellationToken);
    }

    internal void Start(OptionalDataset dataset)
    {
        lock (_gate)
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                throw new InvalidOperationException("The server is shutting down.");
            }
            if (_running is not null)
            {
                throw new InvalidOperationException("An optional data import is already running.");
            }
            if (dataset == OptionalDataset.Traffic)
            {
                using var request = File.Open(_trafficRequest, FileMode.OpenOrCreate, FileAccess.Write);
                request.Flush(flushToDisk: true);
            }
            Enqueue(dataset, "Starting import…");
        }
    }

    private void Enqueue(OptionalDataset dataset, string message)
    {
        if (!_queue.Writer.TryWrite(dataset))
        {
            throw new InvalidOperationException("An optional data import is already queued.");
        }
        _running = dataset;
        SetStatus(dataset, new(true, message));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var dataset in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            OptionalDataImportStatus result;
            try
            {
                await import(dataset, ReportProgress, stoppingToken);
                result = new(false, "Import complete. Restart Eden to use the installed snapshot.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Startup reads the saved request and the importer reuses its completed cache entries.
                return;
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Optional data import ({dataset}) failed: {error}");
                string detail = error is InvalidDataException ? error.Message : "Check the server log for details and try again.";
                result = new(false, $"Import failed. The previous snapshot, if present, is still installed. {detail}", Failed: true);
            }
            if (dataset == OptionalDataset.Traffic)
            {
                try
                {
                    File.Delete(_trafficRequest);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ServerLog.Console.Error($"Could not clear the saved traffic import request at {_trafficRequest}: {error.Message}");
                    result = result with { Message = result.Message + " Eden could not clear the saved import request." };
                }
            }
            lock (_gate)
            {
                SetStatus(dataset, result);
                _running = null;
            }

            void ReportProgress(string message)
            {
                lock (_gate)
                {
                    SetStatus(dataset, new(true, message));
                }
            }
        }
    }

    private void SetStatus(OptionalDataset dataset, OptionalDataImportStatus status)
    {
        switch (dataset)
        {
            case OptionalDataset.Market:
                _market = status;
                break;
            case OptionalDataset.Traffic:
                _traffic = status;
                break;
        }
    }
}
