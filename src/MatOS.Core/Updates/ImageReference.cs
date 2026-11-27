namespace MatOS.Core.Updates;

/// <summary>
/// An image as Docker names it (<c>ghcr.io/real-ttx/matos:latest</c>, <c>nginx</c>, <c>localhost:5000/matos:nightly</c>, <c>repo@sha256:...</c>), taken apart the
/// way the registry needs it: where it lives, the repository and the tag.
/// </summary>
/// <param name="Name">The image without tag and digest, as it was written (<c>ghcr.io/real-ttx/matos</c>, <c>nginx</c>): what a new tag is put on.</param>
/// <param name="Registry">The registry (<c>ghcr.io</c>, <c>docker.io</c> for an image without one, <c>localhost:5000</c>).</param>
/// <param name="Repository">The repository at the registry (<c>real-ttx/matos</c>; <c>library/nginx</c> for an official image of Docker Hub).</param>
/// <param name="Tag">The tag (<c>latest</c> when none is written); null when the reference names a digest only.</param>
/// <param name="Digest">The digest written after <c>@</c>, or null.</param>
public sealed record ImageReference(string Name, string Registry, string Repository, string? Tag, string? Digest)
{
    public const string DockerHub = "docker.io";

    /// <summary>Takes a reference apart; null when it is empty or no image name at all.</summary>
    public static ImageReference? Parse(string? reference)
    {
        var text = reference?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        string? digest = null;
        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            digest = text[(at + 1)..];
            text = text[..at];
        }

        // The tag is after the last colon that comes after the last slash (a colon before it is the port of the registry).
        string? tag = null;
        var slash = text.LastIndexOf('/');
        var colon = text.LastIndexOf(':');
        if (colon > slash)
        {
            tag = text[(colon + 1)..];
            text = text[..colon];
        }

        if (text.Length == 0 || text.EndsWith('/') || text.StartsWith('/'))
        {
            return null;
        }

        var first = text.Split('/')[0];
        var hasRegistry = text.Contains('/', StringComparison.Ordinal)
            && (first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost");
        var registry = hasRegistry ? first.ToLowerInvariant() : DockerHub;
        var repository = hasRegistry ? text[(first.Length + 1)..] : text;
        if (registry is DockerHub or "index.docker.io" or "registry-1.docker.io")
        {
            registry = DockerHub;
            if (!repository.Contains('/', StringComparison.Ordinal))
            {
                repository = "library/" + repository;
            }
        }

        if (tag is null && digest is null)
        {
            tag = "latest";
        }

        return new ImageReference(text, registry, repository.ToLowerInvariant(), tag, digest);
    }

    /// <summary>The host the registry's HTTP API answers on (Docker Hub's is not <c>docker.io</c>).</summary>
    public string ApiHost => Registry == DockerHub ? "registry-1.docker.io" : Registry;

    /// <summary>A registry on this machine (<c>localhost:5000</c>): spoken to over plain HTTP, as Docker does.</summary>
    public bool IsLoopback
    {
        get
        {
            var host = Registry.Split(':')[0];
            return host is "localhost" or "127.0.0.1" or "[::1]";
        }
    }

    /// <summary>The same image under another tag (<c>ghcr.io/real-ttx/matos:previous</c>).</summary>
    public string WithTag(string tag) => $"{Name}:{tag}";

    /// <summary>Whether two references name the same repository at the same registry (the tag and the digest do not matter).</summary>
    public bool SameRepository(ImageReference other)
        => string.Equals(Registry, other.Registry, StringComparison.OrdinalIgnoreCase) && string.Equals(Repository, other.Repository, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The digest under which this image was pulled from its repository, out of an image's <c>RepoDigests</c> (<c>ghcr.io/real-ttx/matos@sha256:...</c>; one
    /// image may have several, the same build pushed under two names); null when the image did not come from there (built on this machine).
    /// </summary>
    public string? DigestIn(IEnumerable<string> repoDigests)
    {
        foreach (var entry in repoDigests)
        {
            if (Parse(entry) is { Digest: { } digest } candidate && SameRepository(candidate))
            {
                return digest;
            }
        }

        return null;
    }

    public override string ToString() => Tag is null ? $"{Name}@{Digest}" : Digest is null ? $"{Name}:{Tag}" : $"{Name}:{Tag}@{Digest}";
}
