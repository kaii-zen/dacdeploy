-- dacdeploy plan. Review it, then apply it with dacdeploy apply.
-- Checksum: a155d724a3458fe32d3a07e4968649b654f9726276a6438974a43f2386c5bb17
-- Environment: dev
-- Server: localhost,1433
-- Database: Shop
-- $(OtherDb): Other
-- Prune: yes
-- Fingerprint: FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210
--
-- In the database but not in the project, dropped by this plan:
--   View    [dbo].[OldView]
--   Column  [dbo].[Item].[Removed]

DROP VIEW [dbo].[OldView];

GO
