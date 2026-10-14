namespace Encoding.Contracts.Auth;

public enum UserRole { Admin, Viewer }

public sealed record CurrentUserResponse(Guid Id, string Username, UserRole Role);

public sealed record AuthStatusResponse(bool SetupRequired, CurrentUserResponse? User);

public sealed record UserResponse(Guid Id, string Username, UserRole Role, bool IsActive, DateTimeOffset CreatedAt);

public sealed record SetupRequest(string Username, string Password, string? SetupToken);

public sealed record LoginRequest(string Username, string Password);

public sealed record CreateUserRequest(string Username, string Password, UserRole Role);

public sealed record UpdateUserRequest(UserRole Role, bool IsActive);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string NewPassword);

public sealed record EnrollmentWindowResponse(bool IsOpen, DateTimeOffset? OpenUntil);

public sealed record OpenEnrollmentWindowRequest(int Minutes);
