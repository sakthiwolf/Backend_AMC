-- Adds the Monthly / Yearly choice to Customers (same change as the EF migration
-- 0003_customer_plan_type). Safe to run more than once, and safe when dbo.Plans
-- does not exist yet.
--   'AMC'     = Yearly : one WhatsApp reminder, 1 month before the AMC end date
--   'Monthly' = Monthly: a reminder every month, 7 days before the monthly due date

IF COL_LENGTH('dbo.Customers', 'PlanType') IS NULL
BEGIN
    ALTER TABLE dbo.Customers
        ADD PlanType NVARCHAR(10) NOT NULL
            CONSTRAINT DF_Customers_PlanType DEFAULT 'AMC';
END
GO

-- Existing customers take the type of their newest open plan (only if Plans exists).
IF OBJECT_ID('dbo.Plans', 'U') IS NOT NULL
BEGIN
    EXEC('
        UPDATE c SET c.PlanType = p.PlanType
        FROM dbo.Customers c
        CROSS APPLY (SELECT TOP 1 PlanType FROM dbo.Plans
                     WHERE CustomerId = c.CustomerId AND Status <> ''Renewed''
                     ORDER BY StartDate DESC, PlanId DESC) p;');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Customers_PlanType')
BEGIN
    ALTER TABLE dbo.Customers
        ADD CONSTRAINT CK_Customers_PlanType CHECK (PlanType IN ('AMC', 'Monthly'));
END
GO
