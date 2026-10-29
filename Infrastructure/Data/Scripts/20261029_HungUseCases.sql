-- Additive SQL Server schema for UC-T15/T16/T17/T21.
-- Run against the intended development/staging database after backup and schema review.
-- No production database is changed automatically by application startup.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Bookings', 'KioskSessionHash') IS NULL
    ALTER TABLE dbo.Bookings ADD KioskSessionHash nvarchar(max) NULL;
IF COL_LENGTH('dbo.Bookings', 'KioskSessionExpiresAt') IS NULL
    ALTER TABLE dbo.Bookings ADD KioskSessionExpiresAt datetime2 NULL;
IF OBJECT_ID(N'dbo.PickupDelegations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PickupDelegations (
        Id uniqueidentifier NOT NULL PRIMARY KEY,
        BookingId uniqueidentifier NOT NULL REFERENCES dbo.Bookings(Id),
        Contact nvarchar(255) NOT NULL,
        PinHash nvarchar(255) NOT NULL,
        ExpiresAt datetime2 NOT NULL,
        UsedAt datetime2 NULL,
        RevokedAt datetime2 NULL
    );
    CREATE INDEX IX_PickupDelegations_BookingId ON dbo.PickupDelegations(BookingId);
END;
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
IF OBJECT_ID(N'dbo.LostItemReports', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LostItemReports (
        Id uniqueidentifier NOT NULL PRIMARY KEY,
        BookingId uniqueidentifier NOT NULL REFERENCES dbo.Bookings(Id),
        Description nvarchar(2000) NOT NULL,
        Photo nvarchar(max) NULL,
        Status nvarchar(30) NOT NULL,
        StaffResponse nvarchar(2000) NULL,
        CreatedAt datetime2 NOT NULL
    );
    CREATE INDEX IX_LostItemReports_BookingId ON dbo.LostItemReports(BookingId);
END;
IF OBJECT_ID(N'dbo.WalkInSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WalkInSessions (
        Id uniqueidentifier NOT NULL PRIMARY KEY,
        BookingId uniqueidentifier NOT NULL REFERENCES dbo.Bookings(Id),
        SecretHash nvarchar(255) NOT NULL,
        ContactPhone nvarchar(30) NOT NULL,
        ExpiresAt datetime2 NOT NULL
    );
    CREATE UNIQUE INDEX IX_WalkInSessions_BookingId ON dbo.WalkInSessions(BookingId);
END;
COMMIT;
