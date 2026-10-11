namespace Batterio.Core.Abstractions;

public interface IStartupRegistrationService
{
    Task<bool> IsRegisteredAsync(CancellationToken cancellationToken = default);

    Task SetRegisteredAsync(bool registered, CancellationToken cancellationToken = default);

    /// <summary>
    /// Points an existing startup entry at the running executable. Returns true when the entry was rewritten;
    /// does nothing when startup is off.
    /// </summary>
    Task<bool> RefreshRegistrationAsync(CancellationToken cancellationToken = default);
}
