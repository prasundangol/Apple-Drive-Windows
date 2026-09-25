namespace AppleDrive.Infrastructure.Database.Migrations;

/// <summary>One forward-only schema change. Applied in order; never edited once released.</summary>
internal sealed record Migration(int Version, string Description, string Sql);

/// <summary>
/// The schema history. To change the schema, append a new migration with the next version;
/// never modify or remove an existing one, because user databases have already applied it.
/// </summary>
internal static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "Media index", """
            CREATE TABLE MediaFiles (
                Id              INTEGER PRIMARY KEY,
                FullPath        TEXT    NOT NULL COLLATE NOCASE,
                MediaType       INTEGER NOT NULL,
                FileSize        INTEGER NOT NULL,
                CreatedAt       INTEGER NULL,      -- Unix ms, UTC
                ModifiedAt      INTEGER NOT NULL,  -- Unix ms, UTC
                Width           INTEGER NULL,
                Height          INTEGER NULL,
                CaptureDate     INTEGER NULL,      -- Unix ms, UTC
                Sha256          BLOB    NULL,      -- 32 bytes, computed lazily
                PerceptualHash  INTEGER NULL,      -- 64-bit fingerprint (images only)
                FirstSeenAt     INTEGER NOT NULL,
                LastScannedAt   INTEGER NOT NULL,
                IsAvailable     INTEGER NOT NULL DEFAULT 1
            );

            -- Path lookups and upserts; also serves prefix scans of a destination folder.
            CREATE UNIQUE INDEX UX_MediaFiles_FullPath ON MediaFiles (FullPath);

            -- Exact-duplicate candidate search always starts from the file size.
            CREATE INDEX IX_MediaFiles_FileSize ON MediaFiles (FileSize) WHERE IsAvailable = 1;

            -- Confirming a duplicate or detecting a moved file by content.
            CREATE INDEX IX_MediaFiles_Sha256 ON MediaFiles (Sha256) WHERE Sha256 IS NOT NULL;
            """),
    ];

    public static int LatestVersion => All[^1].Version;
}
