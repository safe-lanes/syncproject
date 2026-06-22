namespace SyncConsole;

// Keep these names in sync with Db.GetTableMetaAsync and SyncEngine
public sealed record TableMeta(
    string Pk,
    string? UpdatedCol,
    bool HasDeleted,
    List<string> CompareColumns,
    List<string>? BusinessKey = null
);

// Per-table sync configuration loaded from sails_master.*_sync_tables.
// BusinessKey comes from the optional businessKeyColumn JSON column (e.g.
// ["nearmissId","nearmissImpactId"]); empty when the column is NULL/blank.
public sealed record SyncTableConfig(string Table, List<string> BusinessKey);

public sealed record DiffRow(string Pk, object? TVal, object? SVal, DateTime? TUpd, DateTime? SUpd);

public sealed record Decision(string Pk, object? NewVal, bool ManualRequired, string Policy);

public sealed record SyncRule(string Policy, string? Param, int WindowSec, bool RequireManualIfParallel)
{
    public static SyncRule LWW() => new("lww", null, 3600, true);
}
