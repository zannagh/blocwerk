-- Anonymise a restored production dump for the staging copy (experimental.blocwerk.app).
--
-- Run it once against the restored staging database, BEFORE the app starts (see README.md):
--     psql -v ON_ERROR_STOP=1 -f tools/staging/anonymise.sql "<staging connection string>"
-- It is one transaction and idempotent: a failure changes nothing, a second run changes nothing more.
-- Table and column names are the EF model's (src/Blocwerk.Core/Entities, Migrations/BlocwerkDbContextModelSnapshot.cs).
--
-- CHANGES (personal data and anything that grants access to the real world or to the real instance)
--   Users              Email -> user-<id>@staging.invalid; LoginUsername too when it is an address (it is how that user
--                      logs in on staging then); Identifier of OAuth users (provider ids) -> staging__<id>; display names
--                      that are addresses -> "User <id8>"; avatars removed; TOTP secret/flag/last step, failed-login
--                      count and lockout reset. Password hashes are KEPT, so testers sign in with their production
--                      password. Display names that are not addresses stay. The ghost user (system:*) and local__* identifiers are left alone.
--   UserIdentities     deleted: OAuth provider account ids of real people (the Identifier above is the same data).
--   ApiKeys            deleted: personal, wall, kiosk-device and installation keys (kiosk registrations included).
--   PushSubscriptions  deleted: real browser endpoints and keys.
--   RefreshTokens      deleted: login tokens (the new DataProtection key ring ends cookie sessions as well).
--   EmailVerificationCodes  deleted: pending verify-email / password-reset / signup codes (hold the address).
--   TopLoggerConnections    deleted: encrypted TopLogger access and refresh tokens.
--   Walls              ShareToken -> NULL: the link grants read access; ChangeJournalEntries of walls lose it too.
--   WallMembers        kiosk PIN hash and length cleared (a 4-8 digit hash is trivially brute-forced).
--   GpuRunners         deleted (cascades to GpuRunnerWalls, GpuRunnerApprovals = pending approvals, GpuRunnerFailures);
--                      the app re-registers the instance runner from RECOGNITION__INSTANCEKEY on start.
-- KEPT: walls, panels, photos (bytea), holds, boulders, ticks/attempts, ratings, comments, users, memberships, roles.
-- The schema has no outbound webhook or notification target tables, nor recovery codes or security stamps; if a later
-- migration adds one, add it here.

BEGIN;

UPDATE "Users"
SET "Email"           = CASE WHEN "Email" IS NULL THEN NULL ELSE 'user-' || "Id"::text || '@staging.invalid' END,
    "LoginUsername"   = CASE WHEN "LoginUsername" LIKE '%@%' THEN 'user-' || "Id"::text || '@staging.invalid' ELSE "LoginUsername" END,
    "Identifier"      = CASE WHEN "Identifier" LIKE 'local\_\_%' THEN "Identifier" ELSE 'staging__' || "Id"::text END,
    "DisplayName"     = CASE WHEN "DisplayName" LIKE '%@%' THEN 'User ' || left("Id"::text, 8) ELSE "DisplayName" END,
    "CustomDisplayName" = CASE WHEN "CustomDisplayName" LIKE '%@%' THEN NULL ELSE "CustomDisplayName" END,
    "AvatarImage"       = NULL,
    "AvatarContentType" = NULL,
    "AvatarSha256"      = NULL,
    "TotpSecretProtected" = NULL,
    "TotpEnabled"         = FALSE,
    "TotpLastUsedStep"    = NULL,
    "FailedAuthCount"     = 0,
    "LockoutUntil"        = NULL
WHERE "Identifier" NOT LIKE 'system:%';

UPDATE "Walls" SET "ShareToken" = NULL WHERE "ShareToken" IS NOT NULL;

UPDATE "ChangeJournalEntries"
SET "BeforeJson" = regexp_replace("BeforeJson", '"ShareToken"\s*:\s*"[^"]*"', '"ShareToken":null', 'gi'),
    "AfterJson"  = regexp_replace("AfterJson",  '"ShareToken"\s*:\s*"[^"]*"', '"ShareToken":null', 'gi')
WHERE "BeforeJson" ILIKE '%ShareToken%' OR "AfterJson" ILIKE '%ShareToken%';

UPDATE "WallMembers"
SET "KioskPinHash" = NULL, "KioskPinLength" = 0
WHERE "KioskPinHash" IS NOT NULL OR "KioskPinLength" <> 0;

DELETE FROM "UserIdentities";
DELETE FROM "ApiKeys";
DELETE FROM "PushSubscriptions";
DELETE FROM "RefreshTokens";
DELETE FROM "EmailVerificationCodes";
DELETE FROM "TopLoggerConnections";
DELETE FROM "GpuRunnerApprovals";
DELETE FROM "GpuRunnerWalls";
DELETE FROM "GpuRunnerFailures";
DELETE FROM "GpuRunners";

-- What is left in the cleared tables (all must be 0), and what remains of the personal columns.
SELECT t.name AS cleared, t.n AS rows_left FROM (
    SELECT 'UserIdentities' AS name, count(*) AS n FROM "UserIdentities"
    UNION ALL SELECT 'ApiKeys', count(*) FROM "ApiKeys"
    UNION ALL SELECT 'PushSubscriptions', count(*) FROM "PushSubscriptions"
    UNION ALL SELECT 'RefreshTokens', count(*) FROM "RefreshTokens"
    UNION ALL SELECT 'EmailVerificationCodes', count(*) FROM "EmailVerificationCodes"
    UNION ALL SELECT 'TopLoggerConnections', count(*) FROM "TopLoggerConnections"
    UNION ALL SELECT 'GpuRunnerApprovals', count(*) FROM "GpuRunnerApprovals"
    UNION ALL SELECT 'GpuRunnerWalls', count(*) FROM "GpuRunnerWalls"
    UNION ALL SELECT 'GpuRunnerFailures', count(*) FROM "GpuRunnerFailures"
    UNION ALL SELECT 'GpuRunners', count(*) FROM "GpuRunners"
    UNION ALL SELECT 'Walls with ShareToken', count(*) FROM "Walls" WHERE "ShareToken" IS NOT NULL
    UNION ALL SELECT 'WallMembers with kiosk PIN', count(*) FROM "WallMembers" WHERE "KioskPinHash" IS NOT NULL
    UNION ALL SELECT 'Users with TOTP', count(*) FROM "Users" WHERE "TotpEnabled" OR "TotpSecretProtected" IS NOT NULL
    UNION ALL SELECT 'Users with a real e-mail', count(*) FROM "Users" WHERE "Email" IS NOT NULL AND "Email" NOT LIKE '%@staging.invalid'
) t ORDER BY t.name;

COMMIT;
