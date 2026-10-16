-- Brings a database that only has Customers/Users/UserDevices up to the current schema:
-- creates Plans, Reminders and Payments, adds the Customers indexes and checks, and records the
-- EF migrations as applied (so a later "dotnet ef database update" does not try to repeat them).
-- Safe to run more than once. Run 0003_customer_plan_type.sql first (or after: order does not matter).
-- The check constraints use WITH NOCHECK, so existing rows are not validated (new and edited rows are).

IF OBJECT_ID('dbo.Plans', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Plans (
        PlanId           INT           NOT NULL IDENTITY,
        CustomerId       INT           NOT NULL,
        PlanType         NVARCHAR(10)  NOT NULL,
        Amount           DECIMAL(12,2) NOT NULL,
        StartDate        DATE          NOT NULL,
        EndDate          DATE          NOT NULL,
        NotificationDate DATE          NOT NULL,
        Status           NVARCHAR(20)  NOT NULL,
        PreviousPlanId   INT           NULL,
        CreatedAt        DATETIME2     NOT NULL DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Plans PRIMARY KEY (PlanId),
        CONSTRAINT FK_Plans_Customers_CustomerId FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId) ON DELETE NO ACTION,
        CONSTRAINT FK_Plans_Plans_PreviousPlanId FOREIGN KEY (PreviousPlanId) REFERENCES dbo.Plans (PlanId) ON DELETE NO ACTION
    );
    CREATE INDEX IX_Plans_CustomerId       ON dbo.Plans (CustomerId);
    CREATE INDEX IX_Plans_EndDate          ON dbo.Plans (EndDate);
    CREATE INDEX IX_Plans_NotificationDate ON dbo.Plans (NotificationDate);
    CREATE INDEX IX_Plans_PreviousPlanId   ON dbo.Plans (PreviousPlanId);
END

IF OBJECT_ID('dbo.Reminders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Reminders (
        ReminderId    INT           NOT NULL IDENTITY,
        PlanId        INT           NOT NULL,
        ScheduledDate DATE          NOT NULL,
        SentAt        DATETIME2     NULL,
        Channel       NVARCHAR(20)  NOT NULL DEFAULT N'WhatsApp',
        Status        NVARCHAR(20)  NOT NULL,
        ErrorMessage  NVARCHAR(500) NULL,
        CONSTRAINT PK_Reminders PRIMARY KEY (ReminderId),
        CONSTRAINT FK_Reminders_Plans_PlanId FOREIGN KEY (PlanId) REFERENCES dbo.Plans (PlanId) ON DELETE NO ACTION
    );
    -- one reminder row per plan and date: "never send twice"
    CREATE UNIQUE INDEX UQ_Reminder_PlanDate ON dbo.Reminders (PlanId, ScheduledDate);
END

IF OBJECT_ID('dbo.Payments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Payments (
        PaymentId INT           NOT NULL IDENTITY,
        PlanId    INT           NOT NULL,
        PaidOn    DATE          NOT NULL,
        Amount    DECIMAL(12,2) NOT NULL,
        Mode      NVARCHAR(20)  NULL,
        CONSTRAINT PK_Payments PRIMARY KEY (PaymentId),
        CONSTRAINT FK_Payments_Plans_PlanId FOREIGN KEY (PlanId) REFERENCES dbo.Plans (PlanId) ON DELETE NO ACTION
    );
    CREATE INDEX IX_Payments_PlanId ON dbo.Payments (PlanId);
END

-- Customers: search/filter indexes and data checks (migration 0002_customers).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_CustomerName' AND object_id = OBJECT_ID('dbo.Customers'))
    CREATE INDEX IX_Customers_CustomerName ON dbo.Customers (CustomerName);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_Phone' AND object_id = OBJECT_ID('dbo.Customers'))
    CREATE INDEX IX_Customers_Phone ON dbo.Customers (Phone);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Customers_IsActive' AND object_id = OBJECT_ID('dbo.Customers'))
    CREATE INDEX IX_Customers_IsActive ON dbo.Customers (IsActive);

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Customers_ProjectValue_NonNegative')
    ALTER TABLE dbo.Customers WITH NOCHECK ADD CONSTRAINT CK_Customers_ProjectValue_NonNegative
        CHECK ([ProjectValue] IS NULL OR [ProjectValue] >= 0);
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Customers_Phone_E164')
    ALTER TABLE dbo.Customers WITH NOCHECK ADD CONSTRAINT CK_Customers_Phone_E164
        CHECK (LEFT([Phone], 1) = '+' AND LEN([Phone]) BETWEEN 9 AND 16
               AND SUBSTRING([Phone], 2, 1) <> '0' AND SUBSTRING([Phone], 2, 20) NOT LIKE '%[^0-9]%');

GO

-- Record the migrations as applied, so "dotnet ef database update" does not repeat them.
IF OBJECT_ID('dbo.__EFMigrationsHistory', 'U') IS NULL
    CREATE TABLE dbo.__EFMigrationsHistory (
        MigrationId    NVARCHAR(150) NOT NULL,
        ProductVersion NVARCHAR(32)  NOT NULL,
        CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY (MigrationId)
    );
GO
INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
SELECT v.Id, N'10.0.13'
FROM (VALUES (N'20261015045823_InitialCreate'), (N'20261016115145_0002_customers')) v(Id)
WHERE v.Id NOT IN (SELECT MigrationId FROM dbo.__EFMigrationsHistory);

-- 0003 counts as applied only if the PlanType column is there (run 0003_customer_plan_type.sql first).
IF COL_LENGTH('dbo.Customers', 'PlanType') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261016122947_0003_customer_plan_type')
    INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20261016122947_0003_customer_plan_type', N'10.0.13');
GO
