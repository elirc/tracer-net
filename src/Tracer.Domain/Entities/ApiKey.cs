namespace Tracer.Domain.Entities;

/// <summary>
/// A credential belonging to a <see cref="User"/>. The raw token is shown once,
/// at creation, and never stored: only <see cref="KeyHash"/> is persisted, so a
/// database dump does not hand over working credentials.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Human label, e.g. "CI pipeline", so a key can be recognised before revoking it.</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 of the raw token. See <see cref="ApiKeyToken"/> for why SHA-256 is the right hash here.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Leading characters of the raw token, kept in the clear so a key is identifiable in a list.</summary>
    public required string Prefix { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Coarse "last seen" timestamp, refreshed at most once a minute so that
    /// authenticating a read does not turn into a write on every request.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Set when the key is revoked; revoked keys stay for the audit trail rather than being deleted.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
