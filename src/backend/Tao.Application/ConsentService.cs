using Tao.Domain;

namespace Tao.Application;

public sealed record GrantConsentCommand(string Source, string Purpose,
    string PolicyVersion, DateTimeOffset ExpiresAt, bool Accepted);

public sealed class ConsentRequiredException : Exception
{
    public ConsentRequiredException() : base("Active, scoped consent is required.") { }
}

public interface IConsentGuard
{
    Task EnsureActiveAsync(string tenantId, string ownerSubject,
        string source, string purpose, CancellationToken cancellationToken);
}

public interface IConsentRepository
{
    Task<ConsentGrant> GrantAsync(ConsentGrant grant, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(string tenantId, string ownerSubject,
        Guid id, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConsentGrant>> ListAsync(string tenantId, string ownerSubject,
        CancellationToken cancellationToken);
}

public interface IConsentService
{
    Task<ConsentGrant> GrantAsync(string tenantId, string ownerSubject,
        GrantConsentCommand command, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(string tenantId, string ownerSubject,
        Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConsentGrant>> ListAsync(string tenantId, string ownerSubject,
        CancellationToken cancellationToken);
}

public sealed class ConsentService(IConsentRepository repository, TimeProvider time) : IConsentService
{
    public Task<ConsentGrant> GrantAsync(string tenantId, string ownerSubject,
        GrantConsentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.Accepted)
            throw new ArgumentException("Explicit acknowledgement is required.", nameof(command));
        var grant = ConsentGrant.Issue(tenantId, ownerSubject, command.Source,
            command.Purpose, command.PolicyVersion, time.GetUtcNow(), command.ExpiresAt);
        return repository.GrantAsync(grant, cancellationToken);
    }

    public Task<bool> RevokeAsync(string tenantId, string ownerSubject,
        Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A nonempty consent ID is required.", nameof(id));
        return repository.RevokeAsync(tenantId, ownerSubject, id, time.GetUtcNow(), cancellationToken);
    }

    public Task<IReadOnlyList<ConsentGrant>> ListAsync(string tenantId, string ownerSubject,
        CancellationToken cancellationToken) =>
        repository.ListAsync(tenantId, ownerSubject, cancellationToken);
}
