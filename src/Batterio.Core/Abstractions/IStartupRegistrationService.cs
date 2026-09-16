namespace Batterio.Core.Abstractions;

public interface IStartupRegistrationService
{
    Task<bool> IsRegisteredAsync(CancellationToken cancellationToken = default);

    Task SetRegisteredAsync(bool registered, CancellationToken cancellationToken = default);
}
