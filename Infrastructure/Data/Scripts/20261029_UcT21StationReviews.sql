-- UC-T21 only. Existing shared schema is left intact. No seed/backfill/deletion.
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 10000;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.StationReviews', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.StationReviews (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            BookingId uniqueidentifier NOT NULL REFERENCES dbo.Bookings(Id),
            Stars int NOT NULL CHECK (Stars BETWEEN 1 AND 5),
            Comment nvarchar(1000) NOT NULL,
            Criteria nvarchar(150) NOT NULL,
            CreatedAt datetime2 NOT NULL
        );
        CREATE UNIQUE INDEX IX_StationReviews_BookingId ON dbo.StationReviews(BookingId);
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
