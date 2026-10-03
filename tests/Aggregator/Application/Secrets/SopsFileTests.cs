using System.Text;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests.Secrets;

/// <summary>The encrypted samples keep the structure sops 3.13 writes, with the ciphertexts shortened.</summary>
public class SopsFileTests
{
    private const string EncryptedYaml = """
        db:
            password: ENC[AES256_GCM,data:TJ+vKR2JMw==,iv:NYjY=,tag:HiRb==,type:str]
        sops:
            age:
                - recipient: age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng
            mac: ENC[AES256_GCM,data:EEjh,iv:iwlW,tag:DfRl,type:str]
            version: 3.13.3
        """;

    private const string EncryptedDotenv = """
        API_KEY=ENC[AES256_GCM,data:5Ms7UHo0,iv:rEaG=,tag:4fbb==,type:str]
        sops_age__list_0__map_recipient=age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng
        sops_mac=ENC[AES256_GCM,data:HRRL,iv:jAIL,tag:A9i1,type:str]
        sops_version=3.13.3
        """;

    private const string EncryptedJson = """
        {
            "a": "ENC[AES256_GCM,data:FQ==,iv:5hk8=,tag:go7M==,type:str]",
            "sops": { "mac": "ENC[AES256_GCM,data:t5C0,iv:+2MU,tag:f4A/,type:str]", "version": "3.13.3" }
        }
        """;

    private const string EncryptedBinary = """
        {
            "data": "ENC[AES256_GCM,data:/oDOYYQbam8V1Rc=,iv:t1H0=,tag:/MLg==,type:str]",
            "sops": { "mac": "ENC[AES256_GCM,data:/0LR,iv:EVrK,tag:i9p/,type:str]", "version": "3.13.3" }
        }
        """;

    private const string EncryptedIni = """
        [db]
        password = ENC[AES256_GCM,data:E1Aohq8+2w==,iv:Vos2=,tag:jjhL==,type:str]

        [sops]
        mac     = ENC[AES256_GCM,data:xICk,iv:TU85,tag:G2i5,type:str]
        version = 3.13.3
        """;

    [Theory]
    [InlineData("secrets.yaml", EncryptedYaml)]
    [InlineData("config/secrets.yml", EncryptedYaml)]
    [InlineData(".env", EncryptedDotenv)]
    [InlineData("app.env", EncryptedDotenv)]
    [InlineData("settings.json", EncryptedJson)]
    [InlineData("secrets/db_password", EncryptedBinary)]
    [InlineData("tls/server.key", EncryptedBinary)]
    [InlineData("app.ini", EncryptedIni)]
    public void Recognizes_sops_metadata_in_each_format(string path, string content) =>
        Assert.True(IsEncrypted(path, content));

    [Theory]
    [InlineData("README.md", "Secrets are encrypted with sops: run `sops edit secrets.yaml`.")]
    [InlineData("compose.yaml", "services:\n  app:\n    image: app\n    labels:\n      sops: \"yes\"\n")]
    [InlineData("values.yaml", "# sops:\nkey: ENC[AES256_GCM,data:x]\n")]
    [InlineData(".env", "SOPS_VERSION=3\nTOKEN=ENC[AES256_GCM,data:x]\n")]
    [InlineData("settings.json", "{\"sops\": \"a tool\"}")]
    [InlineData("settings.json", "{\"sops\": {")]
    [InlineData("data.bin", "{\"sops\": {\"version\": \"3\"}}")]
    [InlineData("script.sh", "#!/bin/sh\nsops decrypt secrets.yaml\n")]
    [InlineData("app.ini", "[app]\nsops = 1\n")]
    public void Leaves_files_that_only_mention_sops(string path, string content) =>
        Assert.False(IsEncrypted(path, content));

    // A binary sops file has the metadata format of JSON, but a .json file is checked for its own: "data" is not needed.
    [Fact]
    public void Json_file_needs_no_data_key() =>
        Assert.True(IsEncrypted("settings.json", "{\"sops\": {\"version\": \"3\"}}"));

    [Fact]
    public void Yaml_with_windows_line_endings_is_recognized() =>
        Assert.True(IsEncrypted("secrets.yaml", EncryptedYaml.ReplaceLineEndings("\r\n")));

    private static bool IsEncrypted(string path, string content) =>
        SopsFile.IsEncrypted(StackFilePath.Parse(path), Encoding.UTF8.GetBytes(content));
}
