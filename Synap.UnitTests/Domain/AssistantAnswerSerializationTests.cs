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
    public void Status_is_serialized_with_its_camelCase_wire_name(AssistantAnswerStatus status, string expected)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(status, options));
    }
}
