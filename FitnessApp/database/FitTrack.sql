/*
    FitTrack SQL Server schema and seed script.

        Usage with sqlcmd:
            sqlcmd -S "(localdb)\MSSQLLocalDB" -d FitTrack -i database\FitTrack.sql

    PasswordHash intentionally has no seeded value. Create users through the application
    with a slow password hasher such as ASP.NET Core PasswordHasher or Argon2id.
*/

SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

BEGIN TRANSACTION;
GO

IF OBJECT_ID(N'dbo.WorkoutExerciseSet', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WorkoutExerciseSet
    (
        WorkoutExerciseSetId UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_WorkoutExerciseSet_Id DEFAULT NEWID(),
        WorkoutExerciseId UNIQUEIDENTIFIER NOT NULL,
        SetNumber INT NOT NULL,
        Repetitions INT NOT NULL,
        WeightKg DECIMAL(9, 2) NOT NULL CONSTRAINT DF_WorkoutExerciseSet_WeightKg DEFAULT 0,
        Difficulty INT NOT NULL CONSTRAINT DF_WorkoutExerciseSet_Difficulty DEFAULT 5,
        CONSTRAINT PK_WorkoutExerciseSet PRIMARY KEY (WorkoutExerciseSetId),
        CONSTRAINT CK_WorkoutExerciseSet_SetNumber CHECK (SetNumber BETWEEN 1 AND 50),
        CONSTRAINT CK_WorkoutExerciseSet_Repetitions CHECK (Repetitions BETWEEN 1 AND 500),
        CONSTRAINT CK_WorkoutExerciseSet_WeightKg CHECK (WeightKg >= 0),
        CONSTRAINT CK_WorkoutExerciseSet_Difficulty CHECK (Difficulty BETWEEN 1 AND 10)
    );
END;
GO

IF OBJECT_ID(N'dbo.WorkoutExercise', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WorkoutExercise
    (
        WorkoutExerciseId UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_WorkoutExercise_Id DEFAULT NEWID(),
        WorkoutId UNIQUEIDENTIFIER NOT NULL,
        ExerciseId UNIQUEIDENTIFIER NULL,
        Position INT NOT NULL,
        Name NVARCHAR(160) NOT NULL,
        MuscleGroup NVARCHAR(80) NOT NULL,
        ImageUrl NVARCHAR(500) NULL,
        ExerciseType NVARCHAR(32) NOT NULL,
        Sets INT NOT NULL CONSTRAINT DF_WorkoutExercise_Sets DEFAULT 0,
        Repetitions INT NOT NULL CONSTRAINT DF_WorkoutExercise_Repetitions DEFAULT 0,
        WeightKg DECIMAL(9, 2) NOT NULL CONSTRAINT DF_WorkoutExercise_WeightKg DEFAULT 0,
        DurationMinutes INT NOT NULL CONSTRAINT DF_WorkoutExercise_DurationMinutes DEFAULT 0,
        DistanceKm DECIMAL(9, 2) NOT NULL CONSTRAINT DF_WorkoutExercise_DistanceKm DEFAULT 0,
        ElevationMeters INT NULL,
        Difficulty INT NOT NULL CONSTRAINT DF_WorkoutExercise_Difficulty DEFAULT 5,
        CONSTRAINT PK_WorkoutExercise PRIMARY KEY (WorkoutExerciseId),
        CONSTRAINT CK_WorkoutExercise_Position CHECK (Position >= 0),
        CONSTRAINT CK_WorkoutExercise_Type CHECK (ExerciseType IN (N'Strength', N'Endurance', N'Other')),
        CONSTRAINT CK_WorkoutExercise_Sets CHECK (Sets BETWEEN 0 AND 50),
        CONSTRAINT CK_WorkoutExercise_Repetitions CHECK (Repetitions BETWEEN 0 AND 500),
        CONSTRAINT CK_WorkoutExercise_WeightKg CHECK (WeightKg >= 0),
        CONSTRAINT CK_WorkoutExercise_Duration CHECK (DurationMinutes BETWEEN 0 AND 1440),
        CONSTRAINT CK_WorkoutExercise_Distance CHECK (DistanceKm >= 0),
        CONSTRAINT CK_WorkoutExercise_Elevation CHECK (ElevationMeters IS NULL OR ElevationMeters >= 0),
        CONSTRAINT CK_WorkoutExercise_Difficulty CHECK (Difficulty BETWEEN 1 AND 10)
    );
END;
GO

IF OBJECT_ID(N'dbo.Workout', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Workout
    (
        WorkoutId UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Workout_Id DEFAULT NEWID(),
        OwnerUserId UNIQUEIDENTIFIER NOT NULL,
        Name NVARCHAR(160) NOT NULL,
        PerformedAt DATE NOT NULL,
        RecordedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Workout_RecordedAt DEFAULT SYSUTCDATETIME(),
        DurationMinutes INT NOT NULL,
        Notes NVARCHAR(2000) NOT NULL CONSTRAINT DF_Workout_Notes DEFAULT N'',
        Visibility NVARCHAR(16) NOT NULL CONSTRAINT DF_Workout_Visibility DEFAULT N'Personal',
        SourceWorkoutId UNIQUEIDENTIFIER NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Workout_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Workout_UpdatedAt DEFAULT SYSUTCDATETIME(),
        RowVersion ROWVERSION NOT NULL,
        CONSTRAINT PK_Workout PRIMARY KEY (WorkoutId),
        CONSTRAINT CK_Workout_Duration CHECK (DurationMinutes BETWEEN 1 AND 1440),
        CONSTRAINT CK_Workout_Visibility CHECK (Visibility IN (N'Personal', N'Global'))
    );
END;
GO

IF OBJECT_ID(N'dbo.ExerciseMuscleGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExerciseMuscleGroup
    (
        ExerciseId UNIQUEIDENTIFIER NOT NULL,
        MuscleGroup NVARCHAR(80) NOT NULL,
        Position INT NOT NULL,
        CONSTRAINT PK_ExerciseMuscleGroup PRIMARY KEY (ExerciseId, MuscleGroup),
        CONSTRAINT CK_ExerciseMuscleGroup_Position CHECK (Position >= 0)
    );
END;
GO

IF OBJECT_ID(N'dbo.Exercise', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Exercise
    (
        ExerciseId UNIQUEIDENTIFIER NOT NULL,
        Name NVARCHAR(160) NOT NULL,
        Description NVARCHAR(1000) NOT NULL,
        ExerciseType NVARCHAR(32) NOT NULL,
        Category NVARCHAR(80) NOT NULL,
        ImageUrl NVARCHAR(500) NULL,
        IsSystem BIT NOT NULL CONSTRAINT DF_Exercise_IsSystem DEFAULT 0,
        CreatedByUserId UNIQUEIDENTIFIER NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Exercise_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Exercise_UpdatedAt DEFAULT SYSUTCDATETIME(),
        RowVersion ROWVERSION NOT NULL,
        CONSTRAINT PK_Exercise PRIMARY KEY (ExerciseId),
        CONSTRAINT UQ_Exercise_Name UNIQUE (Name),
        CONSTRAINT CK_Exercise_Type CHECK (ExerciseType IN (N'Strength', N'Endurance', N'Other'))
    );
END;
GO

IF OBJECT_ID(N'dbo.AppUser', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AppUser
    (
        UserId UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AppUser_Id DEFAULT NEWID(),
        UserName NVARCHAR(120) NOT NULL,
        DisplayName NVARCHAR(160) NULL,
        NormalizedUserName AS UPPER(UserName) PERSISTED,
        PasswordHash NVARCHAR(512) NOT NULL,
        Role NVARCHAR(32) NOT NULL CONSTRAINT DF_AppUser_Role DEFAULT N'User',
        IsDisabled BIT NOT NULL CONSTRAINT DF_AppUser_IsDisabled DEFAULT 0,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_AppUser_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_AppUser_UpdatedAt DEFAULT SYSUTCDATETIME(),
        LastLoginAt DATETIME2(0) NULL,
        RowVersion ROWVERSION NOT NULL,
        CONSTRAINT PK_AppUser PRIMARY KEY (UserId),
        CONSTRAINT UQ_AppUser_NormalizedUserName UNIQUE (NormalizedUserName),
        CONSTRAINT CK_AppUser_Role CHECK (Role IN (N'User', N'Admin')),
        CONSTRAINT CK_AppUser_PasswordHash_NotPlaintext CHECK (LEN(PasswordHash) >= 60)
    );
END;
GO

IF COL_LENGTH(N'dbo.AppUser', N'DisplayName') IS NULL
BEGIN
    ALTER TABLE dbo.AppUser ADD DisplayName NVARCHAR(160) NULL;
    EXEC(N'UPDATE dbo.AppUser SET DisplayName = UserName WHERE DisplayName IS NULL;');
END;
GO

IF OBJECT_ID(N'dbo.UserProfile', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserProfile
    (
        UserId UNIQUEIDENTIFIER NOT NULL,
        WeeklyGoal INT NOT NULL CONSTRAINT DF_UserProfile_WeeklyGoal DEFAULT 3,
        IsLeaderboardPublic BIT NOT NULL CONSTRAINT DF_UserProfile_IsLeaderboardPublic DEFAULT 0,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_UserProfile_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_UserProfile_UpdatedAt DEFAULT SYSUTCDATETIME(),
        RowVersion ROWVERSION NOT NULL,
        CONSTRAINT PK_UserProfile PRIMARY KEY (UserId),
        CONSTRAINT CK_UserProfile_WeeklyGoal CHECK (WeeklyGoal BETWEEN 1 AND 14)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserProfile_AppUser')
    ALTER TABLE dbo.UserProfile ADD CONSTRAINT FK_UserProfile_AppUser FOREIGN KEY (UserId) REFERENCES dbo.AppUser(UserId) ON DELETE CASCADE;
GO

INSERT INTO dbo.UserProfile (UserId)
SELECT u.UserId
FROM dbo.AppUser u
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserProfile p WHERE p.UserId = u.UserId);
GO

IF OBJECT_ID(N'dbo.UserWeeklyGoal', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserWeeklyGoal
    (
        UserId UNIQUEIDENTIFIER NOT NULL,
        EffectiveWeekStart DATE NOT NULL,
        WeeklyGoal INT NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_UserWeeklyGoal_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_UserWeeklyGoal_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_UserWeeklyGoal PRIMARY KEY (UserId, EffectiveWeekStart),
        CONSTRAINT CK_UserWeeklyGoal_WeekStart CHECK (DATEDIFF(DAY, CONVERT(DATE, '19000101', 112), EffectiveWeekStart) % 7 = 0),
        CONSTRAINT CK_UserWeeklyGoal_Goal CHECK (WeeklyGoal BETWEEN 1 AND 14)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserWeeklyGoal_AppUser')
    ALTER TABLE dbo.UserWeeklyGoal ADD CONSTRAINT FK_UserWeeklyGoal_AppUser FOREIGN KEY (UserId) REFERENCES dbo.AppUser(UserId) ON DELETE CASCADE;
GO

INSERT INTO dbo.UserWeeklyGoal (UserId, EffectiveWeekStart, WeeklyGoal)
SELECT p.UserId, CONVERT(DATE, '19000101', 112), p.WeeklyGoal
FROM dbo.UserProfile p
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserWeeklyGoal g WHERE g.UserId = p.UserId);
GO

IF OBJECT_ID(N'dbo.TR_UserProfile_CreateWeeklyGoal', N'TR') IS NULL
    EXEC(N'
CREATE TRIGGER dbo.TR_UserProfile_CreateWeeklyGoal
ON dbo.UserProfile
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.UserWeeklyGoal (UserId, EffectiveWeekStart, WeeklyGoal)
    SELECT i.UserId, CONVERT(DATE, ''19000101'', 112), i.WeeklyGoal
    FROM inserted i
    WHERE NOT EXISTS (SELECT 1 FROM dbo.UserWeeklyGoal g WHERE g.UserId = i.UserId);
END;');
GO

IF OBJECT_ID(N'dbo.TR_AppUser_CreateProfile', N'TR') IS NULL
    EXEC(N'
CREATE TRIGGER dbo.TR_AppUser_CreateProfile
ON dbo.AppUser
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.UserProfile (UserId)
    SELECT i.UserId
    FROM inserted i
    WHERE NOT EXISTS (SELECT 1 FROM dbo.UserProfile p WHERE p.UserId = i.UserId);
END;');
GO

IF OBJECT_ID(N'dbo.LeaderboardCategory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LeaderboardCategory
    (
        CategoryKey NVARCHAR(64) NOT NULL,
        DisplayName NVARCHAR(120) NOT NULL,
        Description NVARCHAR(500) NOT NULL,
        Unit NVARCHAR(32) NOT NULL,
        IsEnabled BIT NOT NULL CONSTRAINT DF_LeaderboardCategory_IsEnabled DEFAULT 1,
        SortOrder INT NOT NULL,
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LeaderboardCategory_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_LeaderboardCategory PRIMARY KEY (CategoryKey),
        CONSTRAINT CK_LeaderboardCategory_Key CHECK (CategoryKey IN (N'weekly-exercises', N'weekly-volume', N'weekly-running-distance', N'current-weekly-streak')),
        CONSTRAINT CK_LeaderboardCategory_SortOrder CHECK (SortOrder BETWEEN 0 AND 1000)
    );
END;
GO

MERGE dbo.LeaderboardCategory AS target
USING (VALUES
    (N'weekly-exercises', N'Übungen der Woche', N'Anzahl absolvierter Übungen in der aktuellen Woche.', N'Übungen', 1),
    (N'weekly-volume', N'Trainingsvolumen der Woche', N'Summe aus Wiederholungen × Gewicht in der aktuellen Woche.', N'kg', 2),
    (N'weekly-running-distance', N'Laufdistanz der Woche', N'Gesamte Lauf- und Ausdauerentfernung in der aktuellen Woche.', N'km', 3),
    (N'current-weekly-streak', N'Aktuelle Wochenserie', N'Aufeinanderfolgende Wochen mit erreichtem Trainingsziel.', N'Wochen', 4)
) AS source (CategoryKey, DisplayName, Description, Unit, SortOrder)
ON target.CategoryKey = source.CategoryKey
WHEN NOT MATCHED THEN
    INSERT (CategoryKey, DisplayName, Description, Unit, SortOrder)
    VALUES (source.CategoryKey, source.DisplayName, source.Description, source.Unit, source.SortOrder);
GO

IF OBJECT_ID(N'dbo.UserEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserEvent
    (
        EventId UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_UserEvent_Id DEFAULT NEWID(),
        OwnerUserId UNIQUEIDENTIFIER NOT NULL,
        Title NVARCHAR(160) NOT NULL,
        EventType NVARCHAR(32) NOT NULL,
        EventDate DATE NOT NULL,
        Description NVARCHAR(1000) NOT NULL CONSTRAINT DF_UserEvent_Description DEFAULT N'',
        IsCompleted BIT NOT NULL CONSTRAINT DF_UserEvent_IsCompleted DEFAULT 0,
        CompletedAt DATETIME2(0) NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_UserEvent_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_UserEvent_UpdatedAt DEFAULT SYSUTCDATETIME(),
        RowVersion ROWVERSION NOT NULL,
        CONSTRAINT PK_UserEvent PRIMARY KEY (EventId),
        CONSTRAINT CK_UserEvent_Type CHECK (EventType IN (N'Competition', N'Running', N'PersonalGoal', N'Other')),
        CONSTRAINT CK_UserEvent_Completed CHECK ((IsCompleted = 0 AND CompletedAt IS NULL) OR (IsCompleted = 1 AND CompletedAt IS NOT NULL))
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserEvent_AppUser')
    ALTER TABLE dbo.UserEvent ADD CONSTRAINT FK_UserEvent_AppUser FOREIGN KEY (OwnerUserId) REFERENCES dbo.AppUser(UserId) ON DELETE CASCADE;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UserEvent_Owner_Date')
    CREATE INDEX IX_UserEvent_Owner_Date ON dbo.UserEvent(OwnerUserId, IsCompleted, EventDate);
GO

IF COL_LENGTH(N'dbo.Workout', N'IsTemplate') IS NULL
BEGIN
    ALTER TABLE dbo.Workout ADD IsTemplate BIT NOT NULL CONSTRAINT DF_Workout_IsTemplate DEFAULT 0;
    EXEC(N'UPDATE dbo.Workout SET IsTemplate = 1 WHERE Visibility = N''Global'';');
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Exercise_CreatedByUser')
    ALTER TABLE dbo.Exercise ADD CONSTRAINT FK_Exercise_CreatedByUser FOREIGN KEY (CreatedByUserId) REFERENCES dbo.AppUser(UserId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ExerciseMuscleGroup_Exercise')
    ALTER TABLE dbo.ExerciseMuscleGroup ADD CONSTRAINT FK_ExerciseMuscleGroup_Exercise FOREIGN KEY (ExerciseId) REFERENCES dbo.Exercise(ExerciseId) ON DELETE CASCADE;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Workout_OwnerUser')
    ALTER TABLE dbo.Workout ADD CONSTRAINT FK_Workout_OwnerUser FOREIGN KEY (OwnerUserId) REFERENCES dbo.AppUser(UserId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Workout_SourceWorkout')
    ALTER TABLE dbo.Workout ADD CONSTRAINT FK_Workout_SourceWorkout FOREIGN KEY (SourceWorkoutId) REFERENCES dbo.Workout(WorkoutId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_WorkoutExercise_Workout')
    ALTER TABLE dbo.WorkoutExercise ADD CONSTRAINT FK_WorkoutExercise_Workout FOREIGN KEY (WorkoutId) REFERENCES dbo.Workout(WorkoutId) ON DELETE CASCADE;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_WorkoutExercise_Exercise')
    ALTER TABLE dbo.WorkoutExercise ADD CONSTRAINT FK_WorkoutExercise_Exercise FOREIGN KEY (ExerciseId) REFERENCES dbo.Exercise(ExerciseId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_WorkoutExerciseSet_WorkoutExercise')
    ALTER TABLE dbo.WorkoutExerciseSet ADD CONSTRAINT FK_WorkoutExerciseSet_WorkoutExercise FOREIGN KEY (WorkoutExerciseId) REFERENCES dbo.WorkoutExercise(WorkoutExerciseId) ON DELETE CASCADE;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Workout_Owner_RecordedAt')
    CREATE INDEX IX_Workout_Owner_RecordedAt ON dbo.Workout(OwnerUserId, RecordedAt DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Workout_Global')
    CREATE INDEX IX_Workout_Global ON dbo.Workout(Visibility, Name) WHERE Visibility = N'Global';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Workout_Owner_Template_Name')
    CREATE INDEX IX_Workout_Owner_Template_Name ON dbo.Workout(OwnerUserId, IsTemplate, Name);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Exercise_Category_Type')
    CREATE INDEX IX_Exercise_Category_Type ON dbo.Exercise(Category, ExerciseType, Name);
GO

IF OBJECT_ID(N'dbo.TR_Workout_GlobalRequiresAdmin', N'TR') IS NULL
    EXEC(N'
CREATE TRIGGER dbo.TR_Workout_GlobalRequiresAdmin
ON dbo.Workout
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        JOIN dbo.AppUser u ON u.UserId = i.OwnerUserId
        WHERE i.Visibility = N''Global'' AND u.Role <> N''Admin''
    )
    BEGIN
        RAISERROR(N''Nur Admins dürfen allgemein verfügbare Trainings speichern.'', 16, 1);
        ROLLBACK TRANSACTION;
        RETURN;
    END;
END;');
GO

COMMIT TRANSACTION;
GO

SELECT
    (SELECT COUNT(*) FROM dbo.Exercise) AS ExerciseCount,
    (SELECT COUNT(*) FROM dbo.ExerciseMuscleGroup) AS ExerciseMuscleGroupCount,
    (SELECT COUNT(*) FROM dbo.AppUser) AS UserCount,
    (SELECT COUNT(*) FROM dbo.UserProfile) AS UserProfileCount,
    (SELECT COUNT(*) FROM dbo.UserWeeklyGoal) AS UserWeeklyGoalCount,
    (SELECT COUNT(*) FROM dbo.LeaderboardCategory) AS LeaderboardCategoryCount,
    (SELECT COUNT(*) FROM dbo.UserEvent) AS UserEventCount;
GO