namespace TradingBots.App.Services.CopyMirror;

public sealed class CopyMirrorHostedService(
    ICopyMirrorService mirrorService,
    IServiceScopeFactory scopeFactory,
    ILogger<CopyMirrorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CopyMirror hosted service started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = mirrorService.GetStatus();
            var delayMs = status.Settings.Enabled
                ? Math.Clamp(status.Settings.PollIntervalMs, 1000, 30_000)
                : 3000;

            // Backoff suave si el poll falla seguido (rate-limit / busy).
            if (!string.IsNullOrWhiteSpace(status.LastError) && status.Settings.Enabled)
            {
                delayMs = Math.Min(15_000, delayMs + 1500);
            }

            try
            {
                if (status.Settings.Enabled)
                {
                    using var scope = scopeFactory.CreateScope();
                    await mirrorService.PollOnceAsync(scope.ServiceProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CopyMirror cycle error");
            }

            try
            {
                await Task.Delay(delayMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
