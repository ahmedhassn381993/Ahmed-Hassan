-- Run in database "workload" as a member of the SQL Entra admin group, once per environment.
-- Replace enppi-workload-prod-web with the web app name (output webIdentityName).
CREATE USER [enppi-workload-prod-web] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [enppi-workload-prod-web];
ALTER ROLE db_datawriter ADD MEMBER [enppi-workload-prod-web];
ALTER ROLE db_ddladmin  ADD MEMBER [enppi-workload-prod-web];   -- applies EF migrations at start-up; remove if the DBA runs 01_schema.sql
-- Power BI service identity: read-only access to the reporting views (02_reporting_views.sql)
-- CREATE USER [enppi-workload-powerbi] FROM EXTERNAL PROVIDER;
-- GRANT SELECT ON SCHEMA::reporting TO [enppi-workload-powerbi];
