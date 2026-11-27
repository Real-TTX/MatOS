using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;

namespace MatOS.Core.Integrations;

/// <summary>One DNS record to keep on the home IP: the full name, the part before the zone, the zone and the type (A, or AAAA for a fixed IPv6 address).</summary>
public sealed record DnsRecord(string Hostname, string RecordName, string Zone, string Type = "A");

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
    /// Makes sure there is a source of kind <c>Static</c> with this one fixed address (IPv4 or IPv6; the other family is cleared) and returns the id of its
    /// entry: the records of a domain whose internet connection has a fixed IP point to it.
    /// </summary>
    public async Task<string> EnsureStaticSourceAsync(string name, string ip, CancellationToken ct = default)
    {
        var v6 = System.Net.IPAddress.Parse(ip).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var body = new JsonObject
        {
            ["name"] = name,
            ["kind"] = "Static",
            ["intervalSeconds"] = 300,
            ["static"] = new JsonObject { ["ip"] = v6 ? string.Empty : ip, ["ipv6"] = v6 ? ip : string.Empty },
            ["managedBy"] = Owner.Name,
        };
        var source = await SendAsync(HttpMethod.Post, "sources", body, null, ct) as JsonObject
            ?? throw new InvalidOperationException("Matddns did not return the source.");
        return (source["entries"] as JsonArray)?.OfType<JsonObject>().Select(e => e["id"]?.GetValue<string>()).FirstOrDefault(id => id is not null)
            ?? throw new InvalidOperationException("Matddns created the source without an entry.");
    }

    /// <summary>
    /// Makes sure there is a source of kind <c>Dns</c> that looks up the address of <paramref name="hostname"/> (the tunnel name of MatOS Services) and returns
    /// the id of its entry: the records of an own domain that goes through the tunnel follow it.
    /// </summary>
    public async Task<string> EnsureDnsSourceAsync(string name, string hostname, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["kind"] = "Dns",
            ["intervalSeconds"] = 300,
            ["dns"] = new JsonObject { ["hostname"] = hostname },
            ["managedBy"] = Owner.Name,
        };
        var source = await SendAsync(HttpMethod.Post, "sources", body, null, ct) as JsonObject
            ?? throw new InvalidOperationException("Matddns did not return the source.");
        return (source["entries"] as JsonArray)?.OfType<JsonObject>().Select(e => e["id"]?.GetValue<string>()).FirstOrDefault(id => id is not null)
            ?? throw new InvalidOperationException("Matddns created the source without an entry.");
    }

    /// <summary>
    /// Removes the source of this name (or the one MatOS made under <paramref name="formerName"/>) when MatOS made it and no rule uses one of its entries any
    /// more: the rules of MatOS point to the other source by then, and a rule someone made by hand keeps its source. Returns whether it went.
    /// </summary>
    public async Task<bool> DeleteSourceIfUnusedAsync(string name, string? formerName = null, CancellationToken ct = default)
    {
        if (FindByName(await GetAsync("sources", ct) as JsonArray, name, formerName) is not { } source
            || !Owner.IsOurs(source["managedBy"]?.GetValue<string>())
            || source["id"]?.GetValue<string>() is not { } id)
        {
            return false;
        }

        var entries = (source["entries"] as JsonArray)?.OfType<JsonObject>().Select(e => e["id"]?.GetValue<string>()).OfType<string>().ToHashSet() ?? [];
        var used = (await GetAsync("rules", ct) as JsonArray ?? []).OfType<JsonObject>()
            .Any(r => (r["sourceEntryIdsInOrder"] as JsonArray)?.Any(e => e?.GetValue<string>() is { } entry && entries.Contains(entry)) == true);
        if (used)
        {
            return false;
        }

        await SendAsync(HttpMethod.Delete, $"sources/{id}", null, null, ct);
        return true;
    }

    /// <summary>
    /// The public IP the source of this name saw last (IPv4, else IPv6), to prefill the fixed address in the forms; null when there is none or Matddns does
    /// not answer within a few seconds.
    /// </summary>
    public async Task<string?> CurrentIpAsync(string name, string? formerName = null, CancellationToken ct = default)
    {
        try
        {
            var sources = await SendAsync(HttpMethod.Get, "sources", null, TimeSpan.FromSeconds(3), ct) as JsonArray;
            var entry = (FindByName(sources, name, formerName)?["entries"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            return entry?["currentIp"]?.GetValue<string>() is { Length: > 0 } v4 ? v4 : entry?["currentIpv6"]?.GetValue<string>() is { Length: > 0 } v6 ? v6 : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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
                ["type"] = record.Type,
                ["recordName"] = record.RecordName,
                ["domain"] = record.Zone,
            });
        }

        return await UpsertGroupAsync(name, formerName, provider.MatddnsKind, provider.MatddnsSettings, settings, entries, records.Select(r => (r.Hostname, r.Type)), ct);
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

        return UpsertGroupAsync(name, null, "DynDns", "dynDns", settings, entries, hostnames.Select(h => (h, "A")), ct);
    }

    private async Task<(string GroupId, Dictionary<string, string> EntryIds)> UpsertGroupAsync(
        string name,
        string? formerName,
        string kind,
        string settingsKey,
        JsonObject settings,
        JsonArray entries,
        IEnumerable<(string Hostname, string Type)> records,
        CancellationToken ct)
    {
        // Matddns refuses (409) to drop a record that a rule still uses, and never removes a rule on its own: the rules of the
        // records that leave the list go first. Matddns matches a record by its name and its type: a record that changes its type (A to AAAA, a fixed IPv6
        // address) is a new one, and the old one leaves.
        var existing = await FindGroupAsync(name, formerName, ct);
        if (existing is not null)
        {
            var staying = records.Select(r => RecordKey(r.Hostname, r.Type)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var leaving = existing.Records.Where(e => !staying.Contains(RecordKey(e.Hostname, e.Type))).Select(e => e.Id).ToHashSet();
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

    /// <summary>
    /// Ties a record to the IP source: the record follows the IP, checked every five minutes and when the IP changes. A CNAME record gets
    /// <paramref name="cnameTarget"/> (Matddns writes the name, the source only says that there is a way).
    /// </summary>
    public Task UpsertRuleAsync(string groupId, string entryId, string sourceEntryId, CancellationToken ct = default)
        => UpsertRuleAsync(groupId, entryId, sourceEntryId, null, ct);

    /// <inheritdoc cref="UpsertRuleAsync(string, string, string, CancellationToken)"/>
    public Task UpsertRuleAsync(string groupId, string entryId, string sourceEntryId, string? cnameTarget, CancellationToken ct = default)
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
        if (cnameTarget is not null)
        {
            body["cnameTargets"] = new JsonArray(cnameTarget);
        }

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

    /// <summary>A domain group by name: its id and its records (host name, type, id).</summary>
    private sealed record GroupInfo(string Id, List<(string Hostname, string Type, string Id)> Records);

    private static string RecordKey(string hostname, string type) => $"{hostname}|{type}";

    private async Task<GroupInfo?> FindGroupAsync(string name, string? formerName, CancellationToken ct)
    {
        if (FindByName(await GetAsync("domains", ct) as JsonArray, name, formerName) is not { } group || group["id"]?.GetValue<string>() is not { } id)
        {
            return null;
        }

        var records = new List<(string, string, string)>();
        foreach (var entry in (group["entries"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (entry["hostname"]?.GetValue<string>() is { } host && entry["id"]?.GetValue<string>() is { } entryId)
            {
                records.Add((host, entry["type"]?.GetValue<string>() ?? "A", entryId));
            }
        }

        return new GroupInfo(id, records);
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
