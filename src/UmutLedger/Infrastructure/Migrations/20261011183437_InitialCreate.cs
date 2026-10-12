using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace UmutLedger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_currency", "currency IN ('USD', 'EUR', 'GBP')");
                    table.CheckConstraint("ck_accounts_customer_balance_non_negative", "type = 'System' OR balance >= 0");
                    table.CheckConstraint("ck_accounts_name_not_empty", "length(trim(name)) > 0");
                    table.CheckConstraint("ck_accounts_type", "type IN ('Customer', 'System')");
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    debit_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfers", x => x.id);
                    table.CheckConstraint("ck_transfers_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_transfers_currency", "currency IN ('USD', 'EUR', 'GBP')");
                    table.CheckConstraint("ck_transfers_distinct_accounts", "debit_account_id <> credit_account_id");
                    table.ForeignKey(
                        name: "fk_transfers_credit_account",
                        column: x => x.credit_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_transfers_debit_account",
                        column: x => x.debit_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transfer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                    table.CheckConstraint("ck_entries_amount_not_zero", "amount <> 0");
                    table.ForeignKey(
                        name: "fk_entries_account",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entries_transfer",
                        column: x => x.transfer_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "accounts",
                columns: new[] { "id", "balance", "created_at", "currency", "name", "type" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-7000-8000-000000000001"), 0L, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "USD", "World USD", "System" },
                    { new Guid("00000000-0000-7000-8000-000000000002"), 0L, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "EUR", "World EUR", "System" },
                    { new Guid("00000000-0000-7000-8000-000000000003"), 0L, new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "GBP", "World GBP", "System" }
                });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_system_currency",
                table: "accounts",
                column: "currency",
                unique: true,
                filter: "type = 'System'");

            migrationBuilder.CreateIndex(
                name: "IX_entries_transfer_id",
                table: "entries",
                column: "transfer_id");

            migrationBuilder.CreateIndex(
                name: "ix_entries_account_id_id",
                table: "entries",
                columns: new[] { "account_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_transfers_credit_account_id",
                table: "transfers",
                column: "credit_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_transfers_debit_account_id",
                table: "transfers",
                column: "debit_account_id");

            migrationBuilder.CreateIndex(
                name: "ux_transfers_idempotency_key",
                table: "transfers",
                column: "idempotency_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries");

            migrationBuilder.DropTable(
                name: "transfers");

            migrationBuilder.DropTable(
                name: "accounts");
        }
    }
}
