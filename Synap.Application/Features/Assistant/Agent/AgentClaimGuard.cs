namespace Synap.Application.Features.Assistant.Agent;

/// <summary>
/// The wording of the check that stops an answer from asserting an action the system never
/// performed (observable-failures design.md Decision 4, specs/ai-assistant "An answer never
/// claims an action it did not perform").
///
/// The claim is detected by asking the model, not by matching patterns: a phrasing the patterns
/// miss is delivered as true, which is the failure this exists to stop.
/// </summary>
public static class AgentClaimGuard
{
    /// <summary>A yes/no classification, deliberately not a rewrite: the answer's text is never touched here.</summary>
    public const string ClassifierInstructions =
        "You classify one message. Answer with a single word, YES or NO, and nothing else. "
        + "Answer YES when the message states, as something already done, that a note was created or written down, "
        + "that tags were added to a note, that a fact was remembered or saved, or that a reminder or alert was set. "
        + "Answer NO for anything else: questions, answers about the user's notes, offers to do something, "
        + "things stated as still to be done, or saying that something could not be done.";

    /// <summary>
    /// What the model is told when it claimed an action it never requested: the claim is false as
    /// things stand, and the tools are offered again so it can make it true.
    /// </summary>
    public const string RetryInstruction =
        "Your last message says an action was done, but you requested no action, so nothing was done and the user "
        + "would be misled. If the user asked for it, call the tool that does it now. If they did not ask for it, "
        + "or you cannot do it, say so plainly instead of saying it is done.";

    /// <summary>
    /// What the user reads when the claim could not be made true. Fixed text, not the model's:
    /// its own wording is exactly what cannot be trusted at this point.
    /// </summary>
    public const string CouldNotDoItMessage =
        "No he podido hacerlo. Puedes hacerlo tú desde la aplicación: crear una nota o etiquetarla desde la nota, "
        + "los recordatorios desde Recordatorios, y lo que quieres que recuerde de ti desde Configuración > Memoria.";

    public static string ClassifierQuestion(string answer) => $"Message:\n{answer}";

    /// <summary>
    /// True only on an explicit yes. Anything else - a refusal, a sentence, an empty reply - leaves
    /// the answer as it is, so a classifier that misbehaves cannot swallow honest answers.
    /// </summary>
    public static bool ClaimsAnAction(string? classification)
    {
        var word = new string((classification ?? string.Empty).Trim().TakeWhile(char.IsLetter).ToArray());
        return word.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || word.Equals("si", StringComparison.OrdinalIgnoreCase)
            || word.Equals("sí", StringComparison.OrdinalIgnoreCase);
    }
}
