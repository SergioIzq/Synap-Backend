using Synap.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synap.UnitTests.Domain;

/// <summary>
/// The web app switches on these exact strings. The kernel's result handler serializes enums
/// with a plain (PascalCase) JsonStringEnumConverter, so the wire names are pinned on the enum.
/// </summary>
public class AssistantAnswerSerializationTests
{
    [Theory]
    [InlineData(AssistantAnswerStatus.Ok, "ok")]
    [InlineData(AssistantAnswerStatus.NoRelevantNotes, "noRelevantNotes")]
    [InlineData(AssistantAnswerStatus.KeyMissing, "keyMissing")]
    [InlineData(AssistantAnswerStatus.InvalidKey, "invalidKey")]
    [InlineData(AssistantAnswerStatus.RateLimited, "rateLimited")]
    [InlineData(AssistantAnswerStatus.Unavailable, "unavailable")]
    [InlineData(AssistantAnswerStatus.ScopeUnsupported, "scopeUnsupported")]
    public void Status_is_serialized_with_its_camelCase_wire_name(AssistantAnswerStatus status, string expected)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, options));
    }

    [Theory]
    [InlineData(AssistantActionType.NoteCreated, "noteCreated")]
    [InlineData(AssistantActionType.TagsAdded, "tagsAdded")]
    [InlineData(AssistantActionType.MemorySaved, "memorySaved")]
    [InlineData(AssistantActionType.ReminderCreated, "reminderCreated")]
    public void Action_type_is_serialized_with_its_camelCase_wire_name(AssistantActionType type, string expected)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(type, options));
    }

    [Fact]
    public void Actions_are_always_present_even_when_empty()
    {
        var json = JsonSerializer.Serialize(new AssistantAnswer("a", [], false, AssistantAnswerStatus.Ok), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"actions\":[]", json);
    }
}

/// <summary>branding-and-note-composer task 1.1 - the web app switches on these exact strings.</summary>
public class NoteTypeSerializationTests
{
    // What the kernel's result handler does: a plain (PascalCase) enum converter in the options.
    private static readonly JsonSerializerOptions KernelLike = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>note-status task 2.3 - the status has to reach the web app on the wire.</summary>
    [Theory]
    [InlineData(NoteStatus.Pending, "pending")]
    [InlineData(NoteStatus.InProgress, "inProgress")]
    [InlineData(NoteStatus.Paused, "paused")]
    [InlineData(NoteStatus.Completed, "completed")]
    public void A_notes_status_is_serialized_with_its_camelCase_wire_name(NoteStatus status, string expected)
    {
        var result = new NoteSearchResult(Guid.Empty, null, "c", NoteType.Text, default, default, status, [], null, null, null);

        Assert.Contains($"\"status\":\"{expected}\"", JsonSerializer.Serialize(result, KernelLike), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_note_with_no_status_serializes_it_as_null()
    {
        var result = new NoteSearchResult(Guid.Empty, null, "c", NoteType.Text, default, default, null, [], null, null, null);

        Assert.Contains("\"status\":null", JsonSerializer.Serialize(result, KernelLike), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(NoteType.Text, "text")]
    [InlineData(NoteType.CodeSnippet, "codeSnippet")]
    [InlineData(NoteType.Bookmark, "bookmark")]
    public void Response_shapes_use_camelCase_even_with_a_PascalCase_enum_converter(NoteType type, string expected)
    {
        var result = new NoteSearchResult(Guid.Empty, null, "c", type, default, default, null, [], null, null, null);
        var related = new RelatedNote(Guid.Empty, null, "c", type, 0.5);

        Assert.Contains($"\"type\":\"{expected}\"", JsonSerializer.Serialize(result, KernelLike), StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"\"{expected}\"", JsonSerializer.Serialize(related, KernelLike));
    }

    [Theory]
    [InlineData("\"codeSnippet\"")]
    [InlineData("\"CodeSnippet\"")]
    [InlineData("\"codesnippet\"")]
    public void Input_accepts_any_casing(string json)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

        Assert.Equal(NoteType.CodeSnippet, JsonSerializer.Deserialize<NoteType>(json, options));
    }
}
