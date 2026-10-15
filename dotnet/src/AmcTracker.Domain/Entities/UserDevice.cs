namespace AmcTracker.Domain.Entities;

/// <summary>A phone bound to a user. Holds the SHA-256 hash of its current refresh token.</summary>
public class UserDevice
{
    public int DeviceId { get; set; }
    public int UserId { get; set; }
    public string DeviceFingerprint { get; set; } = "";
    public string? DeviceLabel { get; set; }
    public string RefreshTokenHash { get; set; } = "";
    public DateTime RefreshExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public User? User { get; set; }
}
