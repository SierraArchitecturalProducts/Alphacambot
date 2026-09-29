using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AlphacamBridge.Core.Security;

/// <summary>
/// Provides secure pipe configuration with Windows ACLs.
/// By default, restricts access to the current user and SYSTEM account.
/// </summary>
public class PipeSecurityProvider
{
    private readonly List<string> _additionalSids;

    /// <summary>
    /// Initializes a new security provider with an optional list of additional allowed SIDs.
    /// </summary>
    /// <param name="additionalSids">Additional Windows SIDs to grant FullControl access.</param>
    public PipeSecurityProvider(IEnumerable<string>? additionalSids = null)
    {
        _additionalSids = additionalSids?.ToList() ?? new List<string>();
    }

    /// <summary>
    /// Creates a PipeSecurity object with appropriate ACLs.
    /// Current user and SYSTEM are granted FullControl by default.
    /// All other identities are denied access.
    /// </summary>
    public PipeSecurity CreatePipeSecurity()
    {
        var pipeSecurity = new PipeSecurity();

        // Get current user's SID
        var currentUser = WindowsIdentity.GetCurrent();
        var currentUserSid = currentUser.User
            ?? throw new InvalidOperationException("Cannot determine current user SID");

        // SYSTEM SID
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        // Grant current user FullControl
        var currentUserRule = new PipeAccessRule(
            currentUserSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow);
        pipeSecurity.AddAccessRule(currentUserRule);

        // Grant SYSTEM FullControl
        var systemRule = new PipeAccessRule(
            systemSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow);
        pipeSecurity.AddAccessRule(systemRule);

        // Grant additional SIDs from configuration
        foreach (var sidString in _additionalSids)
        {
            try
            {
                var sid = new SecurityIdentifier(sidString);
                var rule = new PipeAccessRule(
                    sid,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow);
                pipeSecurity.AddAccessRule(rule);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid SID format: {sidString}", nameof(sidString), ex);
            }
        }

        // No explicit Deny rule needed — named pipes deny access to
        // any identity not listed in the ACL by default.  An explicit
        // Deny-Everyone rule would override the Allow rules above
        // because Windows evaluates Deny ACEs before Allow ACEs.

        return pipeSecurity;
    }

    /// <summary>
    /// Adds an additional SID to the allowed list for future pipe creation.
    /// </summary>
    /// <param name="sid">Windows SID in string format (e.g., "S-1-5-21-...").</param>
    public void AddAllowedSid(string sid)
    {
        ArgumentException.ThrowIfNullOrEmpty(sid);

        // Validate SID format
        try
        {
            _ = new SecurityIdentifier(sid);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid SID format: {sid}", nameof(sid), ex);
        }

        if (!_additionalSids.Contains(sid))
        {
            _additionalSids.Add(sid);
        }
    }

    /// <summary>
    /// Removes a SID from the allowed list.
    /// </summary>
    public void RemoveAllowedSid(string sid)
    {
        _additionalSids.Remove(sid);
    }
}
