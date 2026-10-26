using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Database.Migrations
{
    /// <summary>
    /// NFR-SEC-03: <c>agents.exchange_key_id</c> may only reference a key of the agent's own user. The single-column
    /// foreign key becomes a composite one over (exchange_key_id, user_id) against the key's (id, user_id), which the
    /// database enforces; its ON DELETE SET NULL goes with it (a composite SET NULL would null user_id too), so the keys
    /// module clears the reference before it deletes a key.
    /// </summary>
    public partial class AddExchangeKeyOwnerConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agents_exchange_keys_exchange_key_id",
                table: "agents");

            migrationBuilder.DropIndex(
                name: "ix_agents_exchange_key_id",
                table: "agents");

            // NFR-SEC-03: an agent that referenced another user's key (nothing in the application could create one; this guards
            // the constraint against a hand-edited row) loses the reference rather than blocking the migration.
            migrationBuilder.Sql("UPDATE agents SET exchange_key_id = NULL WHERE exchange_key_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM exchange_keys k WHERE k.id = agents.exchange_key_id AND k.user_id = agents.user_id)");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_exchange_keys_id_user_id",
                table: "exchange_keys",
                columns: new[] { "id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agents_exchange_key_id_user_id",
                table: "agents",
                columns: new[] { "exchange_key_id", "user_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_agents_exchange_keys_exchange_key_id_user_id",
                table: "agents",
                columns: new[] { "exchange_key_id", "user_id" },
                principalTable: "exchange_keys",
                principalColumns: new[] { "id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migrations are forward-only (DB-02).");
        }
    }
}
