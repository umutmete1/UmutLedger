using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UmutLedger.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AppendOnlyLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION reject_ledger_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER transfers_append_only BEFORE UPDATE OR DELETE ON transfers
                    FOR EACH ROW EXECUTE FUNCTION reject_ledger_mutation();

                CREATE TRIGGER entries_append_only BEFORE UPDATE OR DELETE ON entries
                    FOR EACH ROW EXECUTE FUNCTION reject_ledger_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER entries_append_only ON entries;
                DROP TRIGGER transfers_append_only ON transfers;
                DROP FUNCTION reject_ledger_mutation();
                """);
        }
    }
}
