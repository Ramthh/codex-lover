namespace CodexAccountMonitor.ViewModels;

public interface IUserInteractionService
{
    Task<bool> EnrollAccountAsync(int slot);
    Task<bool> ReauthenticateAsync(Guid profileId);
    Task<bool> ConfirmRemoveAsync(string alias);
    Task<bool> ConfirmResetCreditAsync(string alias);
    Task<bool> SwitchDesktopAccountAsync(Guid profileId) => Task.FromResult(false);
    void ShowSettings();
    void ShowError(string title, string message);
}
