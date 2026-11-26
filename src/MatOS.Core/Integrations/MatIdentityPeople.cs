using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatOS.Core.Catalog;
using MatOS.Core.Engines;

namespace MatOS.Core.Integrations;

/// <summary>What a grant gives in one app: the role (null: the default), permissions on top of it, and quotas (null = no limit).</summary>
public sealed record IdentityGrant(string App, string? Role, IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, long?> Quotas);

/// <summary>What a person ends up with in one app, and why (<c>person</c>, <c>group:&lt;name&gt;</c>, <c>everyone</c>, <c>administrator</c>).</summary>
public sealed record IdentityAccess(string App, string? Role, IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, long?> Quotas, IReadOnlyList<string> Via);

/// <summary>A person of MatIdentity as its SCIM endpoint answers them.</summary>
public sealed record IdentityPerson(string Subject, string UserName, string DisplayName, string? Email, bool Active, bool Administrator)
{
    /// <summary>The ids and names of the person's groups.</summary>
    public IReadOnlyList<(string Id, string Name)> Groups { get; init; } = [];

    /// <summary>What is given to the person directly.</summary>
    public IReadOnlyList<IdentityGrant> Grants { get; init; } = [];

    /// <summary>What the person ends up with in each app they may use.</summary>
    public IReadOnlyList<IdentityAccess> Access { get; init; } = [];
}

/// <summary>A group of MatIdentity: its members (subjects) and what it is given.</summary>
public sealed record IdentityGroup(string Id, string Name, IReadOnlyList<string> Members, IReadOnlyList<IdentityGrant> Grants);

/// <summary>A role, permission or quota of an app as MatIdentity keeps it (what the forms are drawn from).</summary>
public sealed record IdentityRole(string Key, string Label);

public sealed record IdentityPermission(string Key, string Label, string? Hint, IReadOnlyList<string> Roles);

public sealed record IdentityQuota(string Key, string Label, string Unit, string? Period, long? Min, long? Max, string? Hint);

/// <summary>An app (client) of MatIdentity with what decides who may use it.</summary>
public sealed record IdentityApp(string ClientId, string Name, bool AllowSignIn, string RoleMode, bool OpenToEveryone,
    IReadOnlyList<IdentityRole> Roles, IReadOnlyList<IdentityPermission> Permissions, IReadOnlyList<IdentityQuota> Quotas)
{
    /// <summary>MatIdentity gives the roles (and so the permissions and quotas); else the app does.</summary>
    public bool RolesByIdentity => RoleMode != "app";
}

/// <summary>What a person or group is to be (the form of the People area).</summary>
public sealed record IdentityPersonInput(string UserName, string DisplayName, string? Email, bool Active, bool Administrator, string? Password);

/// <summary>
/// The people of MatIdentity for the People area (MatDesign docs/PLATFORM.md "SCIM at MatIdentity"): SCIM 2.0 at <c>/scim/v2</c> and the actions under
/// <c>/api/v1/people</c>, with MatIdentity's key (MatOS holds it as the broker). Every write names the person who acted (<c>X-Mat-Actor</c>). Nothing here knows
/// an app: the forms come from <see cref="AppsAsync"/>, what MatIdentity keeps of each app's roles, permissions and quotas.
/// </summary>
public sealed class MatIdentityPeople
{
    public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string UserAccess = "urn:mat:scim:schemas:extension:access:1.0:User";
    public const string GroupAccess = "urn:mat:scim:schemas:extension:access:1.0:Group";
    public const string ActorHeader = "X-Mat-Actor";

    /// <summary>
    /// The HTTP client of these calls: it follows no redirect (an older MatIdentity without SCIM answers <c>/scim/v2</c> with its sign-in page, which must not be taken
    /// for an answer).
    /// </summary>
    public const string HttpClientName = "matidentity-people";

    /// <summary>What a person reads when MatIdentity answers, but not as SCIM (an image from before SCIM).</summary>
    public const string NoScim = "MatIdentity does not speak SCIM yet (an older version): update MatIdentity.";

    private readonly EngineLocator _locator;
    private readonly IHttpClientFactory _http;

    public MatIdentityPeople(EngineLocator locator, IHttpClientFactory http)
    {
        _locator = locator;
        _http = http;
    }

    /// <summary>MatIdentity is installed and MatOS has its key.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) => await EndpointAsync(ct) is not null;

    // ---- people ----------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<IdentityPerson>> PeopleAsync(CancellationToken ct = default)
        => [.. (await ListAsync("Users", ct)).Select(Person).OfType<IdentityPerson>()];

    public async Task<IdentityPerson?> PersonAsync(string subject, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Get, "/scim/v2/Users/" + Uri.EscapeDataString(subject), null, null, ct, notFoundIsNull: true) is JsonObject user ? Person(user) : null;

    /// <summary>Creates a person; without a password and with an e-mail MatIdentity sends an invitation.</summary>
    public async Task<IdentityPerson> CreatePersonAsync(IdentityPersonInput input, string? actor, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(UserSchema, UserAccess),
            ["userName"] = input.UserName,
            ["displayName"] = input.DisplayName,
            ["active"] = input.Active,
            [UserAccess] = new JsonObject { ["administrator"] = input.Administrator },
        };
        if (!string.IsNullOrWhiteSpace(input.Email))
        {
            body["emails"] = new JsonArray(new JsonObject { ["value"] = input.Email.Trim(), ["primary"] = true });
        }

        if (!string.IsNullOrEmpty(input.Password))
        {
            body["password"] = input.Password;
        }

        var answer = await SendAsync(HttpMethod.Post, "/scim/v2/Users", body, actor, ct);
        return Person((JsonObject)answer!) ?? throw new InvalidOperationException("MatIdentity answered no person.");
    }

    /// <summary>Changes the details of a person (name, e-mail, active, administrator, and a new password when one is given).</summary>
    public async Task<IdentityPerson> UpdatePersonAsync(string subject, IdentityPersonInput input, string? actor, CancellationToken ct = default)
    {
        var operations = new JsonArray(
            Op("replace", "userName", input.UserName),
            Op("replace", "displayName", input.DisplayName),
            Op("replace", "active", input.Active),
            Op("replace", UserAccess + ":administrator", input.Administrator),
            string.IsNullOrWhiteSpace(input.Email) ? Op("remove", "emails", null) : Op("replace", "emails", new JsonArray(new JsonObject { ["value"] = input.Email.Trim(), ["primary"] = true })));
        if (!string.IsNullOrEmpty(input.Password))
        {
            operations.Add(Op("replace", "password", input.Password));
        }

        return await PatchPersonAsync(subject, operations, actor, ct);
    }

    /// <summary>Sets what is given to the person directly, in every app (the whole list).</summary>
    public Task<IdentityPerson> SetPersonGrantsAsync(string subject, IReadOnlyList<IdentityGrant> grants, string? actor, CancellationToken ct = default)
        => PatchPersonAsync(subject, new JsonArray(Op("replace", UserAccess + ":grants", Grants(grants))), actor, ct);

    public async Task DeletePersonAsync(string subject, string? actor, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Delete, "/scim/v2/Users/" + Uri.EscapeDataString(subject), null, actor, ct);

    /// <summary><c>invite</c>, <c>resetPassword</c>, <c>signOut</c>, <c>unlock</c> or <c>resetSecondStep</c>: whether it ran, and the message for the person.</summary>
    public async Task<(bool Ok, string Message)> RunActionAsync(string subject, string action, string? actor, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Post, $"/api/v1/people/{Uri.EscapeDataString(subject)}/actions/{Uri.EscapeDataString(action)}", null, actor, ct, conflictIsAnswer: true);
        return (answer?["ok"]?.GetValue<bool>() == true, answer?["message"]?.GetValue<string>() ?? string.Empty);
    }

    // ---- groups ----------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<IdentityGroup>> GroupsAsync(CancellationToken ct = default)
        => [.. (await ListAsync("Groups", ct)).Select(Group).OfType<IdentityGroup>()];

    public async Task<IdentityGroup?> GroupAsync(string id, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Get, "/scim/v2/Groups/" + Uri.EscapeDataString(id), null, null, ct, notFoundIsNull: true) is JsonObject group ? Group(group) : null;

    public async Task<IdentityGroup> SaveGroupAsync(string? id, string name, IReadOnlyList<string> members, string? actor, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(GroupSchema),
            ["displayName"] = name,
            ["members"] = new JsonArray([.. members.Select(m => (JsonNode)new JsonObject { ["value"] = m })]),
        };
        var answer = id is null
            ? await SendAsync(HttpMethod.Post, "/scim/v2/Groups", body, actor, ct)
            : await SendAsync(HttpMethod.Patch, "/scim/v2/Groups/" + Uri.EscapeDataString(id),
                new JsonObject { ["schemas"] = new JsonArray(PatchSchema), ["Operations"] = new JsonArray(Op("replace", "displayName", name), Op("replace", "members", body["members"]!.DeepClone())) }, actor, ct);
        return Group((JsonObject)answer!) ?? throw new InvalidOperationException("MatIdentity answered no group.");
    }

    /// <summary>Adds the person to these groups and takes them out of the others (one change per group that changes).</summary>
    public async Task SetGroupsOfAsync(string subject, IReadOnlySet<string> groupIds, string? actor, CancellationToken ct = default)
    {
        foreach (var group in await GroupsAsync(ct))
        {
            var isIn = group.Members.Contains(subject);
            var wanted = groupIds.Contains(group.Id);
            if (isIn == wanted)
            {
                continue;
            }

            var operation = wanted
                ? Op("add", "members", new JsonArray(new JsonObject { ["value"] = subject }))
                : Op("remove", $"members[value eq \"{subject}\"]", null);
            await SendAsync(HttpMethod.Patch, "/scim/v2/Groups/" + Uri.EscapeDataString(group.Id),
                new JsonObject { ["schemas"] = new JsonArray(PatchSchema), ["Operations"] = new JsonArray(operation) }, actor, ct);
        }
    }

    public async Task<IdentityGroup> SetGroupGrantsAsync(string id, IReadOnlyList<IdentityGrant> grants, string? actor, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Patch, "/scim/v2/Groups/" + Uri.EscapeDataString(id),
            new JsonObject { ["schemas"] = new JsonArray(PatchSchema), ["Operations"] = new JsonArray(Op("replace", GroupAccess + ":grants", Grants(grants))) }, actor, ct);
        return Group((JsonObject)answer!) ?? throw new InvalidOperationException("MatIdentity answered no group.");
    }

    public async Task DeleteGroupAsync(string id, string? actor, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Delete, "/scim/v2/Groups/" + Uri.EscapeDataString(id), null, actor, ct);

    // ---- apps ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The apps of MatIdentity with their role mode, roles, permissions and quotas.</summary>
    public async Task<IReadOnlyList<IdentityApp>> AppsAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Get, "/api/v1/clients", null, null, ct);
        return answer is JsonArray clients ? [.. clients.OfType<JsonObject>().Select(App).OfType<IdentityApp>()] : [];
    }

    // ---- reading ---------------------------------------------------------------------------------------------------------------------

    private static IdentityPerson? Person(JsonObject user)
    {
        if (Text(user, "id") is not { } id || Text(user, "userName") is not { } userName)
        {
            return null;
        }

        var email = (user["emails"] as JsonArray)?.OfType<JsonObject>().Select(e => Text(e, "value")).OfType<string>().FirstOrDefault();
        var extension = user[UserAccess] as JsonObject;
        return new IdentityPerson(id, userName, Text(user, "displayName") ?? userName, email, user["active"]?.GetValue<bool>() != false, extension?["administrator"]?.GetValue<bool>() == true)
        {
            Groups = [.. ((user["groups"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(g => (Text(g, "value") ?? string.Empty, Text(g, "display") ?? string.Empty))],
            Grants = GrantsOf(extension?["grants"]),
            Access = [.. ((extension?["access"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(a => new IdentityAccess(
                Text(a, "app") ?? string.Empty, Text(a, "role"), Keys(a["permissions"]), QuotasOf(a["quotas"]), Keys(a["via"])))],
        };
    }

    private static IdentityGroup? Group(JsonObject group)
        => Text(group, "id") is { } id
            ? new IdentityGroup(id, Text(group, "displayName") ?? id,
                [.. ((group["members"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(m => Text(m, "value")).OfType<string>()],
                GrantsOf((group[GroupAccess] as JsonObject)?["grants"]))
            : null;

    private static IdentityApp? App(JsonObject client)
        => Text(client, "clientId") is { } id
            ? new IdentityApp(id, Text(client, "displayName") ?? id, client["allowSignIn"]?.GetValue<bool>() != false, Text(client, "roleMode") ?? "matidentity",
                client["openToEveryone"]?.GetValue<bool>() == true,
                [.. ((client["roles"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(r => new IdentityRole(Text(r, "key") ?? string.Empty, Text(r, "label") ?? string.Empty))],
                [.. ((client["permissions"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(p => new IdentityPermission(Text(p, "key") ?? string.Empty, Text(p, "label") ?? string.Empty, Text(p, "hint"), Keys(p["roles"])))],
                [.. ((client["quotas"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(q => new IdentityQuota(Text(q, "key") ?? string.Empty, Text(q, "label") ?? string.Empty,
                    Text(q, "unit") ?? "count", Text(q, "period"), Long(q["min"]), Long(q["max"]), Text(q, "hint")))])
            : null;

    private static IReadOnlyList<IdentityGrant> GrantsOf(JsonNode? node)
        => [.. ((node as JsonArray)?.OfType<JsonObject>() ?? []).Where(g => Text(g, "app") is not null)
            .Select(g => new IdentityGrant(Text(g, "app")!, Text(g, "role"), Keys(g["permissions"]), QuotasOf(g["quotas"])))];

    private static JsonArray Grants(IReadOnlyList<IdentityGrant> grants)
        => new([.. grants.Select(g =>
        {
            var quotas = new JsonObject();
            foreach (var (key, value) in g.Quotas)
            {
                quotas[key] = value is { } number ? JsonValue.Create(number) : null;
            }

            return (JsonNode)new JsonObject
            {
                ["app"] = g.App,
                ["role"] = g.Role,
                ["permissions"] = new JsonArray([.. g.Permissions.Select(p => (JsonNode)p)]),
                ["quotas"] = quotas,
            };
        })]);

    private static IReadOnlyDictionary<string, long?> QuotasOf(JsonNode? node)
    {
        var quotas = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var (key, value) in (node as JsonObject) ?? [])
        {
            quotas[key] = Long(value);
        }

        return quotas;
    }

    private static IReadOnlyList<string> Keys(JsonNode? node)
        => [.. ((node as JsonArray) ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()];

    private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static string? Text(JsonObject? node, string name)
        => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static JsonObject Op(string op, string path, JsonNode? value)
    {
        var operation = new JsonObject { ["op"] = op, ["path"] = path };
        if (op != "remove")
        {
            operation["value"] = value;
        }

        return operation;
    }

    private static JsonObject Op(string op, string path, string? value) => Op(op, path, value is null ? null : JsonValue.Create(value));

    private static JsonObject Op(string op, string path, bool value) => Op(op, path, JsonValue.Create(value));

    private async Task<IdentityPerson> PatchPersonAsync(string subject, JsonArray operations, string? actor, CancellationToken ct)
    {
        var answer = await SendAsync(HttpMethod.Patch, "/scim/v2/Users/" + Uri.EscapeDataString(subject),
            new JsonObject { ["schemas"] = new JsonArray(PatchSchema), ["Operations"] = operations }, actor, ct);
        return Person((JsonObject)answer!) ?? throw new InvalidOperationException("MatIdentity answered no person.");
    }

    /// <summary>Every resource of a SCIM list (all pages).</summary>
    private async Task<IReadOnlyList<JsonObject>> ListAsync(string resource, CancellationToken ct)
    {
        var all = new List<JsonObject>();
        var start = 1;
        while (true)
        {
            var page = await SendAsync(HttpMethod.Get, $"/scim/v2/{resource}?startIndex={start}&count=500", null, null, ct) as JsonObject;
            var items = (page?["Resources"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            all.AddRange(items);
            var total = page?["totalResults"]?.GetValue<int>() ?? all.Count;
            if (items.Count == 0 || all.Count >= total)
            {
                return all;
            }

            start += items.Count;
        }
    }

    private async Task<EngineEndpoint?> EndpointAsync(CancellationToken ct) => await _locator.FindAsync(EngineIds.MatIdentity, ManagedVariables.ApiKey, ct);

    /// <summary>A request to MatIdentity with its key; a refusal becomes an <see cref="InvalidOperationException"/> with MatIdentity's words.</summary>
    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, string? actor, CancellationToken ct, bool notFoundIsNull = false, bool conflictIsAnswer = false)
    {
        var endpoint = await EndpointAsync(ct) ?? throw new InvalidOperationException("MatIdentity is not installed.");
        using var request = new HttpRequestMessage(method, endpoint.BaseUrl + path);
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/scim+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(actor))
        {
            request.Headers.Add(ActorHeader, actor);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/scim+json");
        }

        var client = _http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(30);
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = IsJson(response);
        if ((int)response.StatusCode is >= 300 and < 400 || (!json && !string.IsNullOrWhiteSpace(text)))
        {
            // A redirect (to the sign-in page) or a page instead of JSON: this MatIdentity has no SCIM.
            throw new InvalidOperationException(NoScim);
        }

        if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode && !(conflictIsAnswer && response.StatusCode == HttpStatusCode.Conflict))
        {
            throw new InvalidOperationException(Message(text) ?? $"MatIdentity answered HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(NoScim);
        }
    }

    /// <summary>The answer is JSON (<c>application/json</c>, <c>application/scim+json</c>, any <c>+json</c>).</summary>
    private static bool IsJson(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is { } type
           && (type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    /// <summary>What MatIdentity said: the SCIM <c>detail</c>, the first of <c>errors</c>, or null.</summary>
    private static string? Message(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return node?["detail"]?.GetValue<string>()
                ?? (node?["errors"] as JsonObject)?.Select(e => e.Value?.GetValue<string>()).FirstOrDefault(m => !string.IsNullOrEmpty(m))
                ?? node?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A quota for people: GB for bytes, the period after the unit.</summary>
    public static string UnitOf(IdentityQuota quota)
    {
        var unit = quota.Unit switch { "bytes" => "GB", "count" => "items", _ => quota.Unit };
        return quota.Period switch { "day" => unit + " per day", "month" => unit + " per month", _ => unit };
    }

    /// <summary>A value of a quota as a person reads it (GB for bytes).</summary>
    public static string Show(IdentityQuota quota, long value)
        => quota.Unit == "bytes"
            ? (value / (1024d * 1024 * 1024)).ToString("0.###", CultureInfo.InvariantCulture)
            : value.ToString("N0", CultureInfo.InvariantCulture);
}
