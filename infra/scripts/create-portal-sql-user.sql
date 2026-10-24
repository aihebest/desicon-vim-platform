/*
===============================================================================
 Desicon VIM Platform -- database user for the VENDOR PORTAL's managed identity
===============================================================================

 Run once per environment AFTER the vendor portal app exists (GitHub variable
 VENDOR_PORTAL_ENABLED=true and a deploy, so the vendor tables exist).
 Safe to run more than once.

 The portal is internet-facing, so it gets its OWN role, vim_portal, with only
 what phase 1 needs: read/write vendor invitations and portal users, and
 INSERT (not UPDATE/DELETE) on the vendor access log. It cannot read cases,
 documents, routes or the outbox, and has no DDL rights. Phase 2 (vendor
 submission) will widen this deliberately, table by table.

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

IF OBJECT_ID(N'[vim].[VendorInvitations]') IS NULL
BEGIN
    RAISERROR('Table [vim].[VendorInvitations] does not exist yet. Deploy (run the migrations) first.', 16, 1);
    RETURN;
END

IF DATABASE_PRINCIPAL_ID(N'vim_portal') IS NULL
BEGIN
    CREATE ROLE [vim_portal];
    PRINT 'Created role vim_portal';
END

GRANT SELECT, INSERT, UPDATE ON [vim].[VendorInvitations] TO [vim_portal];
GRANT SELECT, INSERT, UPDATE ON [vim].[VendorUsers] TO [vim_portal];
GRANT SELECT, INSERT ON [vim].[VendorAccessEvents] TO [vim_portal];
DENY UPDATE, DELETE ON [vim].[VendorAccessEvents] TO [vim_portal];

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(PortalAppName)')
BEGIN
    CREATE USER [$(PortalAppName)] FROM EXTERNAL PROVIDER;
    PRINT 'Created user $(PortalAppName)';
END

-- Earlier drafts put the portal in vim_app (all tables); take it out if so.
IF IS_ROLEMEMBER(N'vim_app', N'$(PortalAppName)') = 1
BEGIN
    ALTER ROLE [vim_app] DROP MEMBER [$(PortalAppName)];
    PRINT 'Removed $(PortalAppName) from vim_app';
END

IF IS_ROLEMEMBER(N'vim_portal', N'$(PortalAppName)') = 0
BEGIN
    ALTER ROLE [vim_portal] ADD MEMBER [$(PortalAppName)];
    PRINT 'Added $(PortalAppName) to vim_portal';
END

PRINT 'Done.';
