namespace Batterio.Core.Abstractions;

public interface IWindowCoordinator
{


    void TogglePopup();

    void ShowPopup();

    void HidePopup();

    void ShowTrayMenu();

    Task ShowSettingsAsync();

    void ReportStartupError(string message);

    Task CloseAsync();
}

