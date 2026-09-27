"""Builds the notes context sent with a scoped question (scoped-assistant design.md Decision 3).

Everything is measured in characters against a single budget, so one question never sends an
unbounded amount of text to the user's own (free-tier) Groq key.
"""

from dataclasses import dataclass

NOTE_SEPARATOR = "\n\n---\n\n"

# A note that doesn't fit whole is only worth including cut down if this much room is left.
MIN_PARTIAL_CHARS = 1_000


@dataclass(frozen=True)
class NotesContext:
    text: str
    sources: list[dict]
    # True when a note was cut down or left out to stay within the budget.
    partial: bool


def format_note(note: dict) -> str:
    return f"[{note['title'] or 'Untitled'}]\n{note['content']}"


def truncate(text: str, limit: int) -> str:
    """The start of `text` in at most `limit` characters, cut at a paragraph or line break
    when there is one in the second half of the allowance."""
    if len(text) <= limit:
        return text
    head = text[:limit]
    for boundary in ("\n\n", "\n"):
        cut = head.rfind(boundary)
        if cut >= limit // 2:
            return head[:cut].rstrip()
    return head.rstrip()


def build_note_context(note: dict, budget: int) -> NotesContext:
    formatted = format_note(note)
    text = truncate(formatted, budget)
    return NotesContext(text=text, sources=[note], partial=len(text) < len(formatted))


def build_notes_context(notes: list[dict], budget: int) -> NotesContext:
    """Adds `notes` in the given order (most relevant first) until the budget is used up."""
    parts: list[str] = []
    sources: list[dict] = []
    used = 0
    partial = False

    for note in notes:
        separator = len(NOTE_SEPARATOR) if parts else 0
        remaining = budget - used - separator
        formatted = format_note(note)

        if len(formatted) <= remaining:
            parts.append(formatted)
            sources.append(note)
            used += separator + len(formatted)
            continue

        partial = True
        if remaining >= MIN_PARTIAL_CHARS or not parts:
            parts.append(truncate(formatted, max(remaining, 0)))
            sources.append(note)
        break

    return NotesContext(text=NOTE_SEPARATOR.join(parts), sources=sources, partial=partial)


def fits(notes: list[dict], budget: int) -> bool:
    return sum(len(format_note(n)) for n in notes) + len(NOTE_SEPARATOR) * max(len(notes) - 1, 0) <= budget
