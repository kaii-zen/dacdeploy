-- Reference data: run again whenever this file changes.
IF NOT EXISTS (SELECT 1 FROM dbo.Setting WHERE Name = N'Colour')
    INSERT dbo.Setting (Name, Value) VALUES (N'Colour', N'blue');
