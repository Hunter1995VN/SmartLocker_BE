-- Additive session columns only; no data backfill or lifecycle changes.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Bookings', 'KioskSessionHash') IS NULL
    ALTER TABLE dbo.Bookings ADD KioskSessionHash nvarchar(64) NULL;
IF COL_LENGTH('dbo.Bookings', 'KioskSessionExpiresAt') IS NULL
    ALTER TABLE dbo.Bookings ADD KioskSessionExpiresAt datetime2 NULL;
COMMIT TRANSACTION;
