using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Seeds one "leaver" who has touched every user-referencing table in the schema, so a deletion
/// test can assert on each of them instead of on a happy-path subset.
/// </summary>
/// <remarks>
/// The leaver is a MEMBER of the harness wall, not its owner, so ownership never blocks the
/// deletion; the wall-ownership tests delete <c>harness.Owner</c> instead.
/// </remarks>
public sealed class DeletionFixture
{
    public const string LeaverEmail = "leaver@example.test";
    public const string LeaverSubject = "leaver-oauth-subject";

    /// <summary>A subject a SECOND account also answers to, on a different provider.</summary>
    public const string SharedSubject = "shared-oauth-subject";

    private DeletionFixture(
        AccountDeletionService service,
        IBetaVideoStorage betaVideoStorage,
        Guid leaverId,
        Guid boulderId)
    {
        Service = service;
        BetaVideoStorage = betaVideoStorage;
        LeaverId = leaverId;
        BoulderId = boulderId;
    }

    public AccountDeletionService Service { get; }

    public IBetaVideoStorage BetaVideoStorage { get; }

    public Guid LeaverId { get; }

    public Guid BoulderId { get; }

    public static async Task<DeletionFixture> CreateAsync(WallTestHarness harness)
    {
        await harness.SeedWallAsync();

        var betaVideoStorage = Substitute.For<IBetaVideoStorage>();
        var service = CreateService(harness, betaVideoStorage);

        var leaver = new User
        {
            Identifier = $"leaver__{LeaverSubject}",
            DisplayName = "Leaver Legalname",
            CustomDisplayName = "Crimper",
            Email = LeaverEmail,
            EmailVerified = true,
            LoginUsername = "leaver",
            PasswordHash = "kdf$leaver-hash",
            TotpSecretProtected = "protected-secret",
            TotpEnabled = true,
            TotpLastUsedStep = 42,
            FailedAuthCount = 3,
            LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(5),
            AvatarImage = [7, 7, 7],
            AvatarContentType = "image/jpeg",
            Role = IdentityRole.Admin,
            HomeWallId = harness.WallId,
        };

        Guid boulderId;

        await using (var db = harness.CreateContext())
        {
            db.Users.Add(leaver);
            db.WallMembers.Add(new WallMember
            {
                WallId = harness.WallId,
                UserId = leaver.Id,
                Role = WallRole.Member,
                KioskConsentedAt = DateTimeOffset.UtcNow,
                KioskPinHash = "kdf$pin-hash",
                KioskPinLength = 4,
            });

            // A wall-scoped key minted by the wall's OWNER: it authorises the wall's hardware, not a
            // person, so the ownership tests can check it follows the wall instead of dying.
            db.ApiKeys.Add(new ApiKey
            {
                Name = "wall sensor",
                Scope = ApiKeyScope.Wall,
                UserId = harness.Owner.Id,
                WallId = harness.WallId,
                KeyHash = "sha256-wall",
                Prefix = "bwk_bbb",
            });

            SeedCredentials(db, leaver);
            SeedStrangersArtefacts(db);
            SeedPrivateHistory(db, leaver, harness.WallId);
            boulderId = SeedAuthoredContent(db, leaver, harness.WallId);

            await db.SaveChangesAsync();
        }

        // Deletion is self-service only, so the tests have to be signed in AS the leaver. The two
        // wall-ownership tests delete harness.Owner and set this back themselves.
        harness.ActingUser = leaver;

        return new DeletionFixture(service, betaVideoStorage, leaver.Id, boulderId);
    }

    /// <summary>The service as the app wires it, over a caller-supplied clip storage.</summary>
    public static AccountDeletionService CreateService(WallTestHarness harness, IBetaVideoStorage betaVideoStorage)
    {
        return new AccountDeletionService(
            harness.DbContextFactory,
            betaVideoStorage,
            harness.CurrentUser,
            NullLogger<AccountDeletionService>.Instance);
    }

    /// <summary>
    /// Rows belonging to OTHER people that sit next to the leaver's and must survive: a pending
    /// signup code for the same address (owned by no account yet), and a second account's refresh
    /// token issued on a subject the leaver also answers to on a different provider.
    /// </summary>
    private static void SeedStrangersArtefacts(Blocwerk.Core.Data.BlocwerkDbContext db)
    {
        db.EmailVerificationCodes.Add(new EmailVerificationCode
        {
            Email = LeaverEmail,
            CodeHash = "kdf$signup-code",
            Purpose = EmailVerificationPurpose.Signup,
            UserId = null,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        var stranger = new User
        {
            Identifier = $"stranger__{SharedSubject}",
            DisplayName = "Somebody Else",
        };
        db.Users.Add(stranger);

        db.RefreshTokens.Add(new RefreshToken
        {
            Token = "stranger-refresh-token",
            UserId = SharedSubject,
            UserName = "Somebody Else",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
    }

    private static void SeedCredentials(Blocwerk.Core.Data.BlocwerkDbContext db, User leaver)
    {
        db.UserIdentities.Add(new UserIdentity
        {
            UserId = leaver.Id,
            Provider = "github",
            ProviderUserId = LeaverSubject,
        });

        db.UserIdentities.Add(new UserIdentity
        {
            UserId = leaver.Id,
            Provider = "google",
            ProviderUserId = SharedSubject,
        });

        db.RefreshTokens.Add(new RefreshToken
        {
            Token = "refresh-token",
            UserId = LeaverSubject,

            // A name the person has since changed away from: the row still carries it, so matching
            // on the name alone would leave it (and the name) behind.
            UserName = "Leaver Oldname",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });

        db.RefreshTokens.Add(new RefreshToken
        {
            Token = "leaver-shared-subject-token",
            UserId = SharedSubject,
            UserName = "Leaver Legalname",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });

        db.EmailVerificationCodes.Add(new EmailVerificationCode
        {
            Email = LeaverEmail,
            CodeHash = "kdf$code",
            Purpose = EmailVerificationPurpose.VerifyEmail,
            UserId = leaver.Id,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        db.ApiKeys.Add(new ApiKey
        {
            Name = "personal",
            Scope = ApiKeyScope.User,
            UserId = leaver.Id,
            KeyHash = "sha256-personal",
            Prefix = "bwk_aaa",
        });

        db.TopLoggerConnections.Add(new TopLoggerConnection
        {
            UserId = leaver.Id,
            AccessTokenProtected = "protected-access",
            RefreshTokenProtected = "protected-refresh",
            TopLoggerUserId = "tl-1234",
        });

        db.UserGradeMappings.Add(new UserGradeMapping
        {
            UserId = leaver.Id,
            RawGradeKey = "raw-7",
            FontGrade = "7A",
        });

        db.ExternalAscents.Add(new ExternalAscent
        {
            UserId = leaver.Id,
            ExternalId = "tl-ascent-1",
            ClimbName = "Somewhere else",
            LoggedAt = DateTimeOffset.UtcNow.AddDays(-3),
            Type = AttemptType.Send,
            Ticked = true,
        });
    }

    private static void SeedPrivateHistory(Blocwerk.Core.Data.BlocwerkDbContext db, User leaver, Guid wallId)
    {
        db.HangboardSessions.Add(new HangboardSession
        {
            UserId = leaver.Id,
            EdgeSizeMm = 20,
            Duration = TimeSpan.FromSeconds(10),
        });

        db.PullupSessions.Add(new PullupSession { UserId = leaver.Id, Repetitions = 8 });

        db.ClimbingSessions.Add(new ClimbingSession { UserId = leaver.Id, WallId = wallId });

        db.Activities.Add(new Activity
        {
            UserId = leaver.Id,
            WallId = wallId,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastEventAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
    }

    /// <summary>
    /// Content other members see. All of it must SURVIVE the deletion, reattributed to the
    /// placeholder — except the beta clip, which shows the person and therefore goes.
    /// </summary>
    private static Guid SeedAuthoredContent(Blocwerk.Core.Data.BlocwerkDbContext db, User leaver, Guid wallId)
    {
        var boulder = new Boulder
        {
            WallId = wallId,
            Name = "Leaver's problem",
            Grade = "6C",
            CreatedByUserId = leaver.Id,
        };
        db.Boulders.Add(boulder);

        db.BoulderSetters.Add(new BoulderSetter { BoulderId = boulder.Id, UserId = leaver.Id });
        db.BoulderComments.Add(new BoulderComment
        {
            BoulderId = boulder.Id,
            UserId = leaver.Id,
            Text = "Crux is the heel hook.",
        });
        db.BoulderRatings.Add(new BoulderRating { BoulderId = boulder.Id, UserId = leaver.Id, Stars = 4 });
        db.BoulderFavorites.Add(new BoulderFavorite { BoulderId = boulder.Id, UserId = leaver.Id });
        db.GradeProposals.Add(new GradeProposal
        {
            BoulderId = boulder.Id,
            ProposedByUserId = leaver.Id,
            ProposedGrade = "7A",
        });
        db.Attempts.Add(new Attempt
        {
            BoulderId = boulder.Id,
            UserId = leaver.Id,
            Type = AttemptType.Send,

            // Free text the person typed. The attempt survives (send counts must not change); the
            // sentence about their own shoulder must not.
            Notes = "Tweaked my shoulder on the second move - Anna",
        });
        db.ActivityLog.Add(new ActivityLogEntry
        {
            WallId = wallId,
            BoulderId = boulder.Id,
            UserId = leaver.Id,
            Type = ActivityType.BoulderCreated,
            Details = "Leaver's problem",
        });
        db.WallResets.Add(new WallReset
        {
            WallId = wallId,
            Generation = 0,
            ResetByUserId = leaver.Id,
        });
        db.BetaVideos.Add(new BetaVideo
        {
            BoulderId = boulder.Id,
            UploadedByUserId = leaver.Id,
            ContentType = "video/mp4",
            StoragePath = "leaver-clip.mp4",
            SizeBytes = 1024,
        });

        return boulder.Id;
    }
}
