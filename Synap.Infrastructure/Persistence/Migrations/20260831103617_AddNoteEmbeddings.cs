using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Raw SQL, not an EF-mapped entity: per design.md Decision 1, the Python AI service owns
    /// this table entirely (writes embeddings, reads them for semantic search/RAG) - no C# type
    /// in this solution ever reads or writes a `vector` value, so there is nothing for EF Core
    /// to model here. This migration exists only so schema management for the whole database
    /// stays in one place (`dotnet ef database update`), rather than a second migration system
    /// on the Python side.
    /// </remarks>
    public partial class AddNoteEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            // BAAI/bge-small-en-v1.5 (via fastembed) produces 384-dimensional embeddings - see
            // the AI service's app/embeddings/model.py and design.md's now-resolved Open Question.
            migrationBuilder.Sql("""
                CREATE TABLE note_embeddings (
                    note_id uuid PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
                    user_id uuid NOT NULL,
                    embedding vector(384) NOT NULL,
                    updated_at timestamptz NOT NULL DEFAULT now()
                );
                """);

            // No ANN index (ivfflat/hnsw) yet: at a single user's personal-vault scale, a
            // sequential scan with the <=> operator is fast enough, and ivfflat needs a
            // reasonable amount of existing data to build good clusters. Add one (with a
            // migration) if search/related-notes latency actually becomes a problem.
            migrationBuilder.Sql("CREATE INDEX idx_note_embeddings_user_id ON note_embeddings (user_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS note_embeddings;");
        }
    }
}
