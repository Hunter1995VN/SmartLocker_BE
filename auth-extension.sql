-- ============================================================================
-- SMARTLOCKER - AUTH EXTENSION
-- Thêm các bảng phục vụ Authentication: OtpCodes
-- Áp dụng cho hệ thống có sẵn từ do_an.sql
-- ============================================================================

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OtpCodes')
BEGIN
    CREATE TABLE [dbo].[OtpCodes](
        [Id] [uniqueidentifier] NOT NULL,
        [Identifier] [nvarchar](255) NOT NULL,        -- Email hoặc Số điện thoại
        [CodeHash] [nvarchar](255) NOT NULL,          -- SHA256(Code + salt)
        [Purpose] [nvarchar](30) NOT NULL,            -- Register | Login | ForgotPassword | ChangePhone
        [FailedAttempts] [int] NOT NULL,
        [MaxAttempts] [int] NOT NULL,
        [IssuedAt] [datetime2](7) NOT NULL,
        [ExpiresAt] [datetime2](7) NOT NULL,
        [ConsumedAt] [datetime2](7) NULL,
        [DeliveryChannel] [nvarchar](10) NOT NULL,
        CONSTRAINT [pk_OtpCodes] PRIMARY KEY CLUSTERED
        (
            [Id] ASC
        ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF,
                ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
    ) ON [PRIMARY]

    -- Defaults
    ALTER TABLE [dbo].[OtpCodes] ADD DEFAULT (newsequentialid()) FOR [Id]
    ALTER TABLE [dbo].[OtpCodes] ADD DEFAULT (0) FOR [FailedAttempts]
    ALTER TABLE [dbo].[OtpCodes] ADD DEFAULT (3) FOR [MaxAttempts]
    ALTER TABLE [dbo].[OtpCodes] ADD DEFAULT (getutcdate()) FOR [IssuedAt]
    ALTER TABLE [dbo].[OtpCodes] ADD DEFAULT ('Email') FOR [DeliveryChannel]

    -- Index cho query nhanh
    CREATE NONCLUSTERED INDEX [ix_OtpCodes_Identifier_Purpose]
    ON [dbo].[OtpCodes] ([Identifier] ASC, [Purpose] ASC, [ExpiresAt] DESC)

    PRINT '✅ Bảng OtpCodes đã được tạo';
END
ELSE
BEGIN
    PRINT 'ℹ️ Bảng OtpCodes đã tồn tại, bỏ qua';
END
GO

-- ============================================================================
-- Index unique cho Users.Email & Users.Phone (nếu chưa có)
-- (Bảng Users đã có nhưng cần thêm unique index để tránh trùng lặp)
-- ============================================================================
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'uq_Users_Email' AND object_id = OBJECT_ID('dbo.Users'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [uq_Users_Email] ON [dbo].[Users] ([Email] ASC)
    PRINT '✅ Index unique Users.Email đã được tạo';
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'uq_Users_Phone' AND object_id = OBJECT_ID('dbo.Users'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [uq_Users_Phone] ON [dbo].[Users] ([Phone] ASC)
    PRINT '✅ Index unique Users.Phone đã được tạo';
END
GO

-- ============================================================================
-- Seed data: Một tài khoản admin mẫu để test
-- Password: Admin@123 (đã hash BCrypt)
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[Users] WHERE [Email] = 'admin@smartlocker.vn')
BEGIN
    INSERT INTO [dbo].[Users]
        ([FullName], [Email], [Phone], [PasswordHash], [Role], [Status], [OverdueDebt])
    VALUES
        (N'SmartLocker Admin',
         'admin@smartlocker.vn',
         '0900000000',
         '$2a$11$dQ9XPHdH8mMm7bF8pZeGOOWhYjw3YQvK3yMwTjL7dQKZ1lDr5aG2W',
         'Admin',
         'Active',
         0)

    PRINT '✅ Tài khoản admin mẫu đã tạo (admin@smartlocker.vn / Admin@123)';
END
GO

PRINT '✅ Script auth-extension.sql đã chạy thành công';
