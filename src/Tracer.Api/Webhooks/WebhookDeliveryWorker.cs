namespace Tracer.Api.Webhooks;

/// <summary>
/// Polls the outbox and sends whatever is due.
///
/// <para>
/// Polling, rather than being poked by the request that created the event,
/// because the outbox has to be drained on the way back up from a crash too:
/// deliveries queued before a restart are still owed, and nothing is going to
/// poke anyone about them. A poll finds them for free. The cost is up to one
/// interval of latency on an idle system, which for webhooks is nothing.
/// </para>
/// <para>
/// <b>This assumes one instance.</b> Two of these would both read the same due
/// rows and send them twice, because the claim is not atomic — nothing marks a
/// row as taken. Making it safe to run several means claiming a batch with an
/// atomic conditional update, or a row lock the sender holds. Deliveries are
/// at-least-once regardless, so a duplicate is not a correctness bug, but it is a
/// real limit and is written down rather than discovered.
/// </para>
/// </summary>
public sealed class WebhookDeliveryWorker(
    IServiceScopeFactory scopes,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A fresh scope per pass: the DbContext is scoped, and holding
                // one for the lifetime of the process would accumulate every
                // entity it ever tracked.
                using var scope = scopes.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<WebhookSender>();
                await sender.DeliverDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception exception)
            {
                // Never let one bad pass kill the loop. An unhandled exception
                // here stops the worker for the lifetime of the process, and
                // every webhook silently stops being delivered — with the app
                // still serving traffic and looking perfectly healthy.
                logger.LogError(exception, "Webhook delivery pass failed; continuing.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
