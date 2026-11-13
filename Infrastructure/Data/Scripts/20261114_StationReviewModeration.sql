-- Apply after 20261029_UcT21StationReviews.sql and before deploying the review feed.
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 10000;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo.StationReviews', N'IsHidden') IS NULL
        ALTER TABLE dbo.StationReviews ADD IsHidden bit NOT NULL
            CONSTRAINT DF_StationReviews_IsHidden DEFAULT (0);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StationReviews')
        AND name = N'IX_StationReviews_IsHidden_CreatedAt_Id')
        EXEC(N'CREATE INDEX IX_StationReviews_IsHidden_CreatedAt_Id ON dbo.StationReviews(IsHidden, CreatedAt DESC, Id DESC)');

    IF OBJECT_ID(N'dbo.StationReviewReports', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StationReviewReports (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            ReviewId uniqueidentifier NOT NULL REFERENCES dbo.StationReviews(Id),
            ReporterId uniqueidentifier NOT NULL REFERENCES dbo.Users(Id),
            Reason nvarchar(500) NOT NULL,
            CreatedAt datetime2 NOT NULL,
            ResolvedAt datetime2 NULL
        );
        CREATE UNIQUE INDEX IX_StationReviewReports_ReviewId_ReporterId ON dbo.StationReviewReports(ReviewId, ReporterId);
        CREATE INDEX IX_StationReviewReports_ReporterId ON dbo.StationReviewReports(ReporterId);
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
