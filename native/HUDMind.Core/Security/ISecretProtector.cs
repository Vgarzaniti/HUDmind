namespace HUDMind.Core.Security;

/// <summary>
/// Cross-platform abstraction for protecting locally stored API secrets at rest
/// (backed by Windows CurrentUser DPAPI in <c>HUDMind.Windows</c>).
/// </summary>
public interface ISecretProtector
{
    bool IsSupported { get; }
    string Protect(string plainSecret);
    string Unprotect(string protectedSecret);
}

public sealed class PlaintextSecretProtector : ISecretProtector
{
    public static readonly PlaintextSecretProtector Instance = new();
    public bool IsSupported => false;
    public string Protect(string plainSecret) => plainSecret;
    public string Unprotect(string protectedSecret) => protectedSecret;
}
