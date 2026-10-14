-- One-off fix for #109: the photo archived for a past generation (WallResets.PreviousPhoto) was Wall.Photo, which a
-- crop of the centre panel leaves uncropped. The holds of that generation sit on the cropped centre panel photo, so
-- historic boulders were drawn offset. This archives the cropped panel photo instead, only where the archived photo
-- is exactly the crop's kept original. Safe to run twice (the second run changes nothing).
--
-- Run (prod box, after the release with the code fix):
--   docker compose exec -T postgres psql -U postgres -d blocwerk -v ON_ERROR_STOP=1 < archived-cropped-centre-photo.sql

BEGIN;

SELECT r."WallId", r."Generation", length(r."PreviousPhoto") AS archived_bytes, length(p."Photo") AS cropped_bytes
FROM "WallResets" r
JOIN "WallPanels" p ON p."WallId" = r."WallId" AND p."Col" = 0 AND p."Row" = 0 AND p."Generation" = r."Generation"
JOIN "WallPanelCrops" c ON c."WallPanelId" = p."Id"
WHERE p."Photo" IS NOT NULL AND r."PreviousPhoto" = c."OriginalPhoto";

UPDATE "WallResets" r
SET "PreviousPhoto" = p."Photo", "PreviousPhotoContentType" = p."PhotoContentType"
FROM "WallPanels" p
JOIN "WallPanelCrops" c ON c."WallPanelId" = p."Id"
WHERE p."WallId" = r."WallId" AND p."Col" = 0 AND p."Row" = 0 AND p."Generation" = r."Generation"
  AND p."Photo" IS NOT NULL AND r."PreviousPhoto" = c."OriginalPhoto";

COMMIT;
