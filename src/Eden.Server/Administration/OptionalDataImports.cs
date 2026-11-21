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

internal sealed class OptionalDataImports(OptionalDataImporter importer, IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly Channel<OptionalDataset> _queue = Channel.CreateBounded<OptionalDataset>(1);
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

    internal void Start(OptionalDataset dataset)
    {
        lock (_gate)
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                throw new InvalidOperationException("The server is shutting down.");
            }
            if (_running is not null || !_queue.Writer.TryWrite(dataset))
            {
                throw new InvalidOperationException("An optional data import is already running.");
            }
            _running = dataset;
            SetStatus(dataset, new(true, "Starting import…"));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var dataset in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            OptionalDataImportStatus result;
            try
            {
                await importer.Import(dataset, ReportProgress, stoppingToken);
                result = new(false, "Import complete. Restart Eden to use the installed snapshot.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Optional data import ({dataset}) failed: {error}");
                string detail = error is InvalidDataException ? error.Message : "Check the server log for details and try again.";
                result = new(false, $"Import failed. The previous snapshot, if present, is still installed. {detail}", Failed: true);
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
