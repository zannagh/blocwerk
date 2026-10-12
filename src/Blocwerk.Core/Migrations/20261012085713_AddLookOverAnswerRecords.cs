using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddLookOverAnswerRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnswerBoulders",
                table: "WallLookOverFindings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "ClearedBoulderIds",
                table: "WallLookOverFindings",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClearedHoldMark",
                table: "WallLookOverFindings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "MarkedBoulderIds",
                table: "WallLookOverFindings",
                type: "uuid[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnswerBoulders",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "ClearedBoulderIds",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "ClearedHoldMark",
                table: "WallLookOverFindings");

            migrationBuilder.DropColumn(
                name: "MarkedBoulderIds",
                table: "WallLookOverFindings");
        }
    }
}
