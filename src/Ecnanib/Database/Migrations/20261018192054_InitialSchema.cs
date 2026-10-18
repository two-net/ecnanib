using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ecnanib.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candles",
                columns: table => new
                {
                    environment = table.Column<string>(type: "text", nullable: false),
                    market_type = table.Column<string>(type: "text", nullable: false),
                    symbol = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    interval = table.Column<string>(type: "text", nullable: false),
                    open_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    close_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<decimal>(type: "numeric", nullable: false),
                    high = table.Column<decimal>(type: "numeric", nullable: false),
                    low = table.Column<decimal>(type: "numeric", nullable: false),
                    close = table.Column<decimal>(type: "numeric", nullable: false),
                    volume = table.Column<decimal>(type: "numeric", nullable: false),
                    quote_volume = table.Column<decimal>(type: "numeric", nullable: false),
                    trade_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candles", x => new { x.environment, x.market_type, x.symbol, x.interval, x.open_time });
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "log_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    component = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    exception = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    context = table.Column<JsonElement>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_log_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    input_token_limit = table.Column<int>(type: "integer", nullable: true),
                    output_token_limit = table.Column<int>(type: "integer", nullable: true),
                    supports_text_generation = table.Column<bool>(type: "boolean", nullable: true),
                    capabilities = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    missed_syncs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_models", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_sync_states",
                columns: table => new
                {
                    provider = table.Column<string>(type: "text", nullable: false),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    model_count = table.Column<int>(type: "integer", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_sync_states", x => x.provider);
                });

            migrationBuilder.CreateTable(
                name: "system_keys",
                columns: table => new
                {
                    purpose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_keys", x => x.purpose);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    theme = table.Column<string>(type: "text", nullable: false),
                    reduced_motion = table.Column<bool>(type: "boolean", nullable: false),
                    disclaimer_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "disclaimer_acceptances",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disclaimer_acceptances", x => x.id);
                    table.ForeignKey(
                        name: "fk_disclaimer_acceptances_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "exchange_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    allow_spot = table.Column<bool>(type: "boolean", nullable: false),
                    allow_futures = table.Column<bool>(type: "boolean", nullable: false),
                    api_key = table.Column<string>(type: "text", nullable: false),
                    api_secret = table.Column<string>(type: "text", nullable: false),
                    permissions_snapshot = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_validation_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_exchange_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    api_key = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_validation_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_provider_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    market_type = table.Column<string>(type: "text", nullable: false),
                    symbol = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    timeframe = table.Column<string>(type: "text", nullable: false),
                    strategy_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    strategy_version = table.Column<int>(type: "integer", nullable: false),
                    parameters = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    exchange_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    leverage = table.Column<int>(type: "integer", nullable: true),
                    margin_type = table.Column<string>(type: "text", nullable: true),
                    discretionary = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    pause_cause = table.Column<string>(type: "text", nullable: true),
                    auto_resume_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    risk_limits = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_agents_exchange_keys_exchange_key_id",
                        column: x => x.exchange_key_id,
                        principalTable: "exchange_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_agents_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_changes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    field = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    old_value = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    new_value = table.Column<JsonElement>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_changes_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "council_assignments",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_council_assignments", x => new { x.agent_id, x.role });
                    table.CheckConstraint("ck_council_assignments_risk_manager_enabled", "NOT (role = 'RiskManager' AND enabled = false)");
                    table.ForeignKey(
                        name: "fk_council_assignments_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entry_windows",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    start_day = table.Column<int>(type: "integer", nullable: true),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    end_day = table.Column<int>(type: "integer", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entry_windows", x => x.id);
                    table.CheckConstraint("ck_entry_windows_kind", "(kind = 'OneOff' AND start_at IS NOT NULL AND end_at IS NOT NULL AND start_day IS NULL AND start_time IS NULL AND end_day IS NULL AND end_time IS NULL) OR (kind = 'Recurring' AND start_day IS NOT NULL AND start_time IS NOT NULL AND end_day IS NOT NULL AND end_time IS NOT NULL AND start_at IS NULL AND end_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_entry_windows_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evaluations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    symbol = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    market_type = table.Column<string>(type: "text", nullable: false),
                    timeframe = table.Column<string>(type: "text", nullable: false),
                    candle_open_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    strategy_output = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    council_consulted = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    outcome_detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evaluations", x => x.id);
                    table.ForeignKey(
                        name: "fk_evaluations_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "experience_stats",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    scope = table.Column<string>(type: "text", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    timeframe = table.Column<string>(type: "text", nullable: false),
                    market_type = table.Column<string>(type: "text", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    metrics = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_experience_stats", x => x.id);
                    table.CheckConstraint("ck_experience_stats_scope", "(scope = 'System' AND agent_id IS NULL AND user_id IS NULL) OR (scope = 'User' AND agent_id IS NULL AND user_id IS NOT NULL) OR (scope = 'Agent' AND agent_id IS NOT NULL AND user_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_experience_stats_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_experience_stats_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decision_role_outputs",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    proposed_action = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<decimal>(type: "numeric", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_role_outputs", x => new { x.decision_id, x.role });
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    final_action = table.Column<string>(type: "text", nullable: false),
                    entry_type = table.Column<string>(type: "text", nullable: true),
                    entry_price = table.Column<decimal>(type: "numeric", nullable: true),
                    stop_price = table.Column<decimal>(type: "numeric", nullable: true),
                    targets = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    size = table.Column<decimal>(type: "numeric", nullable: true),
                    risk_percent = table.Column<decimal>(type: "numeric", nullable: true),
                    confidence = table.Column<decimal>(type: "numeric", nullable: true),
                    rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    limits_check = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    total_input_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_latency_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_decisions_evaluations_evaluation_id",
                        column: x => x.evaluation_id,
                        principalTable: "evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deliberation_transcripts",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    input_summary = table.Column<string>(type: "text", nullable: false),
                    output = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliberation_transcripts", x => new { x.decision_id, x.role });
                    table.ForeignKey(
                        name: "fk_deliberation_transcripts_decisions_decision_id",
                        column: x => x.decision_id,
                        principalTable: "decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shadow_outcomes",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    r_multiple = table.Column<decimal>(type: "numeric", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    detail = table.Column<JsonElement>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shadow_outcomes", x => x.decision_id);
                    table.ForeignKey(
                        name: "fk_shadow_outcomes_decisions_decision_id",
                        column: x => x.decision_id,
                        principalTable: "decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trades",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    symbol = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    market_type = table.Column<string>(type: "text", nullable: false),
                    timeframe = table.Column<string>(type: "text", nullable: false),
                    side = table.Column<string>(type: "text", nullable: false),
                    entry_price = table.Column<decimal>(type: "numeric", nullable: false),
                    exit_price = table.Column<decimal>(type: "numeric", nullable: true),
                    size = table.Column<decimal>(type: "numeric", nullable: false),
                    current_stop = table.Column<decimal>(type: "numeric", nullable: true),
                    stop_history = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    targets = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    fees = table.Column<decimal>(type: "numeric", nullable: false),
                    funding = table.Column<decimal>(type: "numeric", nullable: false),
                    realized_pnl = table.Column<decimal>(type: "numeric", nullable: true),
                    r_multiple = table.Column<decimal>(type: "numeric", nullable: true),
                    max_adverse_excursion = table.Column<decimal>(type: "numeric", nullable: true),
                    max_favorable_excursion = table.Column<decimal>(type: "numeric", nullable: true),
                    exit_reason = table.Column<string>(type: "text", nullable: true),
                    is_paper = table.Column<bool>(type: "boolean", nullable: false),
                    is_unmanaged = table.Column<bool>(type: "boolean", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trades", x => x.id);
                    table.ForeignKey(
                        name: "fk_trades_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_trades_decisions_entry_decision_id",
                        column: x => x.entry_decision_id,
                        principalTable: "decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trade_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_order_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    exchange_order_id = table.Column<long>(type: "bigint", nullable: true),
                    order_list_id = table.Column<long>(type: "bigint", nullable: true),
                    list_client_order_id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    side = table.Column<string>(type: "text", nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: true),
                    stop_price = table.Column<decimal>(type: "numeric", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    filled_quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    average_fill_price = table.Column<decimal>(type: "numeric", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    reduce_only = table.Column<bool>(type: "boolean", nullable: false),
                    is_paper = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    filled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_orders_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_orders_decisions_decision_id",
                        column: x => x.decision_id,
                        principalTable: "decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_orders_trades_trade_id",
                        column: x => x.trade_id,
                        principalTable: "trades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_changes_agent_id_timestamp",
                table: "agent_changes",
                columns: new[] { "agent_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_agents_exchange_key_id",
                table: "agents",
                column: "exchange_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_agents_state",
                table: "agents",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_agents_user_id",
                table: "agents",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_candles_open_time",
                table: "candles",
                column: "open_time");

            migrationBuilder.CreateIndex(
                name: "ix_decisions_evaluation_id",
                table: "decisions",
                column: "evaluation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_decisions_trade_id",
                table: "decisions",
                column: "trade_id");

            migrationBuilder.CreateIndex(
                name: "ix_deliberation_transcripts_created_at",
                table: "deliberation_transcripts",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_disclaimer_acceptances_user_id_version",
                table: "disclaimer_acceptances",
                columns: new[] { "user_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_entry_windows_agent_id",
                table: "entry_windows",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_evaluations_agent_id_candle_open_time",
                table: "evaluations",
                columns: new[] { "agent_id", "candle_open_time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_exchange_keys_user_id_label",
                table: "exchange_keys",
                columns: new[] { "user_id", "label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_experience_stats_agent_id",
                table: "experience_stats",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_experience_stats_scope_agent_id_user_id_provider_model_role",
                table: "experience_stats",
                columns: new[] { "scope", "agent_id", "user_id", "provider", "model", "role", "timeframe", "market_type" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_experience_stats_user_id",
                table: "experience_stats",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_log_entries_agent_id_timestamp",
                table: "log_entries",
                columns: new[] { "agent_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_log_entries_timestamp",
                table: "log_entries",
                column: "timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_log_entries_user_id_timestamp",
                table: "log_entries",
                columns: new[] { "user_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_agent_id_created_at",
                table: "orders",
                columns: new[] { "agent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_client_order_id",
                table: "orders",
                column: "client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_decision_id",
                table: "orders",
                column: "decision_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_exchange_order_id",
                table: "orders",
                column: "exchange_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_trade_id",
                table: "orders",
                column: "trade_id");

            migrationBuilder.CreateIndex(
                name: "ix_provider_keys_user_id_provider",
                table: "provider_keys",
                columns: new[] { "user_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_models_provider_model_id",
                table: "provider_models",
                columns: new[] { "provider", "model_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shadow_outcomes_pending",
                table: "shadow_outcomes",
                column: "result",
                filter: "result = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_trades_agent_id_open",
                table: "trades",
                column: "agent_id",
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_trades_agent_id_opened_at",
                table: "trades",
                columns: new[] { "agent_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_trades_entry_decision_id",
                table: "trades",
                column: "entry_decision_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                table: "users",
                column: "normalized_email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_decision_role_outputs_decisions_decision_id",
                table: "decision_role_outputs",
                column: "decision_id",
                principalTable: "decisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_decisions_trades_trade_id",
                table: "decisions",
                column: "trade_id",
                principalTable: "trades",
                principalColumn: "id");
        }

        /// <summary>Migrations are forward-only (DB-02); the schema is never reverted.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migrations are forward-only (DB-02).");
        }
    }
}
