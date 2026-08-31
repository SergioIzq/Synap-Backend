namespace Synap.Shared.Application.BackgroundJobs;

/// <summary>
/// In-process background queue (see design.md Decision 8) - not Hangfire, which needs the
/// MySQL-only SergioIzq.Infrastructure.Kernel. A job receives a fresh DI scope's
/// IServiceProvider (never the enqueuing request's scope, which is gone by the time the job
/// runs) so it can resolve its own repositories/UnitOfWork. Queued work is lost on process
/// restart - accepted for v1's scale.
/// </summary>
public interface IBackgroundJobQueue
{
    void Enqueue(Func<IServiceProvider, CancellationToken, Task> job);
}
