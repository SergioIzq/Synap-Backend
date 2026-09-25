namespace Synap.Application.Features.Settings;

/// <summary>
/// Bound from the "Ai" configuration section. DefaultGroqModel mirrors the AI service's own
/// SYNAP_AI_GROQ_MODEL (both come from GROQ_MODEL in docker-compose) - the .NET side only needs
/// it to show "Por defecto (x)" in Settings; it's the AI service that applies it.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public string DefaultGroqModel { get; set; } = "qwen/qwen3.8-27b";
}
