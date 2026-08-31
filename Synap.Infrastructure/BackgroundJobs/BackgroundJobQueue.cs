using Synap.Shared.Application.BackgroundJobs;
using System.Threading.Channels;

namespace Synap.Infrastructure.BackgroundJobs;

public sealed class BackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<Func<IServiceProvider, CancellationToken, Task>> _channel =
        Channel.CreateUnbounded<Func<IServiceProvider, CancellationToken, Task>>();

    public void Enqueue(Func<IServiceProvider, CancellationToken, Task> job)
        => _channel.Writer.TryWrite(job);

    public ChannelReader<Func<IServiceProvider, CancellationToken, Task>> Reader => _channel.Reader;
}
