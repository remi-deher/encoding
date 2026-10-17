namespace Encoding.Contracts.Auth;

public enum UserRole { Admin, Viewer }

public sealed record CurrentUserResponse(Guid Id, string Username, UserRole Role);

public sealed record AuthStatusResponse(bool SetupRequired, CurrentUserResponse? User);

public sealed record UserResponse(Guid Id, string Username, UserRole Role, bool IsActive, DateTimeOffset CreatedAt);

public sealed record SetupRequest(
    string? Username = null,
    string? Password = null,
    string? SetupToken = null,
    string? NodeDisplayName = null,
    string? Mode = null,
    string? ClusterName = null);

public sealed record LoginRequest(string Username, string Password);

public sealed record CreateUserRequest(string Username, string Password, UserRole Role);

public sealed record UpdateUserRequest(UserRole Role, bool IsActive);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record EnrollmentWindowResponse(bool IsOpen, DateTimeOffset? OpenUntil);

public sealed record OpenEnrollmentWindowRequest(int Minutes);

/// <summary>Mode « accepter la première liaison » (0040) : actif jusqu'à la première liaison acceptée ou jusqu'à son expiration.</summary>
public sealed record FirstLinkResponse(bool IsActive, DateTimeOffset? ExpiresAt);
