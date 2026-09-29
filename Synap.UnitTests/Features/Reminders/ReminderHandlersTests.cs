using Synap.Application.Features.Reminders.Commands;
using Synap.Application.Features.Reminders.Queries;
using Synap.Domain;
using Synap.Shared.Domain.ValueObjects;
using Synap.Shared.Domain.ValueObjects.Ids;
using Synap.UnitTests.Features.Assistant;
using Synap.UnitTests.Features.Settings;

namespace Synap.UnitTests.Features.Reminders;

/// <summary>assistant-reminders tasks 3.1 to 3.4 - specs/reminders rules, with in-memory fakes.</summary>
public class ReminderHandlersTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly FakeReminderRepository _reminders = new();
    private readonly FakeNoteRepository _notes = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakeUserSettingsView _settings = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeUserContext _context = new(Me);

    /// <summary>Whole seconds, like a stored due moment (see Reminder.AsDueMoment).</summary>
    private static DateTime Future => Truncate(DateTime.UtcNow.AddDays(1));

    private static DateTime Truncate(DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private CreateReminderCommandHandler Create() => new(_reminders, new NotesView(_notes), _unitOfWork, _context);

    private Note SeedNote(Guid owner, string title)
        => _notes.Add(Note.Create(UserId.CreateFromDatabase(owner), NoteType.Text, title, "contenido"));

    private Reminder Seed(Guid owner, string text, DateTime? dueAt = null, string? recurrence = null, NoteId? noteId = null)
        => _reminders.Add(Reminder.Create(
            UserId.CreateFromDatabase(owner), text, dueAt ?? Future, DateTime.UtcNow, noteId, recurrence).Value);

    // ---- Create (task 3.1) ----

    [Fact]
    public async Task Create_stores_the_reminder_for_the_current_user()
    {
        var dueAt = Future;

        var result = await Create().Handle(new CreateReminderCommand("Renovar el certificado SSL", dueAt), default);

        Assert.True(result.IsSuccess);
        var stored = Assert.Single(_reminders.All);
        Assert.Equal((Me, "Renovar el certificado SSL"), (stored.UserId.Value, stored.Text));
        Assert.Equal(dueAt, stored.DueAt);
        Assert.True(stored.IsPending);
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Create_links_the_note_and_returns_its_title()
    {
        var note = SeedNote(Me, "Volúmenes de Docker");

        var result = await Create().Handle(new CreateReminderCommand("Revisar esto", Future, note.Id.Value), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(note.Id.Value, result.Value.NoteId);
        Assert.Equal("Volúmenes de Docker", result.Value.NoteTitle);
    }

    [Fact]
    public async Task Create_accepts_a_recurrence()
    {
        var result = await Create().Handle(new CreateReminderCommand("Revisar copias", Future, Recurrence: "weekly:0"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("weekly:0", result.Value.Recurrence);
    }

    [Fact]
    public async Task Create_rejects_a_past_moment_and_stores_nothing()
    {
        var result = await Create().Handle(new CreateReminderCommand("Tarde", DateTime.UtcNow.AddMinutes(-1)), default);

        Assert.True(result.IsFailure);
        Assert.Equal(Reminder.DueInThePast.Message, result.Error.Message);
        Assert.Empty(_reminders.All);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_an_empty_text(string? text)
    {
        var result = await Create().Handle(new CreateReminderCommand(text, Future), default);

        Assert.True(result.IsFailure);
        Assert.Equal(Reminder.EmptyText.Message, result.Error.Message);
        Assert.Empty(_reminders.All);
    }

    [Fact]
    public async Task Create_rejects_a_text_over_500_characters()
    {
        var result = await Create().Handle(new CreateReminderCommand(new string('a', 501), Future), default);

        Assert.True(result.IsFailure);
        Assert.Equal(Reminder.TextTooLong.Message, result.Error.Message);
    }

    [Fact]
    public async Task Create_rejects_an_unsupported_recurrence()
    {
        var result = await Create().Handle(new CreateReminderCommand("Revisar", Future, Recurrence: "el tercer martes"), default);

        Assert.True(result.IsFailure);
        Assert.Equal(Recurrence.Invalid.Message, result.Error.Message);
        Assert.Empty(_reminders.All);
    }

    [Fact]
    public async Task Create_treats_another_users_note_as_missing()
    {
        var theirNote = SeedNote(Other, "Su nota");

        var result = await Create().Handle(new CreateReminderCommand("Revisar esto", Future, theirNote.Id.Value), default);

        Assert.True(result.IsFailure);
        Assert.Equal("Nota no encontrada.", result.Error.Message);
        Assert.Empty(_reminders.All);
    }

    [Fact]
    public async Task Create_treats_an_unknown_note_as_missing()
    {
        var result = await Create().Handle(new CreateReminderCommand("Revisar esto", Future, Guid.NewGuid()), default);

        Assert.True(result.IsFailure);
        Assert.Equal("Nota no encontrada.", result.Error.Message);
    }

    // ---- Update and cancel (task 3.2) ----

    [Fact]
    public async Task Update_changes_the_text_moment_and_recurrence()
    {
        var reminder = Seed(Me, "Antes");
        var newMoment = Future.AddDays(3);

        var result = await new UpdateReminderCommandHandler(_reminders, _unitOfWork, _context)
            .Handle(new UpdateReminderCommand(reminder.Id.Value, "Después", newMoment, "daily"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Después", reminder.Text);
        Assert.Equal(newMoment, reminder.DueAt);
        Assert.True(reminder.IsRecurring);
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Update_rejects_a_past_moment_and_keeps_the_previous_one()
    {
        var original = Future;
        var reminder = Seed(Me, "Antes", original);

        var result = await new UpdateReminderCommandHandler(_reminders, _unitOfWork, _context)
            .Handle(new UpdateReminderCommand(reminder.Id.Value, "Después", DateTime.UtcNow.AddMinutes(-1)), default);

        Assert.True(result.IsFailure);
        Assert.Equal(Reminder.DueInThePast.Message, result.Error.Message);
        Assert.Equal("Antes", reminder.Text);
        Assert.Equal(original, reminder.DueAt);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task Update_treats_another_users_reminder_as_missing()
    {
        var theirs = Seed(Other, "Suyo");

        var result = await new UpdateReminderCommandHandler(_reminders, _unitOfWork, _context)
            .Handle(new UpdateReminderCommand(theirs.Id.Value, "Mío ahora", Future), default);

        Assert.True(result.IsFailure);
        Assert.Equal(Reminder.NotFound.Message, result.Error.Message);
        Assert.Equal("Suyo", theirs.Text);
    }

    [Fact]
    public async Task Cancel_removes_the_reminder()
    {
        var reminder = Seed(Me, "Ya no");

        var result = await new CancelReminderCommandHandler(_reminders, _unitOfWork, _context)
            .Handle(new CancelReminderCommand(reminder.Id.Value), default);

        Assert.True(result.IsSuccess);
        Assert.Empty(_reminders.All);
    }

    [Fact]
    public async Task Cancel_treats_another_users_reminder_as_missing()
    {
        var theirs = Seed(Other, "Suyo");

        var result = await new CancelReminderCommandHandler(_reminders, _unitOfWork, _context)
            .Handle(new CancelReminderCommand(theirs.Id.Value), default);

        Assert.True(result.IsFailure);
        Assert.Single(_reminders.All);
    }

    // ---- Queries (task 3.3) ----

    [Fact]
    public async Task Pending_reminders_are_listed_soonest_first_for_the_current_user_only()
    {
        Seed(Me, "Más tarde", Future.AddDays(3));
        Seed(Me, "Antes", Future.AddHours(1));
        Seed(Other, "De otra persona", Future);

        var result = await new ListRemindersQueryHandler(_reminders, _settings, _context).Handle(new ListRemindersQuery(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Antes", "Más tarde"], result.Value.Reminders.Select(r => r.Text));
        Assert.Equal(Reminder.MaxTextLength, result.Value.MaxTextLength);
    }

    [Fact]
    public async Task The_list_reports_whether_telegram_is_connected()
    {
        var handler = new ListRemindersQueryHandler(_reminders, _settings, _context);

        var before = await handler.Handle(new ListRemindersQuery(), default);
        Assert.False(before.Value.TelegramConnected);

        _settings.Settings = new UserReminderSettings("123456789", "Europe/Madrid");

        var after = await handler.Handle(new ListRemindersQuery(), default);
        Assert.True(after.Value.TelegramConnected);
    }

    [Fact]
    public async Task A_notes_reminders_are_listed_and_a_note_that_is_not_mine_has_none()
    {
        var note = SeedNote(Me, "Con recordatorios");
        var theirNote = SeedNote(Other, "Su nota");
        Seed(Me, "Primero", Future.AddHours(1), noteId: note.Id);
        Seed(Me, "Segundo", Future.AddHours(2), noteId: note.Id);
        Seed(Other, "Suyo", Future, noteId: theirNote.Id);

        var handler = new ListNoteRemindersQueryHandler(_reminders, _context);

        var mine = await handler.Handle(new ListNoteRemindersQuery(note.Id.Value), default);
        Assert.Equal(["Primero", "Segundo"], mine.Value.Select(r => r.Text));

        var theirs = await handler.Handle(new ListNoteRemindersQuery(theirNote.Id.Value), default);
        Assert.Empty(theirs.Value);
    }

    // ---- Timezone (task 3.4) ----

    [Fact]
    public async Task A_known_timezone_is_stored()
    {
        var user = _users.Add(User.Create(Email.CreateFromDatabase("me@example.com"), PasswordHash.CreateFromDatabase("hash")));

        var result = await new SetUserTimezoneCommandHandler(_users, _unitOfWork, new FakeUserContext(user.Id.Value))
            .Handle(new SetUserTimezoneCommand("Europe/Madrid"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Europe/Madrid", user.Timezone);
        Assert.Equal(1, _unitOfWork.SaveCalls);
        Assert.Equal(1, _users.UpdateCalls);
    }

    [Fact]
    public async Task An_unknown_timezone_leaves_the_stored_one_alone_without_failing_the_request()
    {
        var user = _users.Add(User.Create(Email.CreateFromDatabase("me@example.com"), PasswordHash.CreateFromDatabase("hash")));
        user.SetTimezone("Europe/Madrid");

        var result = await new SetUserTimezoneCommandHandler(_users, _unitOfWork, new FakeUserContext(user.Id.Value))
            .Handle(new SetUserTimezoneCommand("Mars/Olympus_Mons"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Europe/Madrid", user.Timezone);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task An_unchanged_timezone_is_not_saved_again()
    {
        var user = _users.Add(User.Create(Email.CreateFromDatabase("me@example.com"), PasswordHash.CreateFromDatabase("hash")));
        user.SetTimezone("Europe/Madrid");

        await new SetUserTimezoneCommandHandler(_users, _unitOfWork, new FakeUserContext(user.Id.Value))
            .Handle(new SetUserTimezoneCommand("Europe/Madrid"), default);

        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task No_timezone_at_all_is_a_no_op()
    {
        var result = await new SetUserTimezoneCommandHandler(_users, _unitOfWork, _context)
            .Handle(new SetUserTimezoneCommand(null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }
}

/// <summary>In-memory reminders with the real repositories' ownership and pending rules.</summary>
internal sealed class FakeReminderRepository : IReminderWriteRepository, IReminderReadRepository
{
    private readonly List<Reminder> _reminders = [];
    private readonly Dictionary<Guid, string?> _noteTitles = [];

    public IReadOnlyList<Reminder> All => _reminders;

    public Reminder Add(Reminder reminder)
    {
        _reminders.Add(reminder);
        return reminder;
    }

    public void SetNoteTitle(Guid noteId, string? title) => _noteTitles[noteId] = title;

    public Task<Reminder?> GetOwnedByUserAsync(Guid reminderId, Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(_reminders.FirstOrDefault(r => r.Id.Value == reminderId && r.UserId.Value == userId));

    public Task<IReadOnlyList<DueReminder>> ListDueAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DueReminder>>(_reminders
            .Where(r => r.DueAt <= nowUtc && r.SentAt is null && r.DismissedAt is null)
            .OrderBy(r => r.DueAt)
            .Take(limit)
            .Select(r => new DueReminder(r, null, null, TitleOf(r)))
            .ToList());

    public Task<IReadOnlyList<ReminderSummary>> ListPendingByUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(Summaries(r => r.UserId.Value == userId));

    public Task<IReadOnlyList<ReminderSummary>> ListByNoteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken = default)
        => Task.FromResult(Summaries(r => r.UserId.Value == userId && r.NoteId?.Value == noteId));

    private IReadOnlyList<ReminderSummary> Summaries(Func<Reminder, bool> predicate)
        => _reminders
            .Where(r => r.DismissedAt is null && predicate(r))
            .OrderBy(r => r.DueAt)
            .Select(r => ReminderSummary.From(r, TitleOf(r)))
            .ToList();

    private string? TitleOf(Reminder reminder)
        => reminder.NoteId is { } noteId ? _noteTitles.GetValueOrDefault(noteId.Value) : null;

    public Task<Reminder?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_reminders.FirstOrDefault(r => r.Id.Value == id));

    void SergioIzq.Domain.Kernel.Interfaces.Repositories.IWriteRepository<Reminder, ReminderId>.Add(Reminder entity) => Add(entity);

    public Task CreateAsync(Reminder entity, CancellationToken cancellationToken)
    {
        Add(entity);
        return Task.CompletedTask;
    }

    public void Update(Reminder entity)
    {
    }

    public void Delete(Reminder entity) => _reminders.Remove(entity);
}

/// <summary>
/// The reminder-relevant reads. <see cref="Settings"/> is what the "Recordatorios" page warns on;
/// <see cref="Users"/> backs the link-code lookup the webhook does, when a test seeds users.
/// </summary>
internal sealed class FakeUserSettingsView : IUserReadRepository
{
    public UserReminderSettings? Settings { get; set; } = new(null, null);

    public List<User> Users { get; } = [];

    public Task<UserReminderSettings?> GetReminderSettingsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = Users.FirstOrDefault(u => u.Id.Value == userId);
        return Task.FromResult(user is null ? Settings : new UserReminderSettings(user.TelegramChatId, user.Timezone));
    }

    public Task<User?> GetByTelegramLinkTokenAsync(string token, CancellationToken cancellationToken = default)
        => Task.FromResult(Users.FirstOrDefault(u => u.TelegramLinkToken == token));

    public Task<User?> GetByTelegramChatIdAsync(string chatId, CancellationToken cancellationToken = default)
        => Task.FromResult(Users.FirstOrDefault(u => u.TelegramChatId == chatId));

    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<User?> GetByApiTokenHashAsync(string apiTokenHash, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
