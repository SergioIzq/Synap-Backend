using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Full-text search that works for Spanish and ignores accents, backed by an index instead of
    /// computing to_tsvector on every query (backend-hardening design.md Decision 4). Raw SQL,
    /// like AddNoteEmbeddings: the column is only read by NoteReadRepository (Dapper), so EF
    /// never needs to model it - and EF inserts simply skip a column it doesn't know about.
    /// </summary>
    public partial class AddNoteSearchVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");

            // Spanish stemming ("notas" -> "nota") with accents stripped first
            // ("configuración" -> "configuracion").
            migrationBuilder.Sql("""
                CREATE TEXT SEARCH CONFIGURATION public.spanish_unaccent (COPY = pg_catalog.spanish);
                ALTER TEXT SEARCH CONFIGURATION public.spanish_unaccent
                    ALTER MAPPING FOR hword, hword_part, word WITH public.unaccent, pg_catalog.spanish_stem;
                """);

            // Generated columns require IMMUTABLE expressions; to_tsvector with a named
            // configuration is only STABLE, hence this wrapper (the configuration never changes
            // under the column without a migration).
            migrationBuilder.Sql("""
                CREATE FUNCTION public.synap_note_search_vector(title text, content text)
                RETURNS tsvector
                LANGUAGE sql
                IMMUTABLE PARALLEL SAFE
                AS $$
                    SELECT setweight(to_tsvector('public.spanish_unaccent', coalesce(title, '')), 'A')
                        || setweight(to_tsvector('public.spanish_unaccent', coalesce(content, '')), 'B')
                $$;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE notes
                    ADD COLUMN search_vector tsvector
                    GENERATED ALWAYS AS (public.synap_note_search_vector(title, content)) STORED;
                """);

            migrationBuilder.Sql("CREATE INDEX idx_notes_search_vector ON notes USING GIN (search_vector);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_notes_search_vector;");
            migrationBuilder.Sql("ALTER TABLE notes DROP COLUMN IF EXISTS search_vector;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.synap_note_search_vector(text, text);");
            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION IF EXISTS public.spanish_unaccent;");
            // The unaccent extension is left installed: harmless, and it may be used elsewhere.
        }
    }
}
