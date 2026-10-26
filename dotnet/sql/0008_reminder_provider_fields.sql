BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023075210_0008_reminder_provider_fields'
)
BEGIN
    ALTER TABLE [Reminders] ADD [IsTest] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023075210_0008_reminder_provider_fields'
)
BEGIN
    ALTER TABLE [Reminders] ADD [ProviderMessageId] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023075210_0008_reminder_provider_fields'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261023075210_0008_reminder_provider_fields', N'10.0.13');
END;

COMMIT;
GO

