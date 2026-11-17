namespace RemoteDeskHub.Core.Security;

/// <summary>
/// Raised when an authenticated request repeats a nonce or carries a timestamp outside the
/// permitted clock skew. The HTTP host maps this to 409 Conflict.
/// </summary>
public sealed class ReplayDetectedException(string message) : Exception(message);
