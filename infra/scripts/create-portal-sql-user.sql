/*
===============================================================================
 Desicon VIM Platform -- database user for the VENDOR PORTAL's managed identity
===============================================================================

 Run once per environment AFTER the vendor portal app exists (GitHub variable
 VENDOR_PORTAL_ENABLED=true and a deploy) and AFTER create-sql-users.sql has
 created the vim_app role. Safe to run more than once.

 The portal joins the same role as the workbench and functions (vim_app), so it
 inherits the same INSERT-ONLY rule on the audit tables (AuditEvents,
 RouteChanges, VendorAccessEvents). It has no DDL rights.

 HOW TO RUN IT (PowerShell)
   az login
   sqlcmd -S <sql_server_fqdn> -d VimDb --authentication-method ActiveDirectoryDefault `
          -i infra\scripts\create-portal-sql-user.sql `
          -v PortalAppName="vendors-vim-dev-weu-a1b2"

   Get the name from: terraform output portal_app_name
===============================================================================
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT 'Database: ' + DB_NAME();

IF DATABASE_PRINCIPAL_ID(N'vim_app') IS NULL
BEGIN
    RAISERROR('Role vim_app does not exist. Run create-sql-users.sql first.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(PortalAppName)')
BEGIN
    CREATE USER [$(PortalAppName)] FROM EXTERNAL PROVIDER;
    PRINT 'Created user $(PortalAppName)';
END

IF IS_ROLEMEMBER(N'vim_app', N'$(PortalAppName)') = 0
BEGIN
    ALTER ROLE vim_app ADD MEMBER [$(PortalAppName)];
    PRINT 'Added $(PortalAppName) to vim_app';
END

PRINT 'Done.';
