using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Memory.Queries;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Memory;

/// <summary>assistant-agent-foundations task 5.1 - specs/assistant-memory rules, with in-memory fakes.</summary>
public class MemoryHandlersTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly FakeMemoryRepository _memory = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeUserContext _context = new(Me);

    private MemoryEntry Seed(Guid owner, string text) => _memory.Add(MemoryEntry.Create(UserId.CreateFromDatabase(owner), text).Value);

    // ---- Domain rules ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_is_rejected(string? text)
    {
        var result = MemoryEntry.Create(UserId.CreateFromDatabase(Me), text);

        Assert.True(result.IsFailure);
        Assert.Equal(MemoryEntry.EmptyText.Message, result.Error.Message);
    }

    [Fact]
    public void Text_is_trimmed_and_limited_to_200_characters()
    {
        Assert.Equal("prefiero respuestas cortas", MemoryEntry.Create(UserId.CreateFromDatabase(Me), "  prefiero respuestas cortas ").Value.Text);
        Assert.True(MemoryEntry.Create(UserId.CreateFromDatabase(Me), new string('a', 200)).IsSuccess);

        var tooLong = MemoryEntry.Create(UserId.CreateFromDatabase(Me), new string('a', 201));

        Assert.True(tooLong.IsFailure);
        Assert.Equal(MemoryEntry.TextTooLong.Message, tooLong.Error.Message);
    }

    // ---- Add ----

    [Fact]
    public async Task Add_stores_the_entry_for_the_current_user()
    {
        var result = await new AddMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new AddMemoryEntryCommand("uso Ubuntu"), default);

        Assert.True(result.IsSuccess);
        var stored = Assert.Single(_memory.Entries);
        Assert.Equal((Me, "uso Ubuntu"), (stored.UserId.Value, stored.Text));
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Add_rejects_a_26th_entry_and_keeps_the_memory_unchanged()
    {
        for (var i = 0; i < MemoryEntry.MaxEntriesPerUser; i++)
        {
            Seed(Me, $"hecho {i}");
        }

        Seed(Other, "no cuenta para mí");

        var result = await new AddMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new AddMemoryEntryCommand("uno más"), default);

        Assert.True(result.IsFailure);
        Assert.Equal(MemoryEntry.MemoryFull.Message, result.Error.Message);
        Assert.Equal(MemoryEntry.MaxEntriesPerUser, _memory.Entries.Count(e => e.UserId.Value == Me));
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Add_rejects_invalid_text_without_saving()
    {
        var result = await new AddMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new AddMemoryEntryCommand(" "), default);

        Assert.True(result.IsFailure);
        Assert.Empty(_memory.Entries);
    }

    // ---- Update / Delete ----

    [Fact]
    public async Task Update_replaces_the_text()
    {
        var entry = Seed(Me, "antes");

        var result = await new UpdateMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new UpdateMemoryEntryCommand(entry.Id.Value, "después"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("después", entry.Text);
    }

    [Fact]
    public async Task Update_with_invalid_text_keeps_the_old_one()
    {
        var entry = Seed(Me, "antes");

        var result = await new UpdateMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new UpdateMemoryEntryCommand(entry.Id.Value, new string('x', 201)), default);

        Assert.True(result.IsFailure);
        Assert.Equal("antes", entry.Text);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Another_users_entry_is_treated_as_missing()
    {
        var theirs = Seed(Other, "de otro");

        var updated = await new UpdateMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new UpdateMemoryEntryCommand(theirs.Id.Value, "hackeado"), default);
        var deleted = await new DeleteMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new DeleteMemoryEntryCommand(theirs.Id.Value), default);

        Assert.Equal(MemoryEntry.NotFound.Message, updated.Error.Message);
        Assert.Equal(MemoryEntry.NotFound.Message, deleted.Error.Message);
        Assert.Equal("de otro", theirs.Text);
        Assert.Contains(theirs, _memory.Entries);
    }

    [Fact]
    public async Task Delete_removes_the_entry()
    {
        var entry = Seed(Me, "borrar");

        var result = await new DeleteMemoryEntryCommandHandler(_memory, _unitOfWork, _context).Handle(new DeleteMemoryEntryCommand(entry.Id.Value), default);

        Assert.True(result.IsSuccess);
        Assert.Empty(_memory.Entries);
    }

    [Fact]
    public async Task DeleteAll_only_removes_the_current_users_entries()
    {
        Seed(Me, "uno");
        Seed(Me, "dos");
        var theirs = Seed(Other, "de otro");

        await new DeleteAllMemoryCommandHandler(_memory, _context).Handle(new DeleteAllMemoryCommand(), default);

        Assert.Equal([theirs], _memory.Entries);
    }

    [Fact]
    public async Task List_returns_only_own_entries_with_the_limits()
    {
        Seed(Me, "mío");
        Seed(Other, "de otro");

        var result = await new ListMemoryQueryHandler(_memory, _context).Handle(new ListMemoryQuery(), default);

        Assert.Equal(["mío"], result.Value.Entries.Select(e => e.Text));
        Assert.Equal((25, 200), (result.Value.MaxEntries, result.Value.MaxTextLength));
    }
}

internal sealed class FakeMemoryRepository : IMemoryEntryWriteRepository, IMemoryEntryReadRepository
{
    public List<MemoryEntry> Entries { get; } = [];

    public MemoryEntry Add(MemoryEntry entry)
    {
        Entries.Add(entry);
        return entry;
    }

    void SergioIzq.Domain.Kernel.Interfaces.Repositories.IWriteRepository<MemoryEntry, MemoryEntryId>.Add(MemoryEntry entity) => Add(entity);

    public Task<MemoryEntry?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Entries.FirstOrDefault(e => e.Id.Value == id));

    public Task CreateAsync(MemoryEntry entity, CancellationToken cancellationToken)
    {
        Add(entity);
        return Task.CompletedTask;
    }

    public void Update(MemoryEntry entity)
    {
    }

    public void Delete(MemoryEntry entity) => Entries.Remove(entity);

    public Task<MemoryEntry?> GetOwnedByUserAsync(Guid entryId, Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(Entries.FirstOrDefault(e => e.Id.Value == entryId && e.UserId.Value == userId));

    public Task<int> CountByUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(Entries.Count(e => e.UserId.Value == userId));

    public Task DeleteAllByUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        Entries.RemoveAll(e => e.UserId.Value == userId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryEntrySummary>> ListByUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MemoryEntrySummary>>(Entries
            .Where(e => e.UserId.Value == userId)
            .OrderByDescending(e => e.UpdatedAt)
            .Select(MemoryEntrySummary.From)
            .ToList());
}
