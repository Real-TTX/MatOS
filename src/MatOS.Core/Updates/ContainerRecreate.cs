using System.Text.Json.Nodes;

namespace MatOS.Core.Updates;

/// <summary>One more network a re-created container joins after its creation (the Docker API takes one network at creation).</summary>
public sealed record ExtraNetwork(string Network, JsonObject Endpoint);

/// <summary>The request that creates a container again: the body of <c>POST /containers/create</c> and the networks it joins afterwards.</summary>
public sealed record RecreatePlan(JsonObject Create, IReadOnlyList<ExtraNetwork> Networks);

/// <summary>
/// Turns what Docker says about a running container (<c>GET /containers/{id}/json</c>) into the request that creates it again from a newer image: the same
/// name, ports, volumes, networks, restart policy, variables and labels, with what came from the old image left out so the new image's own values count
/// (its variables, command, health check, labels). Pure, so it is tested without Docker.
/// </summary>
public static class ContainerRecreate
{
    /// <summary>The settings of <c>Config</c> that an image sets: left out when they are the old image's, so the new image's apply.</summary>
    private static readonly string[] ImageDefaults = ["Cmd", "Entrypoint", "Healthcheck", "WorkingDir", "User", "Volumes", "ExposedPorts", "Shell", "StopSignal", "OnBuild"];

    /// <summary>The fields of a network endpoint that Docker fills in at run time and refuses or ignores on a create.</summary>
    private static readonly string[] EndpointRuntime =
        ["NetworkID", "EndpointID", "Gateway", "IPAddress", "IPPrefixLen", "IPv6Gateway", "GlobalIPv6Address", "GlobalIPv6PrefixLen", "MacAddress", "DNSNames"];

    /// <param name="container">The container as <c>GET /containers/{id}/json</c> gives it.</param>
    /// <param name="oldImage">Its image as <c>GET /images/{id}/json</c> gives it (the defaults to leave out); null leaves everything in.</param>
    /// <param name="image">The image to create it from (the tag it ran under, now pointing to the newer image).</param>
    public static RecreatePlan Build(JsonObject container, JsonObject? oldImage, string image)
    {
        var id = container["Id"]?.GetValue<string>() ?? string.Empty;
        var config = (container["Config"]?.DeepClone() as JsonObject) ?? new JsonObject();
        var imageConfig = oldImage?["Config"] as JsonObject;

        config["Image"] = image;

        // Docker names a container's host after its id unless one was given: the new container gets its own (MatOS finds itself by it).
        if (config["Hostname"]?.GetValue<string>() is { } hostname && (hostname.Length == 0 || IsIdPrefix(id, hostname)))
        {
            config.Remove("Hostname");
        }

        config.Remove("MacAddress");

        if (imageConfig is not null)
        {
            config["Env"] = WithoutImageValues(config["Env"] as JsonArray, imageConfig["Env"] as JsonArray);
            config["Labels"] = WithoutImageLabels(config["Labels"] as JsonObject, imageConfig["Labels"] as JsonObject);
            foreach (var key in ImageDefaults)
            {
                if (config[key] is { } value && imageConfig[key] is { } fromImage && JsonNode.DeepEquals(value, fromImage))
                {
                    config.Remove(key);
                }
            }
        }

        var host = (container["HostConfig"]?.DeepClone() as JsonObject) ?? new JsonObject();
        KeepAnonymousVolumes(host, container["Mounts"] as JsonArray);

        var (create, extra) = Networks(container, host, id);
        config["HostConfig"] = host;
        if (create is not null)
        {
            config["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = create };
        }

        return new RecreatePlan(config, extra);
    }

    /// <summary>Whether a name is the container's id or its short form (Docker's default host name and alias), not a name somebody chose.</summary>
    private static bool IsIdPrefix(string id, string name) => name.Length >= 12 && id.StartsWith(name, StringComparison.Ordinal);

    /// <summary>The variables that are not the old image's own (<c>ASPNETCORE_URLS=...</c> of the image stays out; <c>MatOS__X=...</c> of the compose file stays in).</summary>
    private static JsonArray? WithoutImageValues(JsonArray? values, JsonArray? fromImage)
    {
        if (values is null)
        {
            return null;
        }

        var image = fromImage?.Select(v => v?.GetValue<string>()).Where(v => v is not null).ToHashSet(StringComparer.Ordinal) ?? [];
        return new JsonArray(values.Select(v => v?.GetValue<string>()).Where(v => v is not null && !image.Contains(v)).Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
    }

    private static JsonObject? WithoutImageLabels(JsonObject? labels, JsonObject? fromImage)
    {
        if (labels is null)
        {
            return null;
        }

        var kept = new JsonObject();
        foreach (var (key, value) in labels)
        {
            if (fromImage?[key] is { } image && JsonNode.DeepEquals(image, value))
            {
                continue;
            }

            kept[key] = value?.DeepClone();
        }

        return kept;
    }

    /// <summary>
    /// A volume the image declares (<c>VOLUME /data</c>) and that nobody named gets a fresh, empty volume on a new container: it would lose every byte. The
    /// mounts of the old container that neither <c>Binds</c> nor <c>Mounts</c> name are added as mounts of the same volume.
    /// </summary>
    private static void KeepAnonymousVolumes(JsonObject host, JsonArray? mounts)
    {
        if (mounts is null)
        {
            return;
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bind in (host["Binds"] as JsonArray ?? []).Select(b => b?.GetValue<string>()).Where(b => b is not null))
        {
            var parts = bind!.Split(':');
            if (parts.Length >= 2)
            {
                covered.Add(parts[1]);
            }
        }

        foreach (var mount in (host["Mounts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (mount["Target"]?.GetValue<string>() is { } target)
            {
                covered.Add(target);
            }
        }

        foreach (var mount in mounts.OfType<JsonObject>())
        {
            if (mount["Type"]?.GetValue<string>() != "volume" || mount["Name"]?.GetValue<string>() is not { Length: > 0 } name
                || mount["Destination"]?.GetValue<string>() is not { Length: > 0 } destination || covered.Contains(destination))
            {
                continue;
            }

            if (host["Mounts"] is not JsonArray list)
            {
                list = new JsonArray();
                host["Mounts"] = list;
            }

            list.Add(new JsonObject { ["Type"] = "volume", ["Source"] = name, ["Target"] = destination, ["ReadOnly"] = !(mount["RW"]?.GetValue<bool>() ?? true) });
            covered.Add(destination);
        }
    }

    /// <summary>The network of <c>HostConfig.NetworkMode</c> at the creation, the others afterwards; the aliases without the old id.</summary>
    private static (JsonObject? Create, List<ExtraNetwork> Extra) Networks(JsonObject container, JsonObject host, string id)
    {
        var extra = new List<ExtraNetwork>();
        if (container["NetworkSettings"]?["Networks"] is not JsonObject networks || networks.Count == 0)
        {
            return (null, extra);
        }

        var mode = host["NetworkMode"]?.GetValue<string>();
        if (mode is "host" or "none" || (mode?.StartsWith("container:", StringComparison.Ordinal) ?? false))
        {
            return (null, extra);
        }

        var primary = mode is { Length: > 0 } and not "default" && networks.ContainsKey(mode) ? mode : networks.First().Key;
        host["NetworkMode"] = primary;
        JsonObject? create = null;
        foreach (var (name, value) in networks)
        {
            var endpoint = Endpoint(value as JsonObject, id);
            if (name == primary)
            {
                create = new JsonObject { [name] = endpoint };
            }
            else
            {
                extra.Add(new ExtraNetwork(name, endpoint));
            }
        }

        return (create, extra);
    }

    private static JsonObject Endpoint(JsonObject? endpoint, string id)
    {
        var copy = (endpoint?.DeepClone() as JsonObject) ?? new JsonObject();
        foreach (var key in EndpointRuntime)
        {
            copy.Remove(key);
        }

        if (copy["Aliases"] is JsonArray aliases)
        {
            var kept = aliases.Select(a => a?.GetValue<string>()).Where(a => a is not null && !IsIdPrefix(id, a!));
            copy["Aliases"] = new JsonArray(kept.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        }

        return copy;
    }
}
