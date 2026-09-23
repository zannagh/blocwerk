using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMarkerPlanRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "WallMarkerPlans",
                type: "integer",
                nullable: false,
                defaultValueSql: "0");

            migrationBuilder.AddColumn<int>(
                name: "PlanRevision",
                table: "WallMarkerObservations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanRevision",
                table: "WallGeometryModels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanRevision",
                table: "WallCaptures",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WallMarkerPlans_WallId_Revision",
                table: "WallMarkerPlans",
                columns: new[] { "WallId", "Revision" });

            // Number the plans saved so far per wall, oldest first (ties by id: deterministic).
            migrationBuilder.Sql(
                @"UPDATE ""WallMarkerPlans"" AS p SET ""Revision"" = r.rn
                  FROM (SELECT ""Id"", ROW_NUMBER() OVER (PARTITION BY ""WallId"" ORDER BY ""CreatedAt"", ""Id"") AS rn
                        FROM ""WallMarkerPlans"") AS r
                  WHERE p.""Id"" = r.""Id"";");

            // A capture's plan snapshot is a verbatim copy of the plan it ran with: find that revision.
            migrationBuilder.Sql(
                @"UPDATE ""WallCaptures"" AS c SET ""PlanRevision"" = (
                      SELECT MAX(p.""Revision"") FROM ""WallMarkerPlans"" AS p
                      WHERE p.""WallId"" = c.""WallId"" AND p.""Json"" = c.""PlanJson"")
                  WHERE c.""PlanJson"" IS NOT NULL;");

            // A model a capture produced was solved with that capture's plan.
            migrationBuilder.Sql(
                @"UPDATE ""WallGeometryModels"" AS m SET ""PlanRevision"" = c.""PlanRevision""
                  FROM ""WallCaptures"" AS c
                  WHERE c.""GeometryModelId"" = m.""Id"" AND c.""PlanRevision"" IS NOT NULL;");

            // An observation was detected against the plan that was current at the time.
            migrationBuilder.Sql(
                @"UPDATE ""WallMarkerObservations"" AS o SET ""PlanRevision"" = (
                      SELECT MAX(p.""Revision"") FROM ""WallMarkerPlans"" AS p
                      JOIN ""WallPanels"" AS wp ON wp.""WallId"" = p.""WallId""
                      WHERE wp.""Id"" = o.""WallPanelId"" AND p.""CreatedAt"" <= o.""DetectedAt"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WallMarkerPlans_WallId_Revision",
                table: "WallMarkerPlans");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "WallMarkerPlans");

            migrationBuilder.DropColumn(
                name: "PlanRevision",
                table: "WallMarkerObservations");

            migrationBuilder.DropColumn(
                name: "PlanRevision",
                table: "WallGeometryModels");

            migrationBuilder.DropColumn(
                name: "PlanRevision",
                table: "WallCaptures");
        }
    }
}
