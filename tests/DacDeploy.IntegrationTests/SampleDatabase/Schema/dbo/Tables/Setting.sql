CREATE TABLE [dbo].[Setting] (
    [Name]  NVARCHAR (50) NOT NULL,
    [Value] NVARCHAR (50) NULL,
    CONSTRAINT [PK_Setting] PRIMARY KEY CLUSTERED ([Name] ASC)
);
