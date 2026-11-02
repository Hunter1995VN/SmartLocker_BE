-- UC-T09: additive only. No payment, booking or credential value is backfilled.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.AccessCredentials', 'ProtectedAccessCode') IS NULL
    ALTER TABLE dbo.AccessCredentials ADD ProtectedAccessCode nvarchar(max) NULL;
COMMIT TRANSACTION;
