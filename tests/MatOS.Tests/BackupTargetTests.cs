using System.Text.Json.Nodes;
using MatOS.Core.Backups;
using MatOS.Core.Engines;

namespace MatOS.Tests;

/// <summary>
/// The backup targets: what the engine lists becomes a target in MatOS's words (never a secret), the form becomes the engine's body (a blank secret is
/// left out so the stored one stays), the form is checked before it is sent, and an engine error comes back as its plain reason.
/// </summary>
public class BackupTargetTests
{
    private static JsonObject Listed(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void An_sftp_target_is_read_with_its_address_and_without_secrets()
    {
        var target = BackupTargetService.Parse(Listed("""
            {"id":4,"name":"Box","type":"sftp","isDefault":true,"managedBy":"matos","sftpHost":"box.local","sftpPort":2222,"sftpDirectory":"/backups",
             "sftpUsername":"backup","hasSftpPassword":false,"hasSftpPrivateKey":true,"sftpHostKey":"SHA256:abc","backupCount":3,"schedules":["Nightly"]}
            """))!;

        Assert.Equal((4L, "Box", BackupTargetKind.Sftp, true, true), (target.Id, target.Name, target.Kind, target.IsDefault, target.Ours));
        Assert.Equal("sftp://backup@box.local:2222/backups", target.Address);
        Assert.Equal((false, true, "SHA256:abc", 3), (target.HasPassword, target.HasPrivateKey, target.HostKey, target.BackupCount));
        Assert.Equal(["Nightly"], target.Schedules);
    }

    [Fact]
    public void A_target_of_someone_else_is_not_ours_and_an_unknown_kind_is_left_out()
    {
        var share = BackupTargetService.Parse(Listed("""{"id":1,"name":"NAS","type":"smb","managedBy":null,"smbHost":"nas","smbShare":"backups","smbDirectory":"a/b","hasSmbPassword":true}"""))!;
        Assert.False(share.Ours);
        Assert.Equal(@"\\nas\backups\a\b", share.Address);
        Assert.True(share.HasPassword);

        Assert.True(BackupTargetService.Parse(Listed("""{"id":2,"name":"Old","type":"webdav","managedBy":"mattiverse","webDavUrl":"https://x/dav"}"""))!.Ours);
        Assert.Null(BackupTargetService.Parse(Listed("""{"id":3,"name":"x","type":"ftp"}""")));
    }

    [Fact]
    public void The_body_has_only_the_fields_of_its_kind_and_no_blank_secret()
    {
        var body = BackupTargetService.ToBody(new BackupTargetInput
        {
            Name = " Box ", Kind = BackupTargetKind.Sftp, Host = " box ", Port = 22, Folder = "", Username = "u", Password = "", PrivateKey = "KEY", Url = "ignored",
        });

        Assert.Equal("Box", body["name"]!.GetValue<string>());
        Assert.Equal("sftp", body["type"]!.GetValue<string>());
        Assert.Equal("box", body["sftpHost"]!.GetValue<string>());
        Assert.Null(body["sftpDirectory"]);
        Assert.False(body.ContainsKey("sftpPassword"));
        Assert.Equal("KEY\n", body["sftpPrivateKey"]!.GetValue<string>());
        Assert.False(body.ContainsKey("webDavUrl"));

        var dav = BackupTargetService.ToBody(new BackupTargetInput { Name = "Dav", Kind = BackupTargetKind.WebDav, Url = "https://x/dav", Password = "pw" });
        Assert.Equal("pw", dav["webDavPassword"]!.GetValue<string>());
        Assert.False(dav.ContainsKey("sftpHost"));
    }

    [Theory]
    [InlineData(BackupTargetKind.Sftp, null, null, "u", "p", null, false, "server's name")]
    [InlineData(BackupTargetKind.Sftp, "sftp://box", null, "u", "p", null, false, "without sftp://")]
    [InlineData(BackupTargetKind.Sftp, "box", null, "", "p", null, false, "user name")]
    [InlineData(BackupTargetKind.Sftp, "box", null, "u", null, null, false, "password or a private key")]
    [InlineData(BackupTargetKind.WebDav, null, "ftp://x", null, null, null, false, "WebDAV folder")]
    [InlineData(BackupTargetKind.WebDav, null, "https://me:pw@x/dav", null, null, null, false, "into their fields")]
    [InlineData(BackupTargetKind.Smb, "nas", null, null, null, null, false, "name of the share")]
    [InlineData(BackupTargetKind.Restic, null, null, null, null, null, false, "outside MatOS")]
    public void A_form_that_cannot_work_says_why(BackupTargetKind kind, string? host, string? url, string? user, string? password, string? share, bool isEdit, string expected)
    {
        var input = new BackupTargetInput { Name = "x", Kind = kind, Host = host, Url = url, Username = user, Password = password, Share = share };
        Assert.Contains(expected, BackupTargetService.Validate(input, hasStoredSecret: false, isEdit));
    }

    [Fact]
    public void An_edit_may_keep_the_stored_secret()
    {
        var input = new BackupTargetInput { Name = "x", Kind = BackupTargetKind.Sftp, Host = "box", Username = "u" };
        Assert.NotNull(BackupTargetService.Validate(input, hasStoredSecret: false, isEdit: false));
        Assert.Null(BackupTargetService.Validate(input, hasStoredSecret: false, isEdit: true));
        Assert.Null(BackupTargetService.Validate(new BackupTargetInput { Name = "x", Kind = BackupTargetKind.Smb, Host = "nas", Share = "b" }, false, false));
    }

    [Fact]
    public void An_engine_error_comes_back_as_its_reason()
    {
        Assert.Equal("Target is still used by 2 backup(s) - delete those first.",
            BackupTargetService.Reason(new InvalidOperationException("MatDock answered HTTP 409: Target is still used by 2 backup(s) - delete those first.")));
        Assert.Equal("The backup engine is not reachable: refused",
            BackupTargetService.Reason(new InvalidOperationException("MatDock is not reachable: refused")));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("0", null, false)]
    [InlineData("7", 7L, false)]
    [InlineData("nonsense", null, false)]
    public void The_choice_of_a_form_is_where_the_backup_goes(string? value, long? id, bool useDefault)
        => Assert.Equal(new BackupDestination(id, useDefault), BackupDestination.Parse(value));

    [Fact]
    public void A_schedule_keeps_its_target_when_it_is_written_back()
    {
        var body = MatDockClient.ScheduleBodyOf(Listed("""{"name":"n","stack":null,"frequency":"daily","time":"03:00","retention":7,"targetId":5}"""));
        Assert.Equal(5, body["targetId"]!.GetValue<long>());
    }
}
