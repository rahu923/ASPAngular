namespace OrderManagement.Infrastructure;
public class RefreshFamily
{
    // Family state applies to the whole rotation chain created by one sign-in.
    // Revoking only one already-consumed row would leave its successor usable.
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}
public class RefreshSession
{
    // This is a hash/history record, not the raw cookie secret. Consumed rows remain
    // available to recognize replay; their family's absolute expiry bounds the session.
    public long Id { get; set; }
    public Guid FamilyId { get; set; }
    public RefreshFamily Family { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}
