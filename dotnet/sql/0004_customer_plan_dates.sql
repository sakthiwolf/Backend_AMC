-- Adds the calculated end date and next WhatsApp reminder date to Customers
-- (same change as the EF migration 0004_customer_plan_dates). Safe to run more than once.
-- Run 0003_customer_plan_type.sql first (and production_catchup.sql if dbo.Plans is missing).
--   PlanEndDate  = when the plan ends (installation date + 12 months)
--   ReminderDate = next WhatsApp reminder: Yearly 1 month before the end,
--                  Monthly 7 days before the next monthly due date

IF COL_LENGTH('dbo.Customers', 'PlanEndDate') IS NULL
    ALTER TABLE dbo.Customers ADD PlanEndDate DATE NULL;
GO
IF COL_LENGTH('dbo.Customers', 'ReminderDate') IS NULL
    ALTER TABLE dbo.Customers ADD ReminderDate DATE NULL;
GO

-- Fill existing customers from their newest open plan (skipped when Plans does not exist).
IF OBJECT_ID('dbo.Plans', 'U') IS NOT NULL
BEGIN
    EXEC('
        UPDATE c SET c.PlanEndDate = p.EndDate,
                     c.ReminderDate = CASE
                         WHEN p.PlanType = ''AMC'' THEN CASE WHEN p.NotificationDate >= CAST(GETDATE() AS date) THEN p.NotificationDate END
                         ELSE (SELECT TOP 1 DATEADD(DAY, -7, DATEADD(MONTH, v.n, p.StartDate))
                               FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)) v(n)
                               WHERE DATEADD(DAY, -7, DATEADD(MONTH, v.n, p.StartDate)) >= CAST(GETDATE() AS date)
                               ORDER BY v.n) END
        FROM dbo.Customers c
        CROSS APPLY (SELECT TOP 1 * FROM dbo.Plans
                     WHERE CustomerId = c.CustomerId AND Status <> ''Renewed''
                     ORDER BY StartDate DESC, PlanId DESC) p;');
END
GO

-- Record the migration as applied (only if the history table exists).
IF OBJECT_ID('dbo.__EFMigrationsHistory', 'U') IS NOT NULL
    EXEC('
        IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N''20261016133335_0004_customer_plan_dates'')
            INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
            VALUES (N''20261016133335_0004_customer_plan_dates'', N''10.0.13'');');
GO
