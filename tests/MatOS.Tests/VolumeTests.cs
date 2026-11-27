using System.Text.Json.Nodes;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Volumes;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;

namespace MatOS.Tests;

/// <summary>
/// Volumes and the file explorer (docs/ARCHITECTURE.md "Volumes and the file explorer", docs/CATALOG.md "Data folders"): the data folders of a catalog entry,
/// the compose file with a chosen volume, the choice kept as a variable, the paths of the explorer that never leave a volume, and MatDock's answers in MatOS's words.
/// </summary>
public class VolumeTests
{
    private static readonly IOptions<MatOSOptions> Options = Microsoft.Extensions.Options.Options.Create(new MatOSOptions());

    private const string Photos = """
        x-matos:
          id: photos
          name: Photos
          state: beta
          ui: { service: web, port: 8080 }
          folders:
            - volume: library
              name: Photo library
              purpose: Where the photos are.
            - volume: inbox
              name: Inbox
              readOnly: true
        services:
          web:
            image: example/photos
            volumes:
              - data:/data
              - library:/photos
              - type: volume
                source: inbox
                target: /inbox
          worker:
            image: example/worker
            volumes:
              - library:/photos:rw
        volumes:
          data:
          library:
          inbox:
        """;

    private static CatalogApp Entry(string yaml = Photos) => CatalogParser.Parse(yaml, "photos.yml");

    private static CatalogApp BuiltIn(string id)
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream($"catalog/{id}.yml")!;
        using var reader = new StreamReader(stream);
        return CatalogParser.Parse(reader.ReadToEnd(), id + ".yml");
    }

    private static Dictionary<string, object> Parse(string yaml) => new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);

    private static InstalledApp App() => new() { Slug = "photos", Name = "Photos", ProjectName = "matos-photos", UiService = "web", UiPort = 8080, HostPort = 8101 };

    // ---------------------------------------------------------------- the catalog

    [Fact]
    public void A_data_folder_names_a_volume_and_learns_where_it_is_mounted()
    {
        var app = Entry();
        Assert.Equal(2, app.Folders.Count);
        var library = app.Folders[0];
        Assert.Equal(("library", "Photo library", "Where the photos are.", false), (library.Volume, library.Name, library.Purpose, library.ReadOnly));
        Assert.Equal(["web:/photos", "worker:/photos"], library.Mounts);
        Assert.Equal(["web:/inbox"], app.Folders[1].Mounts);
        Assert.True(app.Folders[1].ReadOnly);
        Assert.Equal("MATOS_FOLDER_LIBRARY", library.Key);
        Assert.Equal("MATOS_FOLDER_MATPHOTO_PHOTOS", FolderKeys.Of("matphoto-photos"));
    }

    [Theory]
    [InlineData("- volume: nope\n      name: X", "not a volume of the compose file")]
    [InlineData("- volume: library\n      name: ''", "needs a name")]
    [InlineData("- volume: library\n      name: A\n    - volume: library\n      name: B", "named twice")]
    [InlineData("- volume: unused\n      name: X", "no service mounts")]
    [InlineData("- volume: outside\n      name: X", "external volume already")]
    public void A_wrong_data_folder_is_an_error_of_the_entry(string folders, string message)
    {
        var yaml = $"""
            x-matos:
              id: wrong
              name: Wrong
              state: beta
              folders:
                {folders}
            services:
              web:
                image: x
                volumes: [ "library:/l", "outside:/o" ]
            volumes:
              library:
              unused:
              outside:
                external: true
            """;
        var ex = Assert.Throws<FormatException>(() => CatalogParser.Parse(yaml, "wrong.yml"));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void MatPhoto_offers_its_photo_library()
    {
        var folder = Assert.Single(BuiltIn("matphoto").Folders);
        Assert.Equal("matphoto-photos", folder.Volume);
        Assert.Equal(["web:/storage/photos"], folder.Mounts);
        Assert.False(folder.ReadOnly);
    }

    // ---------------------------------------------------------------- the choice and the compose file

    [Fact]
    public void The_choice_is_kept_as_a_variable_and_checked()
    {
        var app = Entry();
        var errors = new List<string>();
        var values = AppVariables.Resolve(app, new Dictionary<string, string?> { ["MATOS_FOLDER_LIBRARY"] = " nas-photos " }, null, errors);
        Assert.Empty(errors);
        Assert.Equal("nas-photos", values["MATOS_FOLDER_LIBRARY"]);
        Assert.Equal(string.Empty, values["MATOS_FOLDER_INBOX"]);
        Assert.Equal(new Dictionary<string, string> { ["library"] = "nas-photos" }, AppVariables.Folders(app, values));

        // An update keeps the choice; a later answer changes it.
        var kept = AppVariables.Resolve(app, new Dictionary<string, string?>(), values, errors);
        Assert.Equal("nas-photos", kept["MATOS_FOLDER_LIBRARY"]);

        AppVariables.Resolve(app, new Dictionary<string, string?> { ["MATOS_FOLDER_LIBRARY"] = "bad name; rm -rf /" }, null, errors);
        Assert.Single(errors);
    }

    [Fact]
    public void Without_a_choice_the_compose_file_keeps_the_apps_own_volumes()
    {
        var compose = Parse(new ComposeRenderer(Options).Render(Entry(), App()));
        var volumes = (IDictionary<object, object>)compose["volumes"];
        Assert.Null(volumes["library"]);
        Assert.Null(volumes["inbox"]);
        var labels = (IDictionary<object, object>)((IDictionary<object, object>)((IDictionary<object, object>)compose["services"])["web"])["labels"];
        Assert.Equal("own", labels["matos.folder.library"]);
    }

    [Fact]
    public void A_chosen_volume_is_an_external_volume_and_a_read_only_folder_is_mounted_read_only()
    {
        var text = new ComposeRenderer(Options).Render(Entry(), App(), folders: new Dictionary<string, string> { ["library"] = "nas-photos", ["inbox"] = "scans" });
        var compose = Parse(text);
        var volumes = (IDictionary<object, object>)compose["volumes"];
        var library = (IDictionary<object, object>)volumes["library"];
        Assert.Equal("nas-photos", library["name"]);
        Assert.Equal("true", library["external"]?.ToString());
        Assert.Equal("scans", ((IDictionary<object, object>)volumes["inbox"])["name"]);
        Assert.Null(volumes["data"]);

        var services = (IDictionary<object, object>)compose["services"];
        var web = (IEnumerable<object>)((IDictionary<object, object>)services["web"])["volumes"];
        Assert.Contains("library:/photos", web.Select(v => v.ToString()));
        var inbox = web.OfType<IDictionary<object, object>>().Single();
        Assert.Equal("true", inbox["read_only"]?.ToString());

        // The place is a label of every service that mounts the folder: compose re-creates the container when it changes.
        var webLabels = (IDictionary<object, object>)((IDictionary<object, object>)services["web"])["labels"];
        Assert.Equal("nas-photos", webLabels["matos.folder.library"]);
        Assert.Equal("scans", webLabels["matos.folder.inbox"]);
        Assert.False(((IDictionary<object, object>)((IDictionary<object, object>)services["worker"])["labels"]).ContainsKey("matos.folder.inbox"));

        // A writable folder stays as the entry mounts it.
        var worker = (IEnumerable<object>)((IDictionary<object, object>)services["worker"])["volumes"];
        Assert.Equal(["library:/photos:rw"], worker.Select(v => v.ToString()));
    }

    [Fact]
    public void A_short_mount_of_a_read_only_folder_gets_ro()
    {
        var yaml = Photos.ReplaceLineEndings("\n").Replace("name: Photo library\n", "name: Photo library\n      readOnly: true\n", StringComparison.Ordinal);
        var compose = Parse(new ComposeRenderer(Options).Render(Entry(yaml), App(), folders: new Dictionary<string, string> { ["library"] = "nas" }));
        var services = (IDictionary<object, object>)compose["services"];
        Assert.Contains("library:/photos:ro", ((IEnumerable<object>)((IDictionary<object, object>)services["web"])["volumes"]).Select(v => v.ToString()));
        Assert.Equal(["library:/photos:ro"], ((IEnumerable<object>)((IDictionary<object, object>)services["worker"])["volumes"]).Select(v => v.ToString()));
    }

    // ---------------------------------------------------------------- the paths of the explorer

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("a/b", "a/b")]
    [InlineData("/a//b/./c/", "a/b/c")]
    [InlineData("a\\b", "a/b")]
    [InlineData("Fotos 2026/Sommer", "Fotos 2026/Sommer")]
    public void A_safe_path_is_normalized(string? input, string expected) => Assert.Equal(expected, VolumePaths.Normalize(input));

    [Theory]
    [InlineData("..")]
    [InlineData("a/../../etc")]
    [InlineData("../etc/passwd")]
    [InlineData("a/..")]
    [InlineData("C:/Windows")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    public void A_path_that_could_leave_the_volume_is_refused(string input) => Assert.Null(VolumePaths.Normalize(input));

    [Fact]
    public void A_very_long_path_is_refused() => Assert.Null(VolumePaths.Normalize(new string('a', VolumePaths.MaxLength + 1)));

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("  spaced name.txt ", "spaced name.txt")]
    [InlineData("a/b", null)]
    [InlineData("a\\b", null)]
    [InlineData("..", null)]
    [InlineData(".", null)]
    [InlineData("", null)]
    [InlineData("c:x", null)]
    public void A_name_is_one_segment(string input, string? expected) => Assert.Equal(expected, VolumePaths.Name(input));

    [Fact]
    public void Paths_are_combined_and_taken_apart()
    {
        Assert.Equal("a", VolumePaths.Combine(string.Empty, "a"));
        Assert.Equal("a/b", VolumePaths.Combine("a", "b"));
        Assert.Equal("a", VolumePaths.Parent("a/b"));
        Assert.Equal(string.Empty, VolumePaths.Parent("a"));
        Assert.Equal("b", VolumePaths.Leaf("a/b"));
        Assert.True(VolumePaths.IsInside("a/b", "a"));
        Assert.True(VolumePaths.IsInside("a", "a"));
        Assert.False(VolumePaths.IsInside("ab", "a"));
        Assert.False(VolumePaths.IsInside("a", string.Empty));
        Assert.Equal([("photos", ""), ("2026", "2026"), ("May", "2026/May")], VolumePaths.Trail("photos", "2026/May"));
    }

    [Theory]
    [InlineData("photo.JPG", FilePreview.Image)]
    [InlineData("logo.svg", FilePreview.Image)]
    [InlineData("notes.md", FilePreview.Text)]
    [InlineData("Dockerfile", FilePreview.Text)]
    [InlineData(".env", FilePreview.Text)]
    [InlineData("movie.mkv", FilePreview.None)]
    [InlineData("archive.zip", FilePreview.None)]
    public void A_file_is_previewed_by_its_kind(string name, FilePreview preview) => Assert.Equal(preview, VolumePaths.PreviewOf(name));

    [Theory]
    [InlineData("photos", true)]
    [InlineData("matos-matphoto_matphoto-data", true)]
    [InlineData("Nas.Photos", true)]
    [InlineData("-x", false)]
    [InlineData("a b", false)]
    [InlineData("a;rm", false)]
    public void Volume_names_are_checked(string name, bool valid) => Assert.Equal(valid, VolumePaths.IsVolumeName(name));

    [Fact]
    public void An_own_volume_has_a_lowercase_name()
    {
        Assert.True(VolumePaths.IsOwnName("nas-photos"));
        Assert.False(VolumePaths.IsOwnName("NAS"));
        Assert.False(VolumePaths.IsOwnName(new string('a', 64)));
    }

    // ---------------------------------------------------------------- MatDock's answers

    [Fact]
    public void A_volume_of_MatDock_is_read_with_its_app_and_its_users()
    {
        var apps = new List<InstalledApp>
        {
            new() { Id = 1, Name = "MatPhoto", Slug = "matphoto", ProjectName = "matos-matphoto" },
            new() { Id = 2, Name = "MatPhoto 2", Slug = "matphoto-2", ProjectName = "matos-matphoto-2" },
        };
        var share = VolumeService.Parse(JsonNode.Parse("""
            {"name":"nas-photos","kind":"smb","managedBy":"matos","description":"Photos","stack":null,"containers":["matos-matphoto-2-web","other"],
             "sizeBytes":null,"smbHost":"nas","smbShare":"photos","smbPath":"family","smbUsername":"anna","hasSmbPassword":true,"readOnly":false}
            """)!.AsObject(), apps);
        Assert.True(share.Ours);
        Assert.True(share.IsShare);
        Assert.True(share.InUse);
        Assert.Equal(["MatPhoto 2", "other"], share.Users);
        Assert.Equal(@"\\nas\photos\family", share.Address);
        Assert.True(share.HasPassword);
        Assert.Null(share.Owner);

        var own = VolumeService.Parse(JsonNode.Parse("""{"name":"matos-matphoto_matphoto-data","kind":"local","stack":"matos-matphoto","containers":[],"sizeBytes":2048}""")!.AsObject(), apps);
        Assert.False(own.Ours);
        Assert.Equal("MatPhoto", own.Owner?.Name);
        Assert.Equal(2048, own.SizeBytes);
        Assert.False(own.InUse);
        Assert.Equal("This machine", own.Address);
    }

    [Fact]
    public void The_form_is_checked_before_MatDock_is_asked()
    {
        Assert.Null(VolumeService.Validate(new VolumeInput { Name = "photos", Kind = VolumeKind.Local }, isEdit: false));
        Assert.NotNull(VolumeService.Validate(new VolumeInput { Name = "Photos!", Kind = VolumeKind.Local }, isEdit: false));
        Assert.NotNull(VolumeService.Validate(new VolumeInput { Name = "nas", Kind = VolumeKind.Smb, Host = "nas" }, isEdit: false));
        Assert.NotNull(VolumeService.Validate(new VolumeInput { Name = "nas", Kind = VolumeKind.Smb, Host = "nas", Share = "s", Password = "a,b" }, isEdit: false));
        Assert.Null(VolumeService.Validate(new VolumeInput { Name = "nas", Kind = VolumeKind.Smb, Host = "nas", Share = "s", Username = "u", Password = "p" }, isEdit: false));
        Assert.NotNull(VolumeService.Validate(new VolumeInput { Name = "nfs", Kind = VolumeKind.Nfs, Host = "nas" }, isEdit: false));
        Assert.NotNull(VolumeService.Validate(new VolumeInput { Name = "x", Kind = VolumeKind.Other }, isEdit: false));
    }

    [Fact]
    public void The_reason_of_MatDock_is_shown_without_the_http_status()
        => Assert.Equal("Volume 'x' is used by web.", VolumeService.Reason(new InvalidOperationException("MatDock answered HTTP 409: Volume 'x' is used by web.")));
}
