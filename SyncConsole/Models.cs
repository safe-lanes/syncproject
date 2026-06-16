namespace SyncConsole;

// Keep these names in sync with Db.GetTableMetaAsync and SyncEngine
public sealed record TableMeta(
    string Pk,
    string? UpdatedCol,
    bool HasDeleted,
    List<string> CompareColumns
);

public sealed record DiffRow(string Pk, object? TVal, object? SVal, DateTime? TUpd, DateTime? SUpd);

public sealed record Decision(string Pk, object? NewVal, bool ManualRequired, string Policy);

public sealed record SyncRule(string Policy, string? Param, int WindowSec, bool RequireManualIfParallel)
{
    public static SyncRule LWW() => new("lww", null, 3600, true);
}
