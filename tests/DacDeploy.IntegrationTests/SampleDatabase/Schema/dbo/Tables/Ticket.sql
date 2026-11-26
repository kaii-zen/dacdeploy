CREATE TABLE [dbo].[Ticket] (
    [ID]     INT IDENTITY (1, 1) NOT NULL,
    [Owner]  INT NULL,
    [Status] INT NULL,
    CONSTRAINT [PK_Ticket] PRIMARY KEY CLUSTERED ([ID] ASC)
);
GO

CREATE NONCLUSTERED INDEX [IX_Ticket_Owner]
    ON [dbo].[Ticket]([Owner] ASC);
