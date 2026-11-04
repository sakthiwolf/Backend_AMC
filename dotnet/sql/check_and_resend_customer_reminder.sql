/*
  WhatsApp reminder: check one customer, and (optionally) make their reminder due again today.

  How to use (SSMS or sqlcmd, on the AMC database):
    1. Set @Name below (part of the customer name, e.g. N'sakthi').
    2. Run with @Resend = 0 first. It only reads: customer, plans, reminder rows (status, error, test or real).
    3. To send again: set @Resend = 1 and run. For the customer's current plan(s) it moves ReminderDate to
       today (IST) and removes that plan's old reminder rows, so the next run sends it once.
    4. Then trigger the send: POST /api/reminders/run as an admin user (Swagger or curl, see the end),
       or wait for the 09:00 IST job.
    5. Run again with @Resend = 0: the newest row should be Status = Sent, IsTest = 0, with a ProviderMessageId.

  The message reaches the CUSTOMER only when the API runs with:
    ASPNETCORE_ENVIRONMENT=Production   (Development always sends to the test phone)
    WhatsApp__Provider=Wacto, WhatsApp__PhoneNumberId, WhatsApp__Token set
    Reminders__Enabled=true
    Reminders__TestPhoneOnly=false      (true = only Reminders__TestPhone gets it, row has IsTest = 1)

  The original ReminderDate of each changed plan is printed, so it can be put back (see the end).
*/
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;  -- needed for the filtered unique index (sqlcmd turns it off by default)
SET ANSI_NULLS ON;
SET XACT_ABORT ON;

DECLARE @Name   nvarchar(100) = N'sakthi';
DECLARE @Resend bit = 0;   -- 0 = check only, 1 = make the reminder due today

DECLARE @TodayIst date = CAST(DATEADD(MINUTE, 330, SYSUTCDATETIME()) AS date);

PRINT CONCAT('Today (IST): ', CONVERT(varchar(10), @TodayIst, 23));

-- 1. Customer(s) matching the name
SELECT c.CustomerId, c.CustomerName, c.ProjectName, c.Phone, c.IsActive, c.WhatsAppOptOut,
       CASE
         WHEN c.IsActive = 0 THEN 'NOT SENT: customer is deleted (IsActive = 0)'
         WHEN c.WhatsAppOptOut = 1 THEN 'NOT SENT: customer opted out of WhatsApp'
         WHEN c.Phone NOT LIKE '+%' OR LEN(c.Phone) < 11 THEN 'CHECK: phone is not +<country><number>'
         ELSE 'OK'
       END AS CustomerCheck
FROM Customers c
WHERE c.CustomerName LIKE N'%' + @Name + N'%';

-- 2. Their plans and whether the reminder job would pick each one up today
SELECT p.PlanId, c.CustomerName, p.PlanType, p.StartDate, p.ReminderDate, p.EndDate,
       CAST(DATEADD(MINUTE, 330, p.CreatedAt) AS date) AS CreatedOnIst,
       CASE WHEN EXISTS (SELECT 1 FROM Plans n WHERE n.PreviousPlanId = p.PlanId) THEN 1 ELSE 0 END AS IsRenewed,
       CASE
         WHEN EXISTS (SELECT 1 FROM Plans n WHERE n.PreviousPlanId = p.PlanId) THEN 'skipped: renewed'
         WHEN @TodayIst > p.EndDate THEN 'skipped: plan ended'
         WHEN p.ReminderDate > @TodayIst THEN CONCAT('waiting: due on ', CONVERT(varchar(10), p.ReminderDate, 23))
         WHEN p.ReminderDate < DATEADD(DAY, -3, @TodayIst) THEN 'skipped: reminder date more than 3 days ago'
         WHEN CAST(DATEADD(MINUTE, 330, p.CreatedAt) AS date) > p.ReminderDate THEN 'skipped: plan added after its reminder date'
         WHEN EXISTS (SELECT 1 FROM Reminders r WHERE r.PlanId = p.PlanId AND r.Status = 'Sent' AND r.IsTest = 0)
              THEN 'done: already sent to the customer'
         ELSE 'DUE: will be sent on the next run'
       END AS JobDecision
FROM Plans p
JOIN Customers c ON c.CustomerId = p.CustomerId
WHERE c.CustomerName LIKE N'%' + @Name + N'%'
ORDER BY c.CustomerId, p.EndDate DESC;

-- 3. Reminder rows (what was actually sent)
SELECT r.ReminderId, r.PlanId, c.CustomerName, r.ScheduledDate, r.Status, r.Attempts,
       r.IsTest, CASE WHEN r.IsTest = 1 THEN 'went to TEST phone, not the customer' ELSE 'customer' END AS SentTo,
       r.SentAt AS SentAtUtc, r.ProviderMessageId, r.ErrorMessage
FROM Reminders r
JOIN Plans p ON p.PlanId = r.PlanId
JOIN Customers c ON c.CustomerId = p.CustomerId
WHERE c.CustomerName LIKE N'%' + @Name + N'%'
ORDER BY r.ReminderId DESC;

IF @Resend = 1
BEGIN
    BEGIN TRANSACTION;

    -- Current plans only: active customer, not opted out, not renewed, today strictly between start and end
    -- (the table requires StartDate < ReminderDate < EndDate).
    DECLARE @Changed TABLE (PlanId int, OldReminderDate date);

    UPDATE p
       SET p.ReminderDate = @TodayIst
    OUTPUT inserted.PlanId, deleted.ReminderDate INTO @Changed
      FROM Plans p
      JOIN Customers c ON c.CustomerId = p.CustomerId
     WHERE c.CustomerName LIKE N'%' + @Name + N'%'
       AND c.IsActive = 1 AND c.WhatsAppOptOut = 0
       AND NOT EXISTS (SELECT 1 FROM Plans n WHERE n.PreviousPlanId = p.PlanId)
       AND p.StartDate < @TodayIst AND @TodayIst < p.EndDate
       AND CAST(DATEADD(MINUTE, 330, p.CreatedAt) AS date) <= @TodayIst;

    DELETE r FROM Reminders r JOIN @Changed ch ON ch.PlanId = r.PlanId;

    COMMIT;

    SELECT PlanId, OldReminderDate, @TodayIst AS NewReminderDate FROM @Changed;
    IF NOT EXISTS (SELECT 1 FROM @Changed)
        PRINT 'Nothing changed: no current plan for that customer (check the results above).';
    ELSE
        PRINT 'Done. Now POST /api/reminders/run as an admin (or wait for 09:00 IST), then re-run this script with @Resend = 0.';
END;

/*
  To put a plan's reminder date back afterwards (values printed in OldReminderDate):
    UPDATE Plans SET ReminderDate = '2027-01-01' WHERE PlanId = 123;
*/

/*
  Trigger the run now (the user must be listed in Reminders:AdminUsernames):
    TOKEN=$(curl -s -X POST https://appamcapi.genuineitsolution.com/api/auth/login -H "Content-Type: application/json" \
      -d '{"username":"<admin>","password":"<password>","deviceFingerprint":"11111111-1111-4111-8111-111111111111"}' \
      | python3 -c "import sys,json;print(json.load(sys.stdin)['accessToken'])")
    curl -s -X POST https://appamcapi.genuineitsolution.com/api/reminders/run -H "Authorization: Bearer $TOKEN"
  Expected reply: {"processed":1,"sent":1,"failed":0,"skipped":0}
*/
