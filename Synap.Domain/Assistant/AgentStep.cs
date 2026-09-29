using System.Text.Json;

namespace Synap.Domain;

/// <summary>
/// One message of the assistant's tool loop, in the AI service's provider-neutral shape
/// (assistant-agent-foundations design.md Decision 3): roles system/user/assistant/tool; an
/// assistant message may carry tool calls, and a tool message answers one by its id.
/// </summary>
public sealed record AgentMessage(string Role, string? Content, IReadOnlyList<AgentToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    public static AgentMessage System(string content) => new("system", content);
    public static AgentMessage User(string content) => new("user", content);
    public static AgentMessage Assistant(string? content, IReadOnlyList<AgentToolCall>? toolCalls = null) => new("assistant", content, toolCalls);
    public static AgentMessage ToolResult(string toolCallId, string content) => new("tool", content, ToolCallId: toolCallId);
}

/// <summary><see cref="Arguments"/> is null when the model's arguments were not a JSON object; <see cref="ArgumentsError"/> then says why.</summary>
public sealed record AgentToolCall(string Id, string Name, JsonElement? Arguments, string? ArgumentsError = null);

/// <summary>A tool offered to the model; <see cref="Parameters"/> is its JSON Schema.</summary>
public sealed record AgentTool(string Name, string Description, JsonElement Parameters);

public sealed record AgentStepResult(AgentStepStatus Status, string? Text, IReadOnlyList<AgentToolCall> ToolCalls)
{
    public static AgentStepResult Failed(AgentStepStatus status) => new(status, null, []);
}

public enum AgentStepStatus
{
    Ok,
    InvalidKey,
    RateLimited,
    Unavailable,
    /// <summary>The chosen model can't use tools - answer without actions instead.</summary>
    ToolsUnsupported,
    /// <summary>The model produced a tool call the provider could not parse.</summary>
    ToolCallFailed,
}

/// <summary>
/// Why a question is answered without actions, so the answer can explain it if the user asked
/// for one (specs/ai-assistant "No actions in a scoped conversation", "Answering without actions").
/// </summary>
public enum ActionsUnavailableReason
{
    /// <summary>Scoped conversations never perform actions.</summary>
    Scope,

    /// <summary>The user's chosen model can't use tools.</summary>
    Model,
}

/// <summary>A note found by the AI service's hybrid search, as the `search_notes` tool shows it.</summary>
public sealed record NoteSearchHit(Guid Id, string? Title, string Type, IReadOnlyList<string> Tags, string Snippet);
