using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Raw SQL like AddNoteEmbeddings: the AI service owns note_embeddings. `model` records which
    /// embedding model produced each vector, so the AI service can reindex rows from any other
    /// model in the background (assistant-agent-foundations design.md Decision 5). Existing rows
    /// were all produced by the previous model. The chosen multilingual model is also
    /// 384-dimensional, so `embedding` keeps its type.
    /// </remarks>
    public partial class AddEmbeddingModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE note_embeddings ADD COLUMN model text NOT NULL DEFAULT 'BAAI/bge-small-en-v1.5';");
            migrationBuilder.Sql("CREATE INDEX idx_note_embeddings_user_id_model ON note_embeddings (user_id, model);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS idx_note_embeddings_user_id_model;");
            migrationBuilder.Sql("ALTER TABLE note_embeddings DROP COLUMN IF EXISTS model;");
        }
    }
}
