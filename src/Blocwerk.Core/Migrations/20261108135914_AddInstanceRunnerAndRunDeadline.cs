using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceRunnerAndRunDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RunDeadlineAt",
                table: "RecognitionJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunSeconds",
                table: "RecognitionJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<Guid>(
                name: "OwnerUserId",
                table: "GpuRunners",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "GpuRunners",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunDeadlineAt",
                table: "RecognitionJobs");

            migrationBuilder.DropColumn(
                name: "RunSeconds",
                table: "RecognitionJobs");

            // The instance runner has no owner; it cannot exist without the scope.
            migrationBuilder.Sql("DELETE FROM \"GpuRunners\" WHERE \"OwnerUserId\" IS NULL;");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "GpuRunners");

            migrationBuilder.AlterColumn<Guid>(
                name: "OwnerUserId",
                table: "GpuRunners",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
