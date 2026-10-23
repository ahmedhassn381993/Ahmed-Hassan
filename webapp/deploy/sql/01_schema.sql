IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Audit] (
        [Id] bigint NOT NULL IDENTITY,
        [At] datetime2 NOT NULL,
        [User] nvarchar(120) NOT NULL,
        [Action] nvarchar(60) NOT NULL,
        [Entity] nvarchar(60) NOT NULL,
        [EntityKey] nvarchar(60) NOT NULL,
        [CycleId] int NULL,
        [Details] nvarchar(4000) NULL,
        CONSTRAINT [PK_Audit] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Cycles] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(40) NOT NULL,
        [FirstMonth] date NOT NULL,
        [MonthCount] int NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [PlannerDue] date NULL,
        [ReviewDue] date NULL,
        [ApprovalDue] date NULL,
        [HasAfterData] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [CreatedBy] nvarchar(120) NOT NULL,
        [LockedAt] datetime2 NULL,
        [LockedBy] nvarchar(120) NULL,
        [LockedCapacity] nvarchar(max) NULL,
        CONSTRAINT [PK_Cycles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [DepartmentReviews] (
        [CycleId] int NOT NULL,
        [DeptCode] nvarchar(8) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [SubmittedAt] datetime2 NULL,
        [SubmittedBy] nvarchar(120) NULL,
        [LastReminderAt] datetime2 NULL,
        [ReminderCount] int NOT NULL,
        [Note] nvarchar(500) NULL,
        CONSTRAINT [PK_DepartmentReviews] PRIMARY KEY ([CycleId], [DeptCode])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Departments] (
        [Code] nvarchar(8) NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [ParentCode] nvarchar(8) NULL,
        [Permanent] int NOT NULL,
        [Temporary] int NOT NULL,
        [Managers] int NOT NULL,
        [LoanToUnpaid] int NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Departments] PRIMARY KEY ([Code])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Notifications] (
        [Id] bigint NOT NULL IDENTITY,
        [CycleId] int NOT NULL,
        [DeptCode] nvarchar(8) NOT NULL,
        [Recipient] nvarchar(200) NOT NULL,
        [Subject] nvarchar(200) NOT NULL,
        [Body] nvarchar(4000) NOT NULL,
        [At] datetime2 NOT NULL,
        [Sent] bit NOT NULL,
        [Error] nvarchar(500) NULL,
        CONSTRAINT [PK_Notifications] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Projects] (
        [Code] nvarchar(20) NOT NULL,
        [BaseCode] nvarchar(12) NOT NULL,
        [Name] nvarchar(250) NOT NULL,
        [Client] nvarchar(60) NULL,
        [MasterCategory] nvarchar(4) NULL,
        [InMaster] bit NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Projects] PRIMARY KEY ([Code])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Settings] (
        [Key] nvarchar(60) NOT NULL,
        [Value] nvarchar(200) NOT NULL,
        CONSTRAINT [PK_Settings] PRIMARY KEY ([Key])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Users] (
        [Upn] nvarchar(120) NOT NULL,
        [DisplayName] nvarchar(120) NOT NULL,
        [Role] nvarchar(40) NOT NULL,
        [DeptCodes] nvarchar(200) NULL,
        [Email] nvarchar(200) NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Upn])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Lines] (
        [Id] int NOT NULL IDENTITY,
        [CycleId] int NOT NULL,
        [DeptCode] nvarchar(8) NOT NULL,
        [SectionCode] nvarchar(8) NOT NULL,
        [Category] nvarchar(20) NOT NULL,
        [ApprovedCategory] nvarchar(20) NULL,
        [AfterCategory] nvarchar(20) NULL,
        [ProjectCode] nvarchar(20) NOT NULL,
        [Activity] nvarchar(250) NOT NULL,
        [PlannerUpn] nvarchar(120) NULL,
        [Origin] nvarchar(20) NOT NULL,
        CONSTRAINT [PK_Lines] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Lines_Cycles_CycleId] FOREIGN KEY ([CycleId]) REFERENCES [Cycles] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Proposals] (
        [Id] int NOT NULL IDENTITY,
        [CycleId] int NOT NULL,
        [LineId] int NULL,
        [DeptCode] nvarchar(8) NOT NULL,
        [Kind] nvarchar(20) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [Reason] nvarchar(2000) NOT NULL,
        [ChangeTypes] nvarchar(250) NULL,
        [RequestedCategory] nvarchar(20) NULL,
        [NewProjectCode] nvarchar(20) NULL,
        [NewActivity] nvarchar(250) NULL,
        [NewCategory] nvarchar(20) NULL,
        [NewSectionCode] nvarchar(8) NULL,
        [CreatedBy] nvarchar(120) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [SubmittedAt] datetime2 NULL,
        [ReviewedBy] nvarchar(120) NULL,
        [ReviewedAt] datetime2 NULL,
        [ReviewComment] nvarchar(2000) NULL,
        [ImplementedBy] nvarchar(120) NULL,
        [ImplementedAt] datetime2 NULL,
        CONSTRAINT [PK_Proposals] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Proposals_Cycles_CycleId] FOREIGN KEY ([CycleId]) REFERENCES [Cycles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_Proposals_Lines_LineId] FOREIGN KEY ([LineId]) REFERENCES [Lines] ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [Values] (
        [LineId] int NOT NULL,
        [MonthIndex] int NOT NULL,
        [PlannerMH] float NULL,
        [ApprovedMH] float NULL,
        [AfterMH] float NULL,
        CONSTRAINT [PK_Values] PRIMARY KEY ([LineId], [MonthIndex]),
        CONSTRAINT [FK_Values_Lines_LineId] FOREIGN KEY ([LineId]) REFERENCES [Lines] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE TABLE [ProposalValues] (
        [ProposalId] int NOT NULL,
        [MonthIndex] int NOT NULL,
        [ProposedMH] float NULL,
        [ApprovedMH] float NULL,
        CONSTRAINT [PK_ProposalValues] PRIMARY KEY ([ProposalId], [MonthIndex]),
        CONSTRAINT [FK_ProposalValues_Proposals_ProposalId] FOREIGN KEY ([ProposalId]) REFERENCES [Proposals] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE INDEX [IX_Audit_CycleId_At] ON [Audit] ([CycleId], [At]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Cycles_Name] ON [Cycles] ([Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE INDEX [IX_Lines_CycleId_DeptCode] ON [Lines] ([CycleId], [DeptCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE INDEX [IX_Lines_CycleId_ProjectCode] ON [Lines] ([CycleId], [ProjectCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE INDEX [IX_Notifications_CycleId_DeptCode] ON [Notifications] ([CycleId], [DeptCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE INDEX [IX_Proposals_CycleId_DeptCode] ON [Proposals] ([CycleId], [DeptCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE INDEX [IX_Proposals_CycleId_Status] ON [Proposals] ([CycleId], [Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    CREATE INDEX [IX_Proposals_LineId] ON [Proposals] ([LineId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023214533_Initial'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261023214533_Initial', N'8.0.11');
END;
GO

COMMIT;
GO

