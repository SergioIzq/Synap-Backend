using Microsoft.Extensions.DependencyInjection;
using SergioIzq.Domain.Kernel.Interfaces;
using Synap.Domain;
using Synap.Shared.Application.BackgroundJobs;
using Synap.Shared.Application.Interfaces;
using Synap.Shared.Domain.ValueObjects.Ids;

namespace Synap.Application.Features.Notes;

/// <summary>Shared by CreateNoteCommand and QuickCaptureCommand - both create a note the same way.</summary>
internal static class NoteCreationSupport
{
    public static async Task<Note> CreateAndPersistAsync(
        INoteWriteRepository noteWriteRepository,
        IUnitOfWork unitOfWork,
        IBackgroundJobQueue backgroundJobQueue,
        Guid userId,
        NoteType type,
        string? title,
        string content,
        CancellationToken cancellationToken)
    {
        var note = Note.Create(UserId.CreateFromDatabase(userId), type, title, content);

        await noteWriteRepository.CreateAsync(note, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (type == NoteType.Bookmark)
        {
            EnqueueMetadataScrape(backgroundJobQueue, note.Id.Value, content);
        }

        return note;
    }

    private static void EnqueueMetadataScrape(IBackgroundJobQueue backgroundJobQueue, Guid noteId, string url)
    {
        backgroundJobQueue.Enqueue(async (services, cancellationToken) =>
        {
            var scraper = services.GetRequiredService<IBookmarkMetadataScraper>();
            var metadata = await scraper.ScrapeAsync(url, cancellationToken);
            if (metadata is null)
            {
                return;
            }

            var writeRepository = services.GetRequiredService<INoteWriteRepository>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            var note = await writeRepository.GetByIdAsync(noteId, cancellationToken);
            if (note is null)
            {
                // The note was deleted before the scrape finished - nothing to attach metadata to.
                return;
            }

            note.AttachMetadata(metadata);
            writeRepository.Update(note);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        });
    }
}
