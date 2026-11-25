using Tao.Domain;
using static Tao.Infrastructure.Connectors.ConnectorHttp;

namespace Tao.Infrastructure.Connectors;

/// <summary>
/// Names learned from one fetch (organizers, attendees, senders) so that a card whose counterpart is
/// only an address shows the name when the same address appeared with one. Nothing is stored and no
/// extra permission is used: it lives for the duration of a single request.
/// </summary>
internal sealed class AddressDirectory
{
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public void Learn(string? address, string? name)
    {
        if (!IsAddress(address) || string.IsNullOrWhiteSpace(name) || IsAddress(name)) return;
        lock (_names) _names.TryAdd(address!.Trim(), Clip(name, 80));
    }

    public WorkItem Resolve(WorkItem item)
    {
        if (item.Counterpart is not { } who || !IsAddress(who)) return item;
        string? name;
        lock (_names) _names.TryGetValue(who.Trim(), out name);
        return name is null ? item : item with { Counterpart = name };
    }

    private static bool IsAddress(string? text) =>
        text is { Length: > 2 } && text.Contains('@') && !text.Contains(' ');
}
