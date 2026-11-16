using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRiskFactors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Changed by hand. As it was generated, each column had 0 as its default, which is
            // below the floor of either setting. They are added without a value instead, filled,
            // and only then required.
            migrationBuilder.AddColumn<decimal>(
                name: "liquidation_buffer",
                table: "agents",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "min_reward_to_risk",
                table: "agents",
                type: "numeric",
                nullable: true);

            // An agent that is stored when this is applied is given the defaults of the
            // specification, which the tracked configuration file carries (SRS Appendix B): until
            // now both were single values of the file, the same for every agent. A migration
            // reads no file, so a deployment whose file has other values finds these here, and
            // the user of an agent changes them on its form. From here on each is a setting of
            // the agent (FR-RSK-2). See docs/agents.md.
            migrationBuilder.Sql(
                """
                UPDATE agents
                SET min_reward_to_risk = 1.5,
                    liquidation_buffer = 2.0;
                """);

            migrationBuilder.AlterColumn<decimal>(
                name: "liquidation_buffer",
                table: "agents",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "min_reward_to_risk",
                table: "agents",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "liquidation_buffer",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "min_reward_to_risk",
                table: "agents");
        }
    }
}
