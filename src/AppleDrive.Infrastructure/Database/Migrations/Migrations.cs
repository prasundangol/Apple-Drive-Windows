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

        new(2, "Transfer history", """
            CREATE TABLE TransferSessions (
                Id                TEXT    PRIMARY KEY,
                DeviceName        TEXT    NULL,
                DestinationRoot   TEXT    NOT NULL,
                StartedAt         INTEGER NOT NULL,  -- Unix ms, UTC
                CompletedAt       INTEGER NULL,
                Status            INTEGER NOT NULL,  -- TransferSessionStatus
                TransferredCount  INTEGER NOT NULL DEFAULT 0,
                SkippedCount      INTEGER NOT NULL DEFAULT 0,
                FailedCount       INTEGER NOT NULL DEFAULT 0,
                TransferredBytes  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE Transfers (
                Id                  INTEGER PRIMARY KEY,
                SessionId           TEXT    NOT NULL REFERENCES TransferSessions (Id),
                SourceAssetId       TEXT    NOT NULL,
                SourcePersistentId  TEXT    NULL,
                SourceFileName      TEXT    NOT NULL,
                ReportedSize        INTEGER NULL,
                PartialPath         TEXT    NULL,      -- temporary file, recorded before it is created
                DestinationPath     TEXT    NULL COLLATE NOCASE,
                Sha256              BLOB    NULL,
                FileSize            INTEGER NULL,      -- bytes actually delivered
                StartedAt           INTEGER NOT NULL,
                CompletedAt         INTEGER NULL,
                Status              INTEGER NOT NULL,  -- TransferStatus
                ErrorKind           TEXT    NULL,
                ErrorMessage        TEXT    NULL
            );

            -- A session's files, optionally by outcome (summary, "view failed").
            CREATE INDEX IX_Transfers_SessionId_Status ON Transfers (SessionId, Status);

            -- Crash recovery: transfers that never finished.
            CREATE INDEX IX_Transfers_InProgress ON Transfers (Id) WHERE Status = 0;
            """),
    ];

    public static int LatestVersion => All[^1].Version;
}
