using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blocwerk.Core.Migrations
{
    /// <summary>
    /// Data-only repair for the holds a big update left with a DETACHED outline: their
    /// <c>"ShapePoints"</c> polygon is drawn nowhere near the hold it belongs to, so the body rendered at
    /// the hold's OLD position while its badge drew correctly, and one drag pushed the polygon clean off
    /// the 0..100 viewBox.
    /// </summary>
    /// <remarks>
    /// Cause: on the matched-twin carry path the promote overwrote a twin's own detected outline with the
    /// warp-PREDICTED outline of its predecessor. A warp predicts where the OLD hold's pixels land, so for
    /// a hold that physically MOVED the polygon describes the old place while the twin sits at the new one;
    /// rebasing those absolute vertices on the twin's centre yielded offsets of roughly (old − new). The
    /// code fix (<c>WallBigUpdateService.ApplyWarpedShape</c>) now refuses a warped polygon that does not
    /// sit on its successor; this repairs the rows already written.
    /// <para>
    /// RE-CENTRE rather than null. <c>"ShapePoints"</c> are centre-relative offsets, so the whole defect is
    /// one translation: subtracting the polygon's own vertex mean puts it back around its hold and changes
    /// nothing else about it. The alternative — nulling, so the renderer falls back to a circle
    /// (<c>WallPhotoEditor</c> needs <c>Count &gt;= 3</c>) — throws away a traced outline permanently. The
    /// shape being restored is the same PHYSICAL hold's outline as seen in the previous photo, so its size
    /// and form are right even when perspective has shifted a little; a plainly wrong circle is not better
    /// than a slightly-off true outline, and the owner can retrace either way.
    /// </para>
    /// <para>
    /// SCOPE — the predicate, and why it is NOT the radius test the code fix used to use. A hand-traced
    /// outline is drawn vertex by vertex (<c>WallPhotoEditor</c>'s Shape tool) and NEVER updates the hold's
    /// <c>"X"</c>/<c>"Y"</c>/<c>"Radius"</c>; changing the radius instead RESETS the polygon to a default
    /// octagon. So every traced rail, volume or macro keeps the stale radius of the little circle it grew
    /// out of, and its vertex mean legitimately sits several radii from the hold centre. A
    /// <c>mean &gt; 2 * radius</c> rule therefore fires on healthy, deliberately lopsided outlines and
    /// TRANSLATES them off their hold — irreversibly, at startup. (Verified on production: a correct
    /// 0.21-wide rail outline with <c>radius = 0.0052</c> and a vertex mean of 0.065 tripped that rule.)
    /// <para>
    /// The rule used instead compares the mean offset with the polygon's OWN extent — the half-diagonal of
    /// its bounding box around its vertex mean. An outline of a hold contains that hold's centre, so its
    /// mean offset is smaller than its own extent however lopsided it is; an (old − new) translation moves
    /// the whole polygon clear of the hold, so the offset exceeds the extent. Radius is not consulted at
    /// all. A small absolute floor keeps a degenerate near-zero polygon from qualifying on noise.
    /// </para>
    /// </para>
    /// <para>
    /// Also only polygons with at least 3 vertices, which is the only kind the renderer draws, and only
    /// holds at or below their wall's <c>"CurrentGeneration"</c>: rows ABOVE it are an in-flight update's
    /// staged set, whose decisions are not committed yet and which a discard is about to delete. Retained
    /// historic rows below it are included on purpose — they are equally broken and still render in the
    /// schematics of historic boulders.
    /// </para>
    /// <para>
    /// One UPDATE, no schema change, no long lock; it runs at startup via <c>db.Database.Migrate()</c>.
    /// </para>
    /// </remarks>
    public partial class RecentreDetachedHoldOutlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "ShapePoints" is the JSON serialization of List<ShapePoint> in a text column, so the
            // vertices are read through a jsonb cast and the repaired polygon is written back as text.
            // WITH ORDINALITY + ORDER BY keeps the vertex order, which is the polygon's winding.
            // jsonb_typeof guards the cast: a row holding anything but an array is skipped rather than
            // aborting the migration inside jsonb_array_elements.
            migrationBuilder.Sql(
                """
                WITH parsed AS (
                    SELECT h."Id" AS hold_id,
                           v.ord AS ord,
                           (v.vertex->>'Dx')::double precision AS dx,
                           (v.vertex->>'Dy')::double precision AS dy
                    FROM "Holds" AS h
                    JOIN "Walls" AS w ON w."Id" = h."WallId"
                    CROSS JOIN LATERAL jsonb_array_elements(h."ShapePoints"::jsonb)
                        WITH ORDINALITY AS v(vertex, ord)
                    WHERE h."ShapePoints" IS NOT NULL
                      AND jsonb_typeof(h."ShapePoints"::jsonb) = 'array'
                      AND h."Generation" <= w."CurrentGeneration"
                ),
                means AS (
                    SELECT hold_id,
                           avg(dx) AS cx,
                           avg(dy) AS cy
                    FROM parsed
                    GROUP BY hold_id
                    HAVING count(*) >= 3
                ),
                detached AS (
                    SELECT m.hold_id,
                           m.cx,
                           m.cy
                    FROM means AS m
                    JOIN parsed AS p ON p.hold_id = m.hold_id
                    GROUP BY m.hold_id, m.cx, m.cy
                    -- Mean offset beyond the polygon's own extent: the hold's centre lies outside the
                    -- circle that encloses the polygon, so this cannot be that hold's outline.
                    HAVING sqrt((m.cx * m.cx) + (m.cy * m.cy)) > 0.005
                       AND sqrt((m.cx * m.cx) + (m.cy * m.cy))
                           > sqrt((max(abs(p.dx - m.cx)) * max(abs(p.dx - m.cx)))
                                + (max(abs(p.dy - m.cy)) * max(abs(p.dy - m.cy))))
                ),
                repaired AS (
                    SELECT p.hold_id,
                           jsonb_agg(
                               jsonb_build_object('Dx', p.dx - d.cx, 'Dy', p.dy - d.cy)
                               ORDER BY p.ord) AS shape
                    FROM parsed AS p
                    JOIN detached AS d ON d.hold_id = p.hold_id
                    GROUP BY p.hold_id
                )
                UPDATE "Holds" AS h
                SET "ShapePoints" = r.shape::text
                FROM repaired AS r
                WHERE h."Id" = r.hold_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op, and irreversible: the repair is a translation by a vertex mean that is
            // no longer recorded anywhere once applied, so the original (broken) offsets cannot be
            // reconstructed — and there is no reason to want them back: they drew a hold's outline
            // somewhere the hold is not. Re-running Up is harmless, since a repaired polygon now contains
            // its hold's centre and no longer matches the predicate.
        }
    }
}
