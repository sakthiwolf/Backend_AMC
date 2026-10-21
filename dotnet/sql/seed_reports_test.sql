-- Fake data for trying the Reports screens, in AMCTracker_Reports_Test ONLY.
--
--   * Stops at once in any other database (the check below).
--   * Fake phone numbers only: +910000000001 onward.
--   * Every date is worked out from today's date in India (IST), so the data always looks current.
--   * One batch, one transaction: it either all goes in or nothing does. Safe to run again: it clears the
--     customer, plan, reminder and payment tables of THIS database first. Users (the login) is left alone.
--
-- What it makes (8 customers):
--   1 Asha Traders       AMC      active, ends in 60 days
--   2 Mohan Cables       Monthly  due in 7 days, Pending reminder today
--   3 Lakshmi Pharma     Monthly  expired 25 days ago, Failed reminder with a long error (5 tries)
--   4 Velan Diagnostics  AMC      renewed WITH a payment (UPI), Sent reminder on the old plan
--   5 Kaveri Textiles    Monthly  renewed WITHOUT a payment
--   6 Ananda Caterers    Monthly  renewed WITH a payment (Bank), Failed reminder on the old plan
--   7 Senthil Logistics  AMC      due now (ends in 30 days), Sent reminder today
--   8 Closed Business    Monthly  INACTIVE customer, due in 3 days, Pending reminder: must never show in reports

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'AMCTracker_Reports_Test'
    THROW 50001, 'Stopping: this script only runs in AMCTracker_Reports_Test.', 1;

DECLARE @today date = CAST(DATEADD(MINUTE, 330, SYSUTCDATETIME()) AS date);   -- today in IST

BEGIN TRANSACTION;

-- ---- Clear this database's data tables (not Users) ----
DELETE FROM Payments;
DELETE FROM Reminders;
UPDATE Plans SET PreviousPlanId = NULL;
DELETE FROM Plans;
DELETE FROM Customers;

-- ---- Customers (the installation date and current plan dates are filled in below) ----
INSERT INTO Customers (CustomerName, Phone, Location, InstallationDate, ProjectName, ProjectValue, PlanType,
                       WhatsAppOptOut, IsActive, CreatedAt)
VALUES
    (N'Asha Traders',      N'+910000000001', N'T. Nagar, Chennai',  '2000-01-01', N'Shop CCTV',             12000.00,  'AMC',     0, 1, SYSUTCDATETIME()),
    (N'Mohan Cables',      N'+910000000002', N'Ambattur, Chennai',  '2000-01-01', N'Cable monitoring',        499.00,  'Monthly', 0, 1, SYSUTCDATETIME()),
    (N'Lakshmi Pharma',    N'+910000000003', N'Perungudi, Chennai', '2000-01-01', N'Pharmacy POS',            450.50,  'Monthly', 0, 1, SYSUTCDATETIME()),
    (N'Velan Diagnostics', N'+910000000004', N'Adyar, Chennai',     '2000-01-01', N'Lab network and servers', 85000.00, 'AMC',     0, 1, SYSUTCDATETIME()),
    (N'Kaveri Textiles',   N'+910000000005', N'Tiruppur',           '2000-01-01', N'Billing software',       9000.00,  'Monthly', 0, 1, SYSUTCDATETIME()),
    (N'Ananda Caterers',   N'+910000000006', N'Guindy, Chennai',    '2000-01-01', N'POS and cloud sync',    14500.00,  'Monthly', 0, 1, SYSUTCDATETIME()),
    (N'Senthil Logistics', N'+910000000007', N'Chennai Port',       '2000-01-01', N'Warehouse ERP',        120000.00,  'AMC',     0, 1, SYSUTCDATETIME()),
    (N'Closed Business',   N'+910000000008', N'Closed down',        '2000-01-01', N'Old project',             300.00,  'Monthly', 0, 0, SYSUTCDATETIME());

-- ---- First plan of each customer, placed by the day it ends (days from today) ----
-- AMC: 1 year, reminder 1 month before the end. Monthly: 1 month, reminder 7 days before the end.
INSERT INTO Plans (CustomerId, PlanType, Amount, StartDate, EndDate, ReminderDate, PreviousPlanId, CreatedAt)
SELECT c.CustomerId, v.PlanType, v.Amount, s.StartDate, e.EndDate, r.ReminderDate, NULL, SYSUTCDATETIME()
FROM (VALUES
        (N'Asha Traders',      'AMC',     12000.00,   60),
        (N'Mohan Cables',      'Monthly',   499.00,    7),
        (N'Lakshmi Pharma',    'Monthly',   450.50,  -25),
        (N'Velan Diagnostics', 'AMC',     85000.00,  -40),
        (N'Kaveri Textiles',   'Monthly',  9000.00,  -20),
        (N'Ananda Caterers',   'Monthly', 14500.00,  -12),
        (N'Senthil Logistics', 'AMC',    120000.00,   30),
        (N'Closed Business',   'Monthly',   300.00,    3)
     ) v (CustomerName, PlanType, Amount, EndOffsetDays)
JOIN Customers c ON c.CustomerName = v.CustomerName
CROSS APPLY (SELECT DATEADD(DAY, v.EndOffsetDays, @today) AS Wanted) w
CROSS APPLY (SELECT CASE WHEN v.PlanType = 'AMC' THEN DATEADD(YEAR, -1, w.Wanted) ELSE DATEADD(MONTH, -1, w.Wanted) END AS StartDate) s
CROSS APPLY (SELECT CASE WHEN v.PlanType = 'AMC' THEN DATEADD(YEAR, 1, s.StartDate) ELSE DATEADD(MONTH, 1, s.StartDate) END AS EndDate) e
CROSS APPLY (SELECT CASE WHEN v.PlanType = 'AMC' THEN DATEADD(MONTH, -1, e.EndDate) ELSE DATEADD(DAY, -7, e.EndDate) END AS ReminderDate) r;

-- ---- Renewals: the new plan starts on the later of (previous end + 1 day) and the payment date ----
INSERT INTO Plans (CustomerId, PlanType, Amount, StartDate, EndDate, ReminderDate, PreviousPlanId, CreatedAt)
SELECT p.CustomerId, p.PlanType, p.Amount, s.StartDate, e.EndDate, r.ReminderDate, p.PlanId, SYSUTCDATETIME()
FROM (VALUES
        (N'Velan Diagnostics', -35),   -- paid 35 days ago, 5 days after the plan ended
        (N'Kaveri Textiles',   -19),   -- renewed the day after the plan ended, no payment recorded
        (N'Ananda Caterers',   -10)    -- paid 10 days ago, 2 days after the plan ended
     ) v (CustomerName, StartOffsetDays)
JOIN Customers c ON c.CustomerName = v.CustomerName
JOIN Plans p ON p.CustomerId = c.CustomerId AND p.PreviousPlanId IS NULL
CROSS APPLY (SELECT DATEADD(DAY, v.StartOffsetDays, @today) AS StartDate) s
CROSS APPLY (SELECT CASE WHEN p.PlanType = 'AMC' THEN DATEADD(YEAR, 1, s.StartDate) ELSE DATEADD(MONTH, 1, s.StartDate) END AS EndDate) e
CROSS APPLY (SELECT CASE WHEN p.PlanType = 'AMC' THEN DATEADD(MONTH, -1, e.EndDate) ELSE DATEADD(DAY, -7, e.EndDate) END AS ReminderDate) r;

-- ---- Payments: recorded against the plan that was renewed (Kaveri has none) ----
INSERT INTO Payments (PlanId, PaidOn, Amount, Mode, Note, CreatedAt)
SELECT p.PlanId, DATEADD(DAY, v.PaidOffsetDays, @today), v.Amount, v.Mode, v.Note, SYSUTCDATETIME()
FROM (VALUES
        (N'Velan Diagnostics', -35, 85000.00, 'UPI',  N'GPay ref 4821'),
        (N'Ananda Caterers',   -10, 14500.00, 'Bank', N'Cheque cleared')
     ) v (CustomerName, PaidOffsetDays, Amount, Mode, Note)
JOIN Customers c ON c.CustomerName = v.CustomerName
JOIN Plans p ON p.CustomerId = c.CustomerId AND p.PreviousPlanId IS NULL;

-- ---- Reminders on each customer's first plan ----
INSERT INTO Reminders (PlanId, ScheduledDate, Status, Channel, Attempts, SentAt, ErrorMessage)
SELECT p.PlanId,
       CASE WHEN v.OnReminderDate = 1 THEN p.ReminderDate ELSE @today END,
       v.Status, 'WhatsApp', v.Attempts,
       CASE WHEN v.Status = 'Sent' THEN SYSUTCDATETIME() END,
       v.ErrorMessage
FROM (VALUES
        (N'Mohan Cables',      0, 'Pending', 0, CAST(NULL AS nvarchar(500))),
        (N'Lakshmi Pharma',    1, 'Failed',  5, CAST(N'HTTP 400: (#131026) Message undeliverable. ' + REPLICATE(N'x', 230) AS nvarchar(500))),
        (N'Velan Diagnostics', 1, 'Sent',    1, CAST(NULL AS nvarchar(500))),
        (N'Ananda Caterers',   1, 'Failed',  2, CAST(N'HTTP 500: WhatsApp was unavailable' AS nvarchar(500))),
        (N'Senthil Logistics', 1, 'Sent',    1, CAST(NULL AS nvarchar(500))),
        (N'Closed Business',   0, 'Pending', 0, CAST(NULL AS nvarchar(500)))
     ) v (CustomerName, OnReminderDate, Status, Attempts, ErrorMessage)
JOIN Customers c ON c.CustomerName = v.CustomerName
JOIN Plans p ON p.CustomerId = c.CustomerId AND p.PreviousPlanId IS NULL;

-- ---- Installation date = the first plan's start; the customer's dates = the newest plan's ----
UPDATE c SET c.InstallationDate = first.StartDate
FROM Customers c
CROSS APPLY (SELECT TOP 1 StartDate FROM Plans WHERE CustomerId = c.CustomerId ORDER BY StartDate, PlanId) first;

UPDATE c SET c.PlanEndDate = cur.EndDate, c.ReminderDate = cur.ReminderDate
FROM Customers c
CROSS APPLY (SELECT TOP 1 EndDate, ReminderDate FROM Plans WHERE CustomerId = c.CustomerId ORDER BY StartDate DESC, PlanId DESC) cur;

COMMIT TRANSACTION;

SELECT DB_NAME() AS [database], @today AS today_ist,
       (SELECT COUNT(*) FROM Customers) AS customers, (SELECT COUNT(*) FROM Customers WHERE IsActive = 0) AS inactive_customers,
       (SELECT COUNT(*) FROM Plans) AS plans, (SELECT COUNT(*) FROM Reminders) AS reminders,
       (SELECT COUNT(*) FROM Payments) AS payments;
