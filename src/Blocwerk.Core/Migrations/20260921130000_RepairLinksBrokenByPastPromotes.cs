using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// One-off data repair for the cross-panel <c>"HoldLinks"</c> that a PAST big update left pointing at
    /// retired holds. Walks each dead end forward through the <c>"HoldGenerationLinks"</c> lineage to the
    /// hold that replaced it and re-states the link between the successors; deletes the ones whose holds
    /// are genuinely gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the code fix is not enough. <c>WallBigUpdateService.CarryHoldLinksAsync</c> now projects a
    /// wall's links onto the successors at every promote, but it only knows about the holds in THAT
    /// promote's scope. A link broken by an EARLIER promote has both ends one or more generations back, so
    /// the carry reads both as out-of-scope, leaves them where they are, and the row stays dead for ever.
    /// The owner's wall is exactly this case: all 103 links have both ends at generation 2 while the wall
    /// is at 3. Nothing the code does will ever rescue them.
    /// </para>
    /// <para>
    /// Why it is safe to do automatically. <c>"HoldGenerationLinks"</c> is a complete old→new record of
    /// every carried hold — verified on production: 678 rows for the 2→3 bump (663 Same, 15 Changed), no
    /// old hold with two successors, and the walk resolves 79 of the 103 links to a distinct, live pair
    /// with no collision against an existing link. This is a mechanical re-pointing of a row the owner
    /// created deliberately, not a guess about which holds belong together: the lineage says which hold
    /// each end BECAME, and the link's meaning ("these two panel views are one physical hold") is
    /// preserved exactly. Kind, creator and creation time ride along, so provenance is not invented.
    /// </para>
    /// <para>
    /// The remaining 24 are DELETED rather than kept. Their ends resolve to a hold that is not on the
    /// newest panel at its grid position, i.e. the physical hold was removed at a promote — two of them
    /// have no lineage row at all, which is what a removal looks like (NewHoldId null) —
    /// the same case <c>ResolveEnd</c> handles by dropping the link. Leaving them is not neutral: every
    /// reader (the appearance backfill, the appearance sync, the link tool) walks the wall's links as one
    /// undirected graph, so a dead row fuses a RETIRED hold into a live component and lets it act as a
    /// source of truth. That is the stale-rows-feed-retired-holds defect, and deleting is what fixes it.
    /// </para>
    /// <para>
    /// A hold whose panel was never re-shot is NOT retired, and the liveness test is panel-based for
    /// exactly that reason: an end counts as live when its hold sits on the newest panel at its (Col, Row)
    /// — or has no panel at all, the legacy single-photo case — not when its generation equals the wall's.
    /// A subset promote leaves untouched panels behind at an older generation and their holds are still
    /// the live ones; a generation comparison would delete those links wrongly.
    /// </para>
    /// <para>
    /// Deletes run BEFORE the rewrites so a rewrite can never collide with a dead row on the unique
    /// (HoldAId, HoldBId) index, and the rewrite set is deduplicated on the unordered pair — keeping the
    /// oldest row of any group — so two links that resolve onto the same successor pair cannot both be
    /// written. A self-link (both ends onto one successor, i.e. the two panel copies merged) is deleted.
    /// </para>
    /// </remarks>
    public partial class RepairLinksBrokenByPastPromotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Classify every link once into a temp table, so the delete pass and the rewrite pass cannot
            // disagree about what they are looking at. Plain TEMP + explicit DROP rather than
            // ON COMMIT DROP, which depends on the migration running inside a transaction.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE bwk_link_repair AS
                WITH RECURSIVE
                lineage AS (
                    SELECT "OldHoldId" AS old_id, "NewHoldId" AS new_id
                    FROM "HoldGenerationLinks"
                    WHERE "OldHoldId" IS NOT NULL AND "NewHoldId" IS NOT NULL
                ),
                -- The newest panel at each grid position. A hold on an older one is retired; a hold on
                -- this one is live even when its generation is behind the wall's (untouched panel).
                live_panel AS (
                    SELECT DISTINCT ON (p."WallId", p."Col", p."Row") p."Id"
                    FROM "WallPanels" AS p
                    ORDER BY p."WallId", p."Col", p."Row", p."Generation" DESC
                ),
                ends AS (
                    SELECT "HoldAId" AS hold_id FROM "HoldLinks"
                    UNION
                    SELECT "HoldBId" FROM "HoldLinks"
                ),
                -- Follow old→new until the chain stops. Depth-capped so a malformed lineage cycle cannot
                -- spin; the wall only needs one hop today, but a link broken two updates ago needs two.
                walk(start_id, cur_id, depth) AS (
                    SELECT hold_id, hold_id, 0 FROM ends
                    UNION ALL
                    SELECT w.start_id, l.new_id, w.depth + 1
                    FROM walk AS w
                    JOIN lineage AS l ON l.old_id = w.cur_id
                    WHERE w.depth < 20
                ),
                resolved AS (
                    SELECT DISTINCT ON (start_id) start_id, cur_id AS end_id, depth
                    FROM walk
                    ORDER BY start_id, depth DESC, cur_id
                ),
                usable AS (
                    SELECT r.start_id,
                           r.end_id,
                           r.depth,
                           (lp."Id" IS NOT NULL OR h."WallPanelId" IS NULL) AS is_live
                    FROM resolved AS r
                    JOIN "Holds" AS h ON h."Id" = r.end_id
                    LEFT JOIN live_panel AS lp ON lp."Id" = h."WallPanelId"
                )
                SELECT l."Id" AS link_id,
                       l."CreatedAt" AS created_at,
                       ua.end_id AS new_a,
                       ub.end_id AS new_b,
                       (ua.depth > 0 OR ub.depth > 0) AS moved,
                       (ua.is_live AND ub.is_live AND ua.end_id <> ub.end_id) AS keepable,
                       least(ua.end_id, ub.end_id) AS pair_low,
                       greatest(ua.end_id, ub.end_id) AS pair_high
                FROM "HoldLinks" AS l
                JOIN usable AS ua ON ua.start_id = l."HoldAId"
                JOIN usable AS ub ON ub.start_id = l."HoldBId";
                """);

            // Dead ends, and the duplicates a rewrite would collide with. Oldest row of a duplicate group
            // wins, so the surviving link keeps the earliest provenance.
            migrationBuilder.Sql(
                """
                DELETE FROM "HoldLinks" AS l
                USING bwk_link_repair AS r
                WHERE r.link_id = l."Id"
                  AND (
                      -- Dead: an end walks to a hold that is no longer on the live panel at its
                      -- position. Not gated on `moved`, because a hold REMOVED at a promote has no
                      -- lineage successor at all and so does not walk anywhere — it is still dead.
                      NOT r.keepable
                      OR EXISTS (
                          SELECT 1 FROM bwk_link_repair AS o
                          WHERE r.moved
                            AND o.keepable
                            AND o.pair_low = r.pair_low
                            AND o.pair_high = r.pair_high
                            AND (o.created_at, o.link_id) < (r.created_at, r.link_id)
                      )
                      OR EXISTS (
                          SELECT 1 FROM "HoldLinks" AS e
                          WHERE r.moved
                            AND e."Id" <> l."Id"
                            AND least(e."HoldAId", e."HoldBId") = r.pair_low
                            AND greatest(e."HoldAId", e."HoldBId") = r.pair_high
                            AND NOT EXISTS (
                                SELECT 1 FROM bwk_link_repair AS x
                                WHERE x.link_id = e."Id" AND x.moved
                            )
                      )
                  );
                """);

            // Re-point what is left onto the successors, keeping the original A/B orientation.
            migrationBuilder.Sql(
                """
                UPDATE "HoldLinks" AS l
                SET "HoldAId" = r.new_a,
                    "HoldBId" = r.new_b
                FROM bwk_link_repair AS r
                WHERE r.link_id = l."Id"
                  AND r.moved
                  AND r.keepable
                  AND (l."HoldAId" <> r.new_a OR l."HoldBId" <> r.new_b);
                """);

            migrationBuilder.Sql("""DROP TABLE bwk_link_repair;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op, and honestly irreversible. Nothing records which hold each repaired
            // link pointed at before, and the deleted rows are gone; reconstructing them would mean
            // walking the lineage BACKWARDS and re-creating links onto retired holds — which is the
            // broken state this exists to leave behind, not something worth restoring. Re-running Up is
            // a no-op: a repaired link's ends no longer walk anywhere (moved = false).
        }
    }
}
