-- dacdeploy plan. Review it, then apply it with dacdeploy apply.
-- Checksum: 7e2883b5a473bff1f169d345add1c038d8e7dfec257aa7cc5631270540ce6abe
-- Environment: dev
-- Server: localhost,1433
-- Database: Shop
-- $(OtherDb): Other
-- Fingerprint: 0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF
--
-- Migrations to run before the schema changes:
--   20261120-pre-and-post.sql
--
-- Schema changes:
--   Create  [dbo].[NewView]
--   Alter   [dbo].[Item]
--
-- Reference data to update:
--   Settings.sql
--
-- Migrations to run after the schema changes:
--   20261120-pre-and-post.sql
--   20261121-backfill.sql
--
-- In the database but not in the project, kept (drop with --prune):
--   View    [dbo].[OldView]
--   Column  [dbo].[Item].[Removed] (made nullable)

PRINT N'Altering [dbo].[Item]...';

GO
ALTER TABLE [dbo].[Item] ADD [Note] NVARCHAR (50) NULL;

GO
