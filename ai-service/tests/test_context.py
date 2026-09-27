"""scoped-assistant task 1.2 - the notes context sent with a scoped question stays within the
character budget, cutting at line/paragraph boundaries and saying when something was left out."""

from app.llm.context import NOTE_SEPARATOR, build_note_context, build_notes_context, fits, format_note, truncate


def _note(i, content, title=None):
    return {"id": f"n{i}", "title": title if title is not None else f"Nota {i}", "content": content, "note_type": "Text"}


def test_short_note_is_sent_whole():
    note = _note(1, "Reinicia el contenedor.")

    ctx = build_note_context(note, 1_000)

    assert ctx.text == "[Nota 1]\nReinicia el contenedor."
    assert ctx.sources == [note]
    assert ctx.partial is False


def test_long_note_is_cut_at_a_line_break_and_marked_partial():
    content = "\n".join(f"línea {i} " + "x" * 40 for i in range(100))

    ctx = build_note_context(_note(1, content), 1_000)

    assert len(ctx.text) <= 1_000
    assert ctx.partial is True
    # Cut at a line boundary: the last kept line is complete.
    assert ctx.text.splitlines()[-1].endswith("x" * 40)


def test_truncate_without_breaks_cuts_hard():
    assert truncate("a" * 50, 10) == "a" * 10
    assert truncate("corto", 10) == "corto"


def test_untitled_note_is_labelled():
    assert format_note({"title": None, "content": "c"}) == "[Untitled]\nc"


def test_small_tag_includes_every_note_in_order():
    notes = [_note(i, f"contenido {i}") for i in range(3)]

    ctx = build_notes_context(notes, 10_000)

    assert fits(notes, 10_000)
    assert ctx.sources == notes
    assert ctx.text == NOTE_SEPARATOR.join(format_note(n) for n in notes)
    assert ctx.partial is False


def test_large_tag_includes_the_first_notes_that_fit():
    notes = [_note(i, "y" * 900) for i in range(10)]

    ctx = build_notes_context(notes, 3_000)

    assert not fits(notes, 3_000)
    assert len(ctx.text) <= 3_000
    assert ctx.partial is True
    assert [n["id"] for n in ctx.sources] == ["n0", "n1", "n2"]


def test_large_tag_truncates_a_big_note_when_there_is_room():
    notes = [_note(0, "a" * 100), _note(1, "b" * 5_000)]

    ctx = build_notes_context(notes, 3_000)

    assert [n["id"] for n in ctx.sources] == ["n0", "n1"]
    assert len(ctx.text) <= 3_000
    assert ctx.partial is True


def test_first_note_is_never_dropped_even_if_bigger_than_the_budget():
    ctx = build_notes_context([_note(0, "z" * 5_000)], 1_000)

    assert [n["id"] for n in ctx.sources] == ["n0"]
    assert len(ctx.text) <= 1_000
