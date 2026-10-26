using System.ComponentModel;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Infrastructure;

public static class AccountConnectionErrors
{
    public static string Describe(Exception exception) => exception switch
    {
        InheritedWorkloadIdentityException workload => workload.Message,
        MembershipRequiredException membership => membership.Message,
        FileNotFoundException => "Codex could not be found. Open Settings → Connection and select the official codex.exe, then try again.",
        UnauthorizedAccessException => "Windows could not access this account's local profile folder. Check its permissions and try again.",
        Win32Exception native => $"Windows could not start Codex (error {native.NativeErrorCode}). Open Settings → Connection and test the connection.",
        OperationCanceledException or TimeoutException => "Codex took too long to respond. Please try signing in again.",
        AppServerRpcException { IsAuthenticationFailure: true } => "Codex could not verify your sign-in. Sign in again to connect this account.",
        AppServerRpcException rpc => $"Codex rejected the connection request (RPC {rpc.Code}). Try again, or use the other sign-in method.",
        _ => exception.Message switch
        {
            "login_timeout" => "Sign-in timed out. Please try again.",
            "login_failed" => "Sign-in was not completed.",
            "app_server_terminated" => "Codex App Server stopped during sign-in. Please try again.",
            "identity_verification_failed" => "The account identity could not be verified.",
            "identity_email_unavailable" => "Codex did not report an email address, so this profile cannot be isolated safely.",
            "identity_mismatch" => "The signed-in account does not match this profile. Sign in with the original account.",
            "account_already_connected" => "That account is already connected to this monitor.",
            "active_profile_reauthentication_blocked" or "managed_task_running" or "profile_task_running" => "Let the current managed task finish, then sign in again.",
            "Codex App Server started with an unexpected profile directory." => "Codex reported a different account folder. Sign-in was stopped to keep your accounts separate. Restart the monitor and retry.",
            "The configured Codex executable is not validly signed by OpenAI." or
            "The Codex account connection helper is not validly signed by OpenAI." => "The selected Codex file could not be verified as an official OpenAI build. Repair the official Codex installation, then retry the connection check.",
            _ => "Could not connect this account. Try again; if it continues, use Test connection in Settings → Connection."
        }
    };
}
