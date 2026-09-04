/*
    FitTrack SQL Server schema and seed script.

        Usage with sqlcmd:
            sqlcmd -S "(localdb)\MSSQLLocalDB" -d FitTrack -i database\FitTrack.sql

        The default ExerciseCatalogPath points to C:\Sport\wwwroot\data\exercises.json.

    PasswordHash intentionally has no seeded value. Create users through the application
    with a slow password hasher such as ASP.NET Core PasswordHasher or Argon2id.
*/

:setvar ExerciseCatalogPath "C:\Sport\wwwroot\data\exercises.json"

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

DECLARE @catalogJson NVARCHAR(MAX);

SELECT @catalogJson = BulkColumn
FROM OPENROWSET(BULK '$(ExerciseCatalogPath)', SINGLE_CLOB, CODEPAGE = '65001') AS catalogFile;

IF @catalogJson IS NULL OR ISJSON(@catalogJson) <> 1
    THROW 50001, 'Exercise catalog JSON could not be loaded or is invalid.', 1;

IF OBJECT_ID(N'tempdb..#ExerciseCatalog', N'U') IS NOT NULL
    DROP TABLE #ExerciseCatalog;

CREATE TABLE #ExerciseCatalog
(
    ExerciseId UNIQUEIDENTIFIER NOT NULL,
    Name NVARCHAR(160) NOT NULL,
    Description NVARCHAR(1000) NOT NULL,
    ExerciseType NVARCHAR(32) NOT NULL,
    Category NVARCHAR(80) NOT NULL,
    ImageUrl NVARCHAR(500) NULL
);

INSERT INTO #ExerciseCatalog (ExerciseId, Name, Description, ExerciseType, Category, ImageUrl)
SELECT
    CAST(JSON_VALUE(item.value, '$.id') AS UNIQUEIDENTIFIER),
    JSON_VALUE(item.value, '$.name'),
    JSON_VALUE(item.value, '$.description'),
    JSON_VALUE(item.value, '$.exerciseType'),
    JSON_VALUE(item.value, '$.category'),
    JSON_VALUE(item.value, '$.imageUrl')
FROM OPENJSON(@catalogJson) AS item;

DELETE muscleGroup
FROM dbo.ExerciseMuscleGroup AS muscleGroup
JOIN dbo.Exercise AS exercise ON exercise.ExerciseId = muscleGroup.ExerciseId
WHERE exercise.IsSystem = 1;

IF OBJECT_ID(N'tempdb..#ExerciseIdMap', N'U') IS NOT NULL
    DROP TABLE #ExerciseIdMap;

CREATE TABLE #ExerciseIdMap
(
    OldExerciseId UNIQUEIDENTIFIER NOT NULL,
    NewExerciseId UNIQUEIDENTIFIER NOT NULL,
    Name NVARCHAR(160) NOT NULL
);

INSERT INTO #ExerciseIdMap (OldExerciseId, NewExerciseId, Name)
SELECT currentExercise.ExerciseId, source.ExerciseId, currentExercise.Name
FROM dbo.Exercise AS currentExercise
JOIN #ExerciseCatalog AS source ON source.Name = currentExercise.Name
WHERE currentExercise.ExerciseId <> source.ExerciseId
AND NOT EXISTS
(
    SELECT 1
    FROM dbo.Exercise AS existingExercise
    WHERE existingExercise.ExerciseId = source.ExerciseId
    AND existingExercise.Name <> currentExercise.Name
);

IF EXISTS (SELECT 1 FROM #ExerciseIdMap)
BEGIN
    ALTER TABLE dbo.WorkoutExercise NOCHECK CONSTRAINT FK_WorkoutExercise_Exercise;

    UPDATE currentExercise
    SET ExerciseId = map.NewExerciseId
    FROM dbo.Exercise AS currentExercise
    JOIN #ExerciseIdMap AS map ON map.OldExerciseId = currentExercise.ExerciseId;

    UPDATE workoutExercise
    SET ExerciseId = map.NewExerciseId
    FROM dbo.WorkoutExercise AS workoutExercise
    JOIN #ExerciseIdMap AS map ON map.OldExerciseId = workoutExercise.ExerciseId;

    ALTER TABLE dbo.WorkoutExercise WITH CHECK CHECK CONSTRAINT FK_WorkoutExercise_Exercise;
END;

MERGE dbo.Exercise AS target
USING #ExerciseCatalog AS source
ON target.Name = source.Name
WHEN MATCHED THEN UPDATE SET
    Description = source.Description,
    ExerciseType = source.ExerciseType,
    Category = source.Category,
    ImageUrl = source.ImageUrl,
    IsSystem = 1,
    UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN INSERT
    (ExerciseId, Name, Description, ExerciseType, Category, ImageUrl, IsSystem)
VALUES
    (source.ExerciseId, source.Name, source.Description, source.ExerciseType, source.Category, source.ImageUrl, 1);

INSERT INTO dbo.ExerciseMuscleGroup (ExerciseId, MuscleGroup, Position)
SELECT
    exercise.ExerciseId,
    muscle.[value],
    muscle.[key]
FROM OPENJSON(@catalogJson) AS item
JOIN dbo.Exercise AS exercise ON exercise.Name = JSON_VALUE(item.value, '$.name')
CROSS APPLY OPENJSON(JSON_QUERY(item.value, '$.muscleGroups')) AS muscle;
GO

COMMIT TRANSACTION;
GO

SELECT
    (SELECT COUNT(*) FROM dbo.Exercise) AS ExerciseCount,
    (SELECT COUNT(*) FROM dbo.ExerciseMuscleGroup) AS ExerciseMuscleGroupCount,
    (SELECT COUNT(*) FROM dbo.AppUser) AS UserCount;
GO