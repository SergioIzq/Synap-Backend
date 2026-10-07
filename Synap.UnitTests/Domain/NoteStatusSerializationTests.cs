using Synap.Domain;
using System.Text.Json;

namespace Synap.UnitTests.Domain;

/// <summary>note-status task 1.1 - the wire names and the case-insensitive input.</summary>
public class NoteStatusSerializationTests
{
    private static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NoteStatusJsonConverter());
        return options;
    }

    [Theory]
    [InlineData(NoteStatus.Pending, "pending")]
    [InlineData(NoteStatus.InProgress, "inProgress")]
    [InlineData(NoteStatus.Paused, "paused")]
    [InlineData(NoteStatus.Completed, "completed")]
    public void Each_status_round_trips_through_its_wire_name(NoteStatus status, string wire)
    {
        var json = JsonSerializer.Serialize(status, Options);

        Assert.Equal($"\"{wire}\"", json);
        Assert.Equal(status, JsonSerializer.Deserialize<NoteStatus>(json, Options));
    }

    /// <summary>
    /// design.md Decision 8: an exact-match enum breaks clients sending "Pending", the mistake
    /// the iOS Shortcut already made with note types.
    /// </summary>
    [Theory]
    [InlineData("\"PENDING\"")]
    [InlineData("\"Pending\"")]
    [InlineData("\"pending\"")]
    public void Input_is_case_insensitive(string json)
        => Assert.Equal(NoteStatus.Pending, JsonSerializer.Deserialize<NoteStatus>(json, Options));

    [Theory]
    [InlineData("\"INPROGRESS\"")]
    [InlineData("\"inprogress\"")]
    public void In_progress_is_read_whatever_its_casing(string json)
        => Assert.Equal(NoteStatus.InProgress, JsonSerializer.Deserialize<NoteStatus>(json, Options));

    [Fact]
    public void An_unknown_status_is_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NoteStatus>("\"archived\"", Options));

    /// <summary>"none" is a value of the *search filter*, never of the status itself.</summary>
    [Fact]
    public void None_is_not_a_status()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<NoteStatus>("\"none\"", Options));
}
