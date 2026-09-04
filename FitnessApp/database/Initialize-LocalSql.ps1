param(
    [string]$ServerInstance = "(localdb)\MSSQLLocalDB",
    [string]$DatabaseName = "FitTrack",
    [switch]$Reset
)

$ErrorActionPreference = "Stop"
$schemaPath = Join-Path $PSScriptRoot "FitTrack.sql"

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd wurde nicht gefunden. Bitte SQL Server command line tools installieren."
}

if ($Reset) {
    sqlcmd -S $ServerInstance -Q "IF DB_ID(N'$DatabaseName') IS NOT NULL BEGIN ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DatabaseName]; END; CREATE DATABASE [$DatabaseName];"
}
else {
    sqlcmd -S $ServerInstance -Q "IF DB_ID(N'$DatabaseName') IS NULL CREATE DATABASE [$DatabaseName];"
}

sqlcmd -S $ServerInstance -d $DatabaseName -i $schemaPath
sqlcmd -S $ServerInstance -d $DatabaseName -Q "SELECT (SELECT COUNT(*) FROM dbo.Exercise) AS ExerciseCount, (SELECT COUNT(*) FROM dbo.ExerciseMuscleGroup) AS MuscleGroupCount, (SELECT COUNT(*) FROM dbo.AppUser) AS UserCount;"