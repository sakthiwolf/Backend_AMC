BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023061810_0007_total_project_value'
)
BEGIN
    ALTER TABLE [Customers] ADD [TotalProjectValue] decimal(12,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023061810_0007_total_project_value'
)
BEGIN
    EXEC(N'ALTER TABLE [Customers] ADD CONSTRAINT [CK_Customers_TotalProjectValue_NonNegative] CHECK ([TotalProjectValue] IS NULL OR [TotalProjectValue] >= 0)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023061810_0007_total_project_value'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261023061810_0007_total_project_value', N'10.0.13');
END;

COMMIT;
GO

