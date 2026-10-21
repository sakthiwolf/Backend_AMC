BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC sp_rename N'[Plans].[NotificationDate]', N'ReminderDate', 'COLUMN';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC sp_rename N'[Plans].[IX_Plans_NotificationDate]', N'IX_Plans_ReminderDate', 'INDEX';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'
    UPDATE Plans SET EndDate = CASE WHEN PlanType = ''AMC'' THEN DATEADD(YEAR, 1, StartDate)
                                    ELSE DATEADD(MONTH, 1, StartDate) END;
    UPDATE Plans SET ReminderDate = CASE WHEN PlanType = ''AMC'' THEN DATEADD(MONTH, -1, EndDate)
                                         ELSE DATEADD(DAY, -7, EndDate) END;')
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Plans]') AND [c].[name] = N'Status');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Plans] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [Plans] DROP COLUMN [Status];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    DROP INDEX [IX_Plans_PreviousPlanId] ON [Plans];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Plans_PreviousPlanId] ON [Plans] ([PreviousPlanId]) WHERE [PreviousPlanId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'ALTER TABLE [Plans] ADD CONSTRAINT [CK_Plans_PlanType] CHECK ([PlanType] IN (''AMC'', ''Monthly''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'ALTER TABLE [Plans] ADD CONSTRAINT [CK_Plans_Amount_NonNegative] CHECK ([Amount] >= 0)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'ALTER TABLE [Plans] ADD CONSTRAINT [CK_Plans_Dates] CHECK ([StartDate] < [ReminderDate] AND [ReminderDate] < [EndDate])');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    ALTER TABLE [Reminders] ADD [Attempts] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'UPDATE Reminders SET Attempts = 1 WHERE Status IN (''Sent'', ''Failed'');')
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'ALTER TABLE [Reminders] ADD CONSTRAINT [CK_Reminders_Status] CHECK ([Status] IN (''Pending'', ''Sent'', ''Failed''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'ALTER TABLE [Reminders] ADD CONSTRAINT [CK_Reminders_Attempts] CHECK ([Attempts] >= 0)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    EXEC(N'
    UPDATE c SET c.PlanEndDate = p.EndDate, c.ReminderDate = p.ReminderDate
    FROM Customers c
    CROSS APPLY (SELECT TOP 1 x.EndDate, x.ReminderDate FROM Plans x
                 WHERE x.CustomerId = c.CustomerId
                   AND NOT EXISTS (SELECT 1 FROM Plans r WHERE r.PreviousPlanId = x.PlanId)
                 ORDER BY CASE WHEN x.PlanType = c.PlanType THEN 0 ELSE 1 END, x.StartDate DESC, x.PlanId DESC) p;')
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021054436_0005_plans_reminders'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261021054436_0005_plans_reminders', N'10.0.13');
END;

COMMIT;
GO

