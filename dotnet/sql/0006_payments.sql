    BEGIN TRANSACTION;
    IF NOT EXISTS (
        SELECT * FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261021071755_0006_payments'
    )
    BEGIN

        IF EXISTS (SELECT 1 FROM Payments WHERE Mode IS NULL OR Mode NOT IN ('Cash', 'UPI', 'Bank') OR Amount <= 0)
            THROW 50006, 'Payments has rows without a Cash/UPI/Bank mode or with an amount of 0 or less. Fix or remove them, then run this migration again.', 1;
    END;

    IF NOT EXISTS (
        SELECT * FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261021071755_0006_payments'
    )
    BEGIN
        DECLARE @var nvarchar(max);
        SELECT @var = QUOTENAME([d].[name])
        FROM [sys].[default_constraints] [d]
        INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
        WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Payments]') AND [c].[name] = N'Mode');
        IF @var IS NOT NULL EXEC(N'ALTER TABLE [Payments] DROP CONSTRAINT ' + @var + ';');
        ALTER TABLE [Payments] ALTER COLUMN [Mode] nvarchar(20) NOT NULL;
    END;

    IF NOT EXISTS (
        SELECT * FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261021071755_0006_payments'
    )
    BEGIN
        ALTER TABLE [Payments] ADD [CreatedAt] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME());
    END;

    IF NOT EXISTS (
        SELECT * FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261021071755_0006_payments'
    )
    BEGIN
        ALTER TABLE [Payments] ADD [Note] nvarchar(200) NULL;
    END;

    IF NOT EXISTS (
        SELECT * FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261021071755_0006_payments'
    )
    BEGIN
        EXEC(N'ALTER TABLE [Payments] ADD CONSTRAINT [CK_Payments_Amount_Positive] CHECK ([Amount] > 0)');
    END;

    IF NOT EXISTS (
        SELECT * FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261021071755_0006_payments'
    )
    BEGIN
        EXEC(N'ALTER TABLE [Payments] ADD CONSTRAINT [CK_Payments_Mode] CHECK ([Mode] IN (''Cash'', ''UPI'', ''Bank''))');
    END;

    IF NOT EXISTS (
        SELECT * FROM [__EFMigrationsHistory]
        WHERE [MigrationId] = N'20261021071755_0006_payments'
    )
    BEGIN
        INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
        VALUES (N'20261021071755_0006_payments', N'10.0.13');
    END;

    COMMIT;
    GO

