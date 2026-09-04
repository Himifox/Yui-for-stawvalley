namespace YuiToIssho;

internal readonly record struct SaveMigrationResult(bool IsSuccess, bool WasMigrated, string Code, string Message)
{
    public static SaveMigrationResult Success(bool migrated, string message) => new(true, migrated, migrated ? "SAVE-MIGRATED" : "SAVE-CURRENT", message);

    public static SaveMigrationResult Failure(string code, string message) => new(false, false, code, message);
}

internal static class SaveDataMigrator
{
    public const int MinimumSupportedSchemaVersion = 9;

    public static SaveMigrationResult Migrate(YuiToIsshoSaveData data)
    {
        if (data.SchemaVersion < MinimumSupportedSchemaVersion)
            return SaveMigrationResult.Failure("SAVE-SCHEMA-TOO-OLD", $"Save schema {data.SchemaVersion} predates the oldest supported schema {MinimumSupportedSchemaVersion}.");
        if (data.SchemaVersion > YuiToIsshoSaveData.CurrentSchemaVersion)
            return SaveMigrationResult.Failure("SAVE-SCHEMA-TOO-NEW", $"Save schema {data.SchemaVersion} requires a newer Yui version than schema {YuiToIsshoSaveData.CurrentSchemaVersion}.");

        int sourceVersion = data.SchemaVersion;
        while (data.SchemaVersion < YuiToIsshoSaveData.CurrentSchemaVersion)
        {
            switch (data.SchemaVersion)
            {
                case 9:
                    MigrateV9ToV10(data);
                    break;
                case 10:
                    MigrateV10ToV11(data);
                    break;
                default:
                    return SaveMigrationResult.Failure("SAVE-MIGRATION-MISSING", $"No migration step exists for schema {data.SchemaVersion}.");
            }
        }

        bool migrated = sourceVersion != data.SchemaVersion;
        return SaveMigrationResult.Success(
            migrated,
            migrated
                ? $"Migrated Yui save data from schema {sourceVersion} to {data.SchemaVersion}."
                : $"Yui save data already uses schema {data.SchemaVersion}.");
    }

    private static void MigrateV9ToV10(YuiToIsshoSaveData data)
    {
        data.Companions ??= new List<CompanionRecord>();
        data.AuthorizedChests ??= new List<AuthorizedChestRecord>();
        foreach (CompanionRecord record in data.Companions.Where(record => record is not null))
            record.Bond ??= new CompanionBondRecord();
        data.SchemaVersion = 10;
    }

    private static void MigrateV10ToV11(YuiToIsshoSaveData data)
    {
        data.Companions ??= new List<CompanionRecord>();
        foreach (CompanionRecord record in data.Companions.Where(record => record is not null))
            record.Bond ??= new CompanionBondRecord();
        data.SchemaVersion = 11;
    }
}
