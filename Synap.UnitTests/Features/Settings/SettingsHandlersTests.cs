using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Synap.Application.Features.Assistant.Agent;
using Synap.Application.Features.Assistant.Queries;
using Synap.Application.Features.Memory.Commands;
using Synap.Application.Features.Notes.Commands.AddTag;
using Synap.Application.Features.Notes.Commands.Create;
using Synap.Application.Features.Settings;
using Synap.Application.Features.Settings.Commands;
using Synap.Application.Features.Settings.Queries;
using Synap.Domain;
using Synap.UnitTests.Domain;

namespace Synap.UnitTests.Features.Settings;

/// <summary>
/// byok-groq-and-settings tasks 3.2 and 4.1-4.4 - specs/user-settings and specs/ai-assistant
/// behaviour of the settings and assistant handlers, with in-memory fakes (no DB, no network).
/// </summary>
public class SettingsHandlersTests
{
    private const string ValidKey = "gsk_valid_key_a1B2";

    private readonly FakeUserRepository _users = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeSecretProtector _protector = new();
    private readonly FakeAiServiceClient _ai = new();
    private readonly IOptions<AiOptions> _options = Options.Create(new AiOptions { DefaultGroqModel = "default-model" });
    private readonly User _user = UserGroqSettingsTests.NewUser();
    private readonly FakeUserContext _context;

    public SettingsHandlersTests()
    {
        _users.Add(_user);
        _context = new FakeUserContext(_user.Id.Value);
    }

    private void GiveUserKey(string key = ValidKey) => _user.SetGroqApiKey(_protector.Protect(key), key[^4..]);

    // ---- GetSettings ----

    [Fact]
    public async Task GetSettings_never_returns_the_plaintext_key()
    {
        GiveUserKey();

        var result = await new GetSettingsQueryHandler(_users, _context, _options).Handle(new GetSettingsQuery(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("user@example.com", result.Value.Email);
        Assert.True(result.Value.Ai.HasGroqKey);
        Assert.Equal("gsk_…a1B2", result.Value.Ai.GroqKeyMasked);
        Assert.Equal("default-model", result.Value.Ai.DefaultGroqModel);
        Assert.DoesNotContain(ValidKey, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task GetSettings_without_key()
    {
        var result = await new GetSettingsQueryHandler(_users, _context, _options).Handle(new GetSettingsQuery(), default);

        Assert.False(result.Value.Ai.HasGroqKey);
        Assert.Null(result.Value.Ai.GroqKeyMasked);
    }

    // ---- SaveGroqApiKey ----

    private SaveGroqApiKeyCommandHandler SaveHandler() => new(_users, _unitOfWork, _context, _protector, _ai, _options);

    [Fact]
    public async Task SaveGroqApiKey_valid_key_is_validated_encrypted_and_stored()
    {
        var result = await SaveHandler().Handle(new SaveGroqApiKeyCommand($"  {ValidKey}  "), default);

        Assert.True(result.IsSuccess);
        Assert.Equal([ValidKey], _ai.ListModelsKeys);
        Assert.Equal($"enc({ValidKey})", _user.GroqApiKeyEncrypted);
        Assert.Equal("a1B2", _user.GroqApiKeyLast4);
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Theory]
    [InlineData(LlmKeyStatus.InvalidKey)]
    [InlineData(LlmKeyStatus.RateLimited)]
    [InlineData(LlmKeyStatus.Unavailable)]
    [InlineData(LlmKeyStatus.ServiceUnavailable)]
    public async Task SaveGroqApiKey_failed_validation_keeps_previous_key(LlmKeyStatus status)
    {
        GiveUserKey("gsk_previous_key_OLD1");
        _ai.ModelsResult = LlmModelsResult.Failed(status);

        var result = await SaveHandler().Handle(new SaveGroqApiKeyCommand(ValidKey), default);

        Assert.True(result.IsFailure);
        Assert.Equal(SettingsErrors.FromKeyStatus(status).Message, result.Error.Message);
        Assert.Equal("enc(gsk_previous_key_OLD1)", _user.GroqApiKeyEncrypted);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    /// <summary>
    /// specs/user-settings "AI service unreachable during validation": the user is told what
    /// actually failed. Blaming Groq for a failure between Synap's own containers is what sent a
    /// whole debugging session after the wrong party.
    /// </summary>
    [Fact]
    public async Task SaveGroqApiKey_does_not_blame_groq_when_the_ai_service_is_what_failed()
    {
        _ai.ModelsResult = LlmModelsResult.Failed(LlmKeyStatus.ServiceUnavailable);

        var result = await SaveHandler().Handle(new SaveGroqApiKeyCommand(ValidKey), default);

        Assert.True(result.IsFailure);
        Assert.DoesNotContain("Groq", result.Error.Message);
        Assert.Contains("servicio de IA", result.Error.Message);
    }

    [Fact]
    public async Task SaveGroqApiKey_empty_key_is_rejected_without_calling_groq()
    {
        var result = await SaveHandler().Handle(new SaveGroqApiKeyCommand("   "), default);

        Assert.True(result.IsFailure);
        Assert.Empty(_ai.ListModelsKeys);
    }

    [Fact]
    public async Task SaveGroqApiKey_resets_a_model_the_new_key_does_not_offer()
    {
        GiveUserKey("gsk_previous_key_OLD1");
        _user.SetGroqModel("model-z");

        await SaveHandler().Handle(new SaveGroqApiKeyCommand(ValidKey), default);

        Assert.Null(_user.GroqModel);
    }

    // ---- DeleteGroqApiKey / ListGroqModels ----

    [Fact]
    public async Task DeleteGroqApiKey_clears_key()
    {
        GiveUserKey();

        var result = await new DeleteGroqApiKeyCommandHandler(_users, _unitOfWork, _context, _options).Handle(new DeleteGroqApiKeyCommand(), default);

        Assert.False(result.Value.HasGroqKey);
        Assert.False(_user.HasGroqApiKey);
        Assert.Equal(1, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task ListGroqModels_uses_the_decrypted_stored_key()
    {
        GiveUserKey();

        var result = await new ListGroqModelsQueryHandler(_users, _context, _protector, _ai).Handle(new ListGroqModelsQuery(), default);

        Assert.Equal([new LlmModel("model-a", true), new LlmModel("model-b", false)], result.Value);
        Assert.Equal([ValidKey], _ai.ListModelsKeys);
    }

    [Fact]
    public async Task ListGroqModels_without_key_fails()
    {
        var result = await new ListGroqModelsQueryHandler(_users, _context, _protector, _ai).Handle(new ListGroqModelsQuery(), default);

        Assert.True(result.IsFailure);
        Assert.Equal(SettingsErrors.GroqKeyNotConfigured.Message, result.Error.Message);
    }

    // ---- SetGroqModel ----

    private SetGroqModelCommandHandler SetModelHandler() => new(_users, _unitOfWork, _context, _protector, _ai, _options);

    [Fact]
    public async Task SetGroqModel_accepts_an_available_model()
    {
        GiveUserKey();

        var result = await SetModelHandler().Handle(new SetGroqModelCommand("model-b"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("model-b", _user.GroqModel);
    }

    [Fact]
    public async Task SetGroqModel_rejects_an_unknown_model_and_keeps_the_previous_one()
    {
        GiveUserKey();
        _user.SetGroqModel("model-a");

        var result = await SetModelHandler().Handle(new SetGroqModelCommand("model-z"), default);

        Assert.True(result.IsFailure);
        Assert.Equal(SettingsErrors.GroqModelUnknown.Message, result.Error.Message);
        Assert.Equal("model-a", _user.GroqModel);
        Assert.Equal(0, _unitOfWork.SaveCalls);
    }

    [Fact]
    public async Task SetGroqModel_null_resets_to_default_without_calling_groq()
    {
        GiveUserKey();
        _user.SetGroqModel("model-a");

        var result = await SetModelHandler().Handle(new SetGroqModelCommand(null), default);

        Assert.True(result.IsSuccess);
        Assert.Null(_user.GroqModel);
        Assert.Empty(_ai.ListModelsKeys);
    }

    // ---- SetBriefing (daily-briefing task 1.3) ----

    private SetBriefingCommandHandler BriefingHandler() => new(_users, _unitOfWork, _context);

    [Fact]
    public async Task SetBriefing_turns_it_on_at_the_chosen_hour()
    {
        var result = await BriefingHandler().Handle(new SetBriefingCommand(Enabled: true, Hour: 7), default);

        Assert.True(result.IsSuccess);
        Assert.Equal((true, 7), (result.Value.Enabled, result.Value.Hour));
        Assert.Equal((true, 7), (_user.BriefingEnabled, _user.BriefingHour));
    }

    [Fact]
    public async Task SetBriefing_refuses_an_hour_outside_the_day_and_changes_nothing()
    {
        var result = await BriefingHandler().Handle(new SetBriefingCommand(Enabled: true, Hour: 25), default);

        Assert.True(result.IsFailure);
        Assert.Equal(SettingsErrors.BriefingHourInvalid, result.Error);
        Assert.False(_user.BriefingEnabled);
        Assert.Null(_user.BriefingHour);
    }

    [Fact]
    public async Task SetBriefing_reports_whether_it_can_be_delivered()
    {
        Assert.False((await BriefingHandler().Handle(new SetBriefingCommand(true, 9), default)).Value.CanBeDelivered);

        _user.CompleteTelegramLink("123456");

        Assert.True((await BriefingHandler().Handle(new SetBriefingCommand(true, 9), default)).Value.CanBeDelivered);
    }

    /// <summary>specs/briefing "One user's setting is not another's".</summary>
    [Fact]
    public async Task SetBriefing_touches_only_the_requesting_user()
    {
        var other = UserGroqSettingsTests.NewUser();
        _users.Add(other);

        await BriefingHandler().Handle(new SetBriefingCommand(Enabled: true, Hour: 9), default);

        Assert.True(_user.BriefingEnabled);
        Assert.False(other.BriefingEnabled);
        Assert.Null(other.BriefingHour);
    }

    // ---- AskAssistant ----

    private AskAssistantQueryHandler AskHandler()
    {
        var notes = new FakeNoteRepository();
        var memory = new Memory.FakeMemoryRepository();
        var sender = new Assistant.DispatchingSender(
            new CreateNoteCommandHandler(notes, new Assistant.FakeTagRepository(), _unitOfWork, _context, new Assistant.NoopJobQueue()),
            new AddTagCommandHandler(notes, new Assistant.FakeTagRepository(), _unitOfWork, _context),
            new AddMemoryEntryCommandHandler(memory, _unitOfWork, _context));
        return new(_ai, _context, _users, _protector, notes, memory, new AssistantAgent(_ai, sender, new Assistant.NotesView(notes), NullLogger<AssistantAgent>.Instance));
    }

    [Fact]
    public async Task Ask_without_key_returns_key_missing_and_never_calls_the_ai_service()
    {
        var result = await AskHandler().Handle(new AskAssistantQuery("¿qué?"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(AssistantAnswerStatus.KeyMissing, result.Value.Status);
        Assert.False(result.Value.Grounded);
        Assert.Empty(_ai.AskCalls);
        Assert.Empty(_ai.StepCalls);
    }

    [Fact]
    public async Task Ask_with_key_forwards_the_users_own_key_and_model()
    {
        GiveUserKey();
        _user.SetGroqModel("model-b");

        // A model without tools, so the question also takes the plain answer path.
        _ai.Steps.Enqueue(AgentStepResult.Failed(AgentStepStatus.ToolsUnsupported));

        var result = await AskHandler().Handle(new AskAssistantQuery("¿qué?"), default);

        Assert.Equal(AssistantAnswerStatus.Ok, result.Value.Status);
        Assert.Equal((ValidKey, "model-b"), (_ai.StepCalls.Single().Key, _ai.StepCalls.Single().Model));
        Assert.Equal([(_user.Id.Value, ValidKey, (string?)"model-b")], _ai.AskCalls);
    }

    [Fact]
    public async Task Ask_with_undecryptable_key_returns_invalid_key()
    {
        _user.SetGroqApiKey("garbage-from-another-master-key", "a1B2");

        var result = await AskHandler().Handle(new AskAssistantQuery("¿qué?"), default);

        Assert.Equal(AssistantAnswerStatus.InvalidKey, result.Value.Status);
        Assert.Empty(_ai.AskCalls);
    }

    [Fact]
    public async Task Ask_never_uses_another_users_key()
    {
        var otherUser = _users.Add(UserGroqSettingsTests.NewUser());
        otherUser.SetGroqApiKey(_protector.Protect("gsk_other_users_key"), "_key");

        var result = await AskHandler().Handle(new AskAssistantQuery("¿qué?"), default);

        Assert.Equal(AssistantAnswerStatus.KeyMissing, result.Value.Status);
        Assert.Empty(_ai.AskCalls);
    }
}
