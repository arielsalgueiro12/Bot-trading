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
            var delayMs = mirrorService.GetRecommendedDelayMs();

            try
            {
                var status = mirrorService.GetStatus();
                if (status.Settings.Enabled)
                {
                    using var scope = scopeFactory.CreateScope();
                    await mirrorService.PollOnceAsync(scope.ServiceProvider, stoppingToken);
                    delayMs = mirrorService.GetRecommendedDelayMs();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CopyMirror cycle error");
                delayMs = Math.Min(45_000, Math.Max(delayMs, 8_000));
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
