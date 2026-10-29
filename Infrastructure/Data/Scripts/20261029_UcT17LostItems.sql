-- UC-T17 only; Notifications is an existing system table, not recreated here.
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 10000;
BEGIN TRY
    BEGIN TRANSACTION;
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
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.LostItemReports') AND name = N'UX_LostItemReports_OpenBooking')
        CREATE UNIQUE INDEX UX_LostItemReports_OpenBooking ON dbo.LostItemReports(BookingId) WHERE Status = 'OPEN';
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.LostItemReports') AND name = N'CK_LostItemReports_Status')
        ALTER TABLE dbo.LostItemReports ADD CONSTRAINT CK_LostItemReports_Status CHECK (Status IN ('OPEN','FOUND','NOT_FOUND','RETURNED'));
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
