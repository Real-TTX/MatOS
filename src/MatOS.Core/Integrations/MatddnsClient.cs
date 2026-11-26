using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;

namespace MatOS.Core.Integrations;

/// <summary>One DNS record to keep on the home IP: the full name, the part before the zone and the zone.</summary>
public sealed record DnsRecord(string Hostname, string RecordName, string Zone);

/// <summary>
/// Client of the Matddns management API (see docs/ENGINES.md). Matddns owns the sources of the IP, the DNS records and the rules;
/// everything MatOS writes there carries <c>managedBy: matos</c> so Matddns shows where it came from.
/// </summary>
public sealed class MatddnsClient : EngineClient
{

    public MatddnsClient(EngineLocator locator, IHttpClientFactory http)
        : base(locator, http)
    {
    }

    protected override string CatalogId => "matddns";

    protected override string KeyVariable => "MATDDNS_API_KEY";

    protected override string DisplayName => "Matddns";

    /// <summary>
    /// Makes sure there is a source "public IP" (the IP this host reaches the internet with) and returns the id of its entry. Without a source of this
    /// name, the one MatOS made under <paramref name="formerName"/> (its name while MatOS was called Mattiverse) is taken over and renamed: its entry,
    /// and with it the rules that use it, stay.
    /// </summary>
    public async Task<string> EnsurePublicIpSourceAsync(string name, string? formerName = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["kind"] = "PublicIp",
            ["intervalSeconds"] = 300,
            ["managedBy"] = Owner.Name,
        };
        if (formerName is not null && FindByName(await GetAsync("sources", ct) as JsonArray, name, formerName) is { } existing
            && existing["id"]?.GetValue<string>() is { } existingId)
        {
            body["id"] = existingId;
        }

        var source = await SendAsync(HttpMethod.Post, "sources", body, null, ct) as JsonObject
            ?? throw new InvalidOperationException("Matddns did not return the source.");
        return (source["entries"] as JsonArray)?.OfType<JsonObject>().Select(e => e["id"]?.GetValue<string>()).FirstOrDefault(id => id is not null)
            ?? throw new InvalidOperationException("Matddns created the source without an entry.");
    }

    /// <summary>
    /// Creates or updates the group of records at the provider with exactly these <paramref name="records"/> (the list is complete: records
    /// that are not in it are removed). Returns the id of the group and the id of each record by host name. Without a group of this name, the one MatOS
    /// made under <paramref name="formerName"/> (its name while MatOS was called Mattiverse) is taken over and renamed, so no second group holds the same records.
    /// </summary>
    public async Task<(string GroupId, Dictionary<string, string> EntryIds)> UpsertGroupAsync(
        string name,
        DnsProvider provider,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyList<DnsRecord> records,
        string? formerName = null,
        CancellationToken ct = default)
    {
        var settings = new JsonObject { ["allowDynamic"] = true }; // create the records that do not exist yet
        foreach (var field in provider.Fields)
        {
            if (credentials.TryGetValue(field.Key, out var value) && !string.IsNullOrEmpty(value))
            {
                settings[field.Key] = value;
            }
        }

        var entries = new JsonArray();
        foreach (var record in records)
        {
            entries.Add(new JsonObject
            {
                ["hostname"] = record.Hostname,
                ["type"] = "A",
                ["recordName"] = record.RecordName,
                ["domain"] = record.Zone,
            });
        }

        return await UpsertGroupAsync(name, formerName, provider.MatddnsKind, provider.MatddnsSettings, settings, entries, records.Select(r => r.Hostname), ct);
    }

    /// <summary>
    /// Creates or updates a group of kind <c>DynDns</c> (an update address with a user and a password, the DynDNS2 protocol) with exactly these A records:
    /// the free address of the address service in direct mode. Returns the id of the group and the id of each record by host name.
    /// </summary>
    public Task<(string GroupId, Dictionary<string, string> EntryIds)> UpsertDynDnsGroupAsync(
        string name,
        string updateUrl,
        string username,
        string password,
        IReadOnlyList<string> hostnames,
        CancellationToken ct = default)
    {
        var settings = new JsonObject { ["updateUrl"] = updateUrl, ["username"] = username, ["password"] = password };
        var entries = new JsonArray();
        foreach (var host in hostnames)
        {
            entries.Add(new JsonObject { ["hostname"] = host, ["type"] = "A" });
        }

        return UpsertGroupAsync(name, null, "DynDns", "dynDns", settings, entries, hostnames, ct);
    }

    private async Task<(string GroupId, Dictionary<string, string> EntryIds)> UpsertGroupAsync(
        string name,
        string? formerName,
        string kind,
        string settingsKey,
        JsonObject settings,
        JsonArray entries,
        IEnumerable<string> hostnames,
        CancellationToken ct)
    {
        // Matddns refuses (409) to drop a record that a rule still uses, and never removes a rule on its own: the rules of the
        // records that leave the list go first.
        var existing = await FindGroupAsync(name, formerName, ct);
        if (existing is not null)
        {
            var staying = hostnames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var leaving = existing.Entries.Where(e => !staying.Contains(e.Key)).Select(e => e.Value).ToHashSet();
            await DeleteRulesAsync(existing.Id, leaving.Contains, ct);
        }

        var body = new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            [settingsKey] = settings,
            ["entries"] = entries,
            ["managedBy"] = Owner.Name,
        };
        if (existing is not null)
        {
            // By its id: a group of the former name gets the new one.
            body["id"] = existing.Id;
        }

        var group = await SendAsync(HttpMethod.Post, "domains", body, null, ct) as JsonObject
            ?? throw new InvalidOperationException("Matddns did not return the domain group.");

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (group["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (entry["hostname"]?.GetValue<string>() is { } host && entry["id"]?.GetValue<string>() is { } id)
            {
                ids[host] = id;
            }
        }

        return (group["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Matddns did not return the id of the group."), ids);
    }

    /// <summary>Ties a record to the IP source: the record follows the IP, checked every five minutes and when the IP changes.</summary>
    public Task UpsertRuleAsync(string groupId, string entryId, string sourceEntryId, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["enabled"] = true,
            ["onChange"] = true,
            ["intervalSeconds"] = 300,
            ["validatePing"] = false,
            ["validateTcp"] = false,
            ["domainGroupId"] = groupId,
            ["domainEntryId"] = entryId,
            ["sourceEntryIdsInOrder"] = new JsonArray(sourceEntryId),
            ["managedBy"] = Owner.Name,
        };
        return SendAsync(HttpMethod.Post, "rules", body, null, ct);
    }

    /// <summary>Evaluates all rules now, so a new record is written right away.</summary>
    public Task RunNowAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "run", new JsonObject(), null, ct);

    /// <summary>
    /// Removes the group of records (or the one MatOS made under <paramref name="formerName"/>). Matddns refuses (409) while rules use the group, so its
    /// rules are removed first. Returns whether the group existed.
    /// </summary>
    public async Task<bool> DeleteGroupAsync(string name, string? formerName = null, CancellationToken ct = default)
    {
        if (await FindGroupAsync(name, formerName, ct) is not { } group)
        {
            return false;
        }

        await DeleteRulesAsync(group.Id, _ => true, ct);
        await SendAsync(HttpMethod.Delete, $"domains/{group.Id}", null, null, ct);
        return true;
    }

    // ------------------------------------------------------------------ lookups

    /// <summary>A domain group by name: its id and the id of each record by host name.</summary>
    private sealed record GroupInfo(string Id, Dictionary<string, string> Entries);

    private async Task<GroupInfo?> FindGroupAsync(string name, string? formerName, CancellationToken ct)
    {
        if (FindByName(await GetAsync("domains", ct) as JsonArray, name, formerName) is not { } group || group["id"]?.GetValue<string>() is not { } id)
        {
            return null;
        }

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (group["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (entry["hostname"]?.GetValue<string>() is { } host && entry["id"]?.GetValue<string>() is { } entryId)
            {
                entries[host] = entryId;
            }
        }

        return new GroupInfo(id, entries);
    }

    /// <summary>
    /// The item of <paramref name="items"/> named <paramref name="name"/> (case-insensitive, like Matddns itself); without one, the item named
    /// <paramref name="formerName"/> that carries the mark of MatOS (<see cref="Owner"/>): what MatOS made while it was called Mattiverse.
    /// </summary>
    public static JsonObject? FindByName(JsonArray? items, string name, string? formerName)
    {
        var all = items?.OfType<JsonObject>().ToList() ?? [];
        return all.FirstOrDefault(i => string.Equals(i["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
            ?? (formerName is null
                ? null
                : all.FirstOrDefault(i => string.Equals(i["name"]?.GetValue<string>(), formerName, StringComparison.OrdinalIgnoreCase)
                    && Owner.IsOurs(i["managedBy"]?.GetValue<string>())));
    }

    /// <summary>Deletes the rules of a group whose record matches <paramref name="entryIs"/>.</summary>
    private async Task DeleteRulesAsync(string groupId, Func<string, bool> entryIs, CancellationToken ct)
    {
        foreach (var rule in (await GetAsync("rules", ct) as JsonArray ?? []).OfType<JsonObject>())
        {
            if (rule["domainGroupId"]?.GetValue<string>() == groupId &&
                rule["domainEntryId"]?.GetValue<string>() is { } entryId && entryIs(entryId) &&
                rule["id"]?.GetValue<string>() is { } id)
            {
                await SendAsync(HttpMethod.Delete, $"rules/{id}", null, null, ct);
            }
        }
    }
}
