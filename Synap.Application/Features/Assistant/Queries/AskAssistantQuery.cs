using SergioIzq.Application.Kernel.Messaging;
using Synap.Domain;

namespace Synap.Application.Features.Assistant.Queries;

public sealed record AskAssistantQuery(string Question) : IQuery<AssistantAnswer>;
