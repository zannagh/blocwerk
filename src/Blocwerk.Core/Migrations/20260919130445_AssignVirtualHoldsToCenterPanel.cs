using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// Data-only migration: stamps the wall's live CENTER panel onto the virtual holds that were
    /// created before <c>WallService.AddHoldAsync</c> took a panel id, so those rows say explicitly
    /// what every read already assumes about them.
    /// </summary>
    /// <remarks>
    /// The center (Col 0, Row 0) is where BoulderDetail, BoulderCreate and BoulderRevise already draw
    /// a panel-less hold, so this changes no rendering — it only makes the data explicit. Which panel
    /// such a hold was really placed on is unrecoverable; the center is the accepted answer.
    /// <para>
    /// Scope, deliberately narrow. Only <c>"IsVirtual"</c> rows: a legacy single-image wall may keep
    /// perfectly valid panel-less real holds, and moving those would corrupt them. Only walls that
    /// actually have a live (bytes-carrying) center panel, so a wall with no panels is untouched. Only
    /// holds at the wall's own <c>"CurrentGeneration"</c> — the exact set the live reads surface today;
    /// older rows are history and <c>CurrentGeneration + 1</c> rows are in-flight staged ones. And only
    /// walls that are not single-image staged (<c>"StagedAt" IS NULL</c>), for the same reason
    /// <c>ResolvePanelStampAsync</c> refuses to stamp while staging: the staged set lives one generation
    /// ahead and a hold pinned to a live panel would vanish at promotion.
    /// </para>
    /// <para>
    /// <c>"Generation"</c> is NOT changed. It keys cross-generation lineage (HoldGenerationLink), the
    /// outdated-panel flag and the change journal, so rewriting it retroactively would reclassify
    /// history. It is also unnecessary: every read that shows these holds today resolves them by panel
    /// id (GetWallAsync's live-panel window, BoulderDetail's RenderPanelId, the editors'
    /// RefreshPickerHolds), not by generation. The one generation-keyed read, GetPanelHoldsAsync, does
    /// not return panel-less holds today either, so it cannot regress; where the center panel already
    /// sits at the wall's current generation — the normal case, only a subset promote breaks it — those
    /// holds now additionally appear there.
    /// </para>
    /// <para>
    /// One UPDATE, no schema change, no long lock; it runs at startup via <c>db.Database.Migrate()</c>.
    /// </para>
    /// </remarks>
    public partial class AssignVirtualHoldsToCenterPanel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The center panel is the LATEST generation row at (0,0) that carries bytes — a center
            // update adds a new row and the superseded one keeps its photo, and the live reads all
            // resolve the position to the newest live row. Matching that here is what keeps the
            // stamped hold inside the live-panel window.
            migrationBuilder.Sql(
                """
                UPDATE "Holds" AS h
                SET "WallPanelId" = c."Id"
                FROM "Walls" AS w
                CROSS JOIN LATERAL (
                    SELECT p."Id"
                    FROM "WallPanels" AS p
                    WHERE p."WallId" = w."Id"
                      AND p."Col" = 0
                      AND p."Row" = 0
                      AND p."Photo" IS NOT NULL
                    ORDER BY p."Generation" DESC
                    LIMIT 1
                ) AS c
                WHERE h."WallId" = w."Id"
                  AND h."IsVirtual"
                  AND h."WallPanelId" IS NULL
                  AND h."Generation" = w."CurrentGeneration"
                  AND w."StagedAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op. Nothing records which virtual holds were panel-less before Up ran,
            // so the only reversal expressible here — nulling the center-panel stamp on virtual holds —
            // would also strip legitimately stamped holds that AddHoldAsync, merge or promote assigned,
            // which is strictly worse than leaving the data as it is. Re-running Up is harmless
            // (already-stamped rows no longer match), so a rollback loses nothing.
        }
    }
}
