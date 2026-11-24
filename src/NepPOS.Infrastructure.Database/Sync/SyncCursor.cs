namespace NepPOS.Infrastructure.Database.Sync;

/// <summary>How far this register has read a list kept elsewhere (e.g. "shared" = Head Office's shared events).</summary>
public sealed class SyncCursor
{
    public string Name { get; set; } = "";
    public long Value { get; set; }
}
