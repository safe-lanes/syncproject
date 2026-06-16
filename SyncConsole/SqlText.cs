namespace SyncConsole;

public static class SqlText
{
    public static string InsertMissing(string localDb, string vesselDb, string table, string pk) => $@"
        INSERT INTO `{localDb}`.`{table}`
        SELECT s.*
        FROM `{vesselDb}`.`{table}` s
        LEFT JOIN `{localDb}`.`{table}` t ON t.`{pk}` = s.`{pk}`
        WHERE t.`{pk}` IS NULL
    ";

    public static string SelectPkBatch(string localDb, string vesselDb, string table, string pk) => $@"
        SELECT t.`{pk}` AS pk
        FROM `{localDb}`.`{table}` t
        JOIN `{vesselDb}`.`{table}` s ON s.`{pk}` = t.`{pk}`
        WHERE (@lastPk IS NULL OR t.`{pk}` > @lastPk)
        ORDER BY t.`{pk}`
        LIMIT @batch
    ";

    public static string SelectColumnDiffs(string localDb, string vesselDb, string table, string pk, string col, string? updatedCol)
    {
        var ts = updatedCol is null
            ? "NULL AS tt, NULL AS ts"
            : $"t.`{updatedCol}` AS tt, s.`{updatedCol}` AS ts";
        return $@"
            SELECT t.`{pk}` AS pk, t.`{col}` AS tval, s.`{col}` AS sval, {ts}
            FROM `{localDb}`.`{table}` t
            JOIN `{vesselDb}`.`{table}` s ON s.`{pk}` = t.`{pk}`
            WHERE NOT (t.`{col}` <=> s.`{col}`)
              AND t.`{pk}` IN @pks
        ";
    }

    public static string UpdateColumn(string localDb, string table, string pk, string col) => $@"
        UPDATE `{localDb}`.`{table}`
        SET `{col}` = @newVal
        WHERE `{pk}` = @pk AND NOT (`{col}` <=> @newVal)
    ";

    public static string PropagateDeletions(string localDb, string vesselDb, string table, string pk) => $@"
        UPDATE `{localDb}`.`{table}` t
        JOIN `{vesselDb}`.`{table}` s ON s.`{pk}`=t.`{pk}`
        SET t.`is_deleted` = 1
        WHERE s.`is_deleted`=1 AND COALESCE(t.`is_deleted`,0)=0
    ";
}
