/*
===============================================================================
 Desicon VIM Platform -- database users for the managed identities
===============================================================================

 WHAT THIS DOES
   Creates contained database users for:
     - the web app's managed identity        (e.g. app-vim-dev-weu-a1b2)
     - the function app's managed identity   (e.g. func-vim-dev-weu-a1b2)
     - the MIGRATION identity                (e.g. sp-vim-migrations-dev)
   and gives each exactly the access it needs.

   The two APP identities go into one role, vim_app:
     - read and write data (db_datareader + db_datawriter)
     - EXECUTE (stored procedures / functions, if the app uses any)
     - DENY UPDATE and DELETE on [vim].[AuditEvents] and [vim].[RouteChanges]
       => the audit trail is INSERT-ONLY for the running application. Even a
          bug or a compromised app cannot rewrite or erase audit history. A
          DENY always beats a GRANT in SQL Server, so db_datawriter cannot
          override it.

   The MIGRATION identity is separate on purpose. It is the only principal
   that can change the schema (db_ddladmin). It is used only by the
   "migrate" job in .github/workflows/deploy.yml, never by the running apps.
   If the web app were compromised, it could not drop tables or alter the
   audit table, because it has no DDL rights.

 WHY TERRAFORM DOESN'T DO THIS
   Terraform talks to Azure Resource Manager. Database users live INSIDE the
   database and are created with T-SQL by someone signed in as the Entra SQL
   administrator (a member of the SQL admin group). Run this script yourself.

 WHEN TO RUN IT (it is safe to run more than once)
   1st run: after the first `terraform apply`, BEFORE the first deployment.
            Creates all three users. The audit-table DENY is skipped with a
            message, because the table does not exist until the first
            migration has run.
   2nd run: after the first successful deployment (migrations have created
            [vim].[AuditEvents]). Applies the DENY.
   Re-run:  after any migration that drops and re-creates [vim].[AuditEvents]
            (dropping a table drops its permissions).

 HOW TO RUN IT (Windows PowerShell; install sqlcmd once with: winget install sqlcmd)
   az login
   sqlcmd -S <sql_server_fqdn> -d VimDb --authentication-method ActiveDirectoryDefault `
          -i infra\scripts\create-sql-users.sql `
          -v WebAppName="app-vim-dev-weu-a1b2" FunctionAppName="func-vim-dev-weu-a1b2" MigrationIdentityName="sp-vim-migrations-dev"

   Get the names from: terraform output web_app_name / function_app_name
   (run in infra\terraform\environments\<env>). The migration identity name
   is the display name of the app registration created for migrations (see
   docs/operations/infrastructure.md).

   With private networking (prod) the server has no public endpoint, so run
   this from a machine inside the VNet (or via the self-hosted runner).
===============================================================================
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT 'Database: ' + DB_NAME();

-- -- 1. Users ------------------------------------------------------------------
-- FROM EXTERNAL PROVIDER = "this is a Microsoft Entra identity". The name must
-- be the identity's display name; for a system-assigned managed identity that
-- is the app's name.

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(WebAppName)')
BEGIN
    CREATE USER [$(WebAppName)] FROM EXTERNAL PROVIDER;
    PRINT 'Created user $(WebAppName)';
END

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(FunctionAppName)')
BEGIN
    CREATE USER [$(FunctionAppName)] FROM EXTERNAL PROVIDER;
    PRINT 'Created user $(FunctionAppName)';
END

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(MigrationIdentityName)')
BEGIN
    CREATE USER [$(MigrationIdentityName)] FROM EXTERNAL PROVIDER;
    PRINT 'Created user $(MigrationIdentityName)';
END

-- -- 2. Application role -----------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'vim_app' AND type = 'R')
BEGIN
    CREATE ROLE [vim_app];
    PRINT 'Created role vim_app';
END

ALTER ROLE [db_datareader] ADD MEMBER [vim_app];
ALTER ROLE [db_datawriter] ADD MEMBER [vim_app];
GRANT EXECUTE TO [vim_app];

IF IS_ROLEMEMBER(N'vim_app', N'$(WebAppName)') = 0
    ALTER ROLE [vim_app] ADD MEMBER [$(WebAppName)];

IF IS_ROLEMEMBER(N'vim_app', N'$(FunctionAppName)') = 0
    ALTER ROLE [vim_app] ADD MEMBER [$(FunctionAppName)];

-- Case references come from a SEQUENCE. NEXT VALUE FOR needs UPDATE on the
-- sequence object, which db_datawriter (tables only) does not include.
IF OBJECT_ID(N'[vim].[CaseReferenceSeq]', N'SO') IS NOT NULL
BEGIN
    GRANT UPDATE ON OBJECT::[vim].[CaseReferenceSeq] TO [vim_app];
    PRINT 'Granted UPDATE on sequence [vim].[CaseReferenceSeq] to vim_app (case reference numbers).';
END;

-- -- 3. Insert-only audit table ----------------------------------------------
IF OBJECT_ID(N'[vim].[RouteChanges]', N'U') IS NOT NULL
BEGIN
    DENY UPDATE, DELETE ON OBJECT::[vim].[RouteChanges] TO [vim_app];
    PRINT 'Applied DENY UPDATE, DELETE on [vim].[RouteChanges] to vim_app (insert-only routing history).';
END;

IF OBJECT_ID(N'[vim].[AuditEvents]', N'U') IS NOT NULL
BEGIN
    DENY UPDATE, DELETE ON OBJECT::[vim].[AuditEvents] TO [vim_app];
    PRINT 'Applied DENY UPDATE, DELETE on [vim].[AuditEvents] to vim_app (insert-only audit).';
END
ELSE
BEGIN
    PRINT '*** [vim].[AuditEvents] does not exist yet. Run the first deployment (migrations), then run this script again to apply the insert-only DENY. ***';
END

-- -- 4. Migration identity (schema changes only) ---------------------------------
-- db_ddladmin: create/alter/drop tables, indexes, schemas.
-- db_datareader/db_datawriter: EF Core reads/writes __EFMigrationsHistory and
-- any seed data a migration inserts.
-- Deliberately NOT a member of vim_app and NOT db_owner.
IF IS_ROLEMEMBER(N'db_ddladmin', N'$(MigrationIdentityName)') = 0
    ALTER ROLE [db_ddladmin] ADD MEMBER [$(MigrationIdentityName)];

IF IS_ROLEMEMBER(N'db_datareader', N'$(MigrationIdentityName)') = 0
    ALTER ROLE [db_datareader] ADD MEMBER [$(MigrationIdentityName)];

IF IS_ROLEMEMBER(N'db_datawriter', N'$(MigrationIdentityName)') = 0
    ALTER ROLE [db_datawriter] ADD MEMBER [$(MigrationIdentityName)];

-- -- 5. Show the result --------------------------------------------------------
SELECT  member.name AS [principal],
        role.name   AS [role]
FROM    sys.database_role_members AS drm
JOIN    sys.database_principals   AS role   ON role.principal_id   = drm.role_principal_id
JOIN    sys.database_principals   AS member ON member.principal_id = drm.member_principal_id
WHERE   member.name IN (N'$(WebAppName)', N'$(FunctionAppName)', N'$(MigrationIdentityName)', N'vim_app')
ORDER BY member.name, role.name;

SELECT  pr.name AS [principal], pe.state_desc, pe.permission_name,
        OBJECT_SCHEMA_NAME(pe.major_id) + '.' + OBJECT_NAME(pe.major_id) AS [object]
FROM    sys.database_permissions AS pe
JOIN    sys.database_principals  AS pr ON pr.principal_id = pe.grantee_principal_id
WHERE   pr.name = N'vim_app';
