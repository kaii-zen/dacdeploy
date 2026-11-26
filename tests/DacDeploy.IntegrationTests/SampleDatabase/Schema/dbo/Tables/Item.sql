-- Category comes before Name: a database with Category last makes the deploy rebuild the table.
CREATE TABLE [dbo].[Item] (
    [ID]       INT           IDENTITY (1, 1) NOT NULL,
    [Category] NVARCHAR (50) NULL,
    [Name]     NVARCHAR (50) NULL,
    CONSTRAINT [PK_Item] PRIMARY KEY CLUSTERED ([ID] ASC)
);
