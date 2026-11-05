/*
  Clear all customer data. KEEPS the Users and UserDevices tables (admin / staff logins).
  Deletes: Reminders, Payments, Plans, Customers (in that order, because of the foreign keys).

  !! This cannot be undone. Take a database backup first. !!
  Step 1: run the SELECT counts below and check them.
  Step 2: change @Confirm to 1 and run the whole script again.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Confirm bit = 0;   -- 0 = only show counts, 1 = really delete

SELECT
  (SELECT COUNT(*) FROM Customers) AS Customers,
  (SELECT COUNT(*) FROM Plans)     AS Plans,
  (SELECT COUNT(*) FROM Reminders) AS Reminders,
  (SELECT COUNT(*) FROM Payments)  AS Payments,
  (SELECT COUNT(*) FROM Users)     AS Users_KEPT;

IF @Confirm = 1
BEGIN
  BEGIN TRANSACTION;

  DELETE FROM Reminders;
  DELETE FROM Payments;

  -- a renewal points at the plan it replaced: break that link before deleting plans
  UPDATE Plans SET PreviousPlanId = NULL WHERE PreviousPlanId IS NOT NULL;
  DELETE FROM Plans;

  DELETE FROM Customers;

  COMMIT TRANSACTION;

  -- optional: make new rows start from id 1 again (remove these 4 lines to keep counting on)
  DBCC CHECKIDENT ('Reminders', RESEED, 0);
  DBCC CHECKIDENT ('Payments',  RESEED, 0);
  DBCC CHECKIDENT ('Plans',     RESEED, 0);
  DBCC CHECKIDENT ('Customers', RESEED, 0);

  SELECT
    (SELECT COUNT(*) FROM Customers) AS Customers,
    (SELECT COUNT(*) FROM Plans)     AS Plans,
    (SELECT COUNT(*) FROM Reminders) AS Reminders,
    (SELECT COUNT(*) FROM Payments)  AS Payments,
    (SELECT COUNT(*) FROM Users)     AS Users_KEPT;
END
