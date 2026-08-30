using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGymGradeCalibration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FlashBonusPoints",
                table: "ExternalGyms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GymGradePoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalGymId = table.Column<Guid>(type: "uuid", nullable: false),
                    Grade = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GymGradePoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GymGradePoints_ExternalGyms_ExternalGymId",
                        column: x => x.ExternalGymId,
                        principalTable: "ExternalGyms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GymGradePoints_ExternalGymId_Grade",
                table: "GymGradePoints",
                columns: new[] { "ExternalGymId", "Grade" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GymGradePoints");

            migrationBuilder.DropColumn(
                name: "FlashBonusPoints",
                table: "ExternalGyms");
        }
    }
}
