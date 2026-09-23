using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAlignmentFailureFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "UnalignedCarryPanelIds",
                table: "WallUpdateSessions",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "UnalignedOverlapPanelIds",
                table: "WallUpdateSessions",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnalignedCarryPanelIds",
                table: "WallUpdateSessions");

            migrationBuilder.DropColumn(
                name: "UnalignedOverlapPanelIds",
                table: "WallUpdateSessions");
        }
    }
}
