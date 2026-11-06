using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Amazon.Runtime;
using ContainerForest.Aggregator.Application.Backups;
using ContainerForest.Aggregator.Infrastructure.Backups;
using Microsoft.Data.Sqlite;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Backups;

/// <summary>
/// The real archive with the real age, decrypted and unpacked the way the restore guide says, and the upload against a
/// local server. Without age the archive tests are skipped, unless CF_TEST_REQUIRE_SOPS=1, as in CI.
/// </summary>
public sealed class BackupArchiveTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cf-backup-test-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(directory, "aggregator.db");

    private string KeyDirectory => Path.Combine(directory, "keys");

    private string ConnectionString => $"Data Source={DatabasePath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task The_archive_decrypts_only_with_the_private_key_and_holds_the_database_and_the_key_ring()
    {
        await RequireToolsAsync();

        await CreateDatabaseAsync();
        WriteKey("key-1.xml", "<key/>");
        var (identity, recipient) = await GenerateAgeKeyAsync();
        var archiver = new SqliteBackupArchiver(ConnectionString, KeyDirectory, "age");

        using var archive = await archiver.CreateAsync(recipient, Ct);

        Assert.True(archive.SizeBytes > 0);
        Assert.StartsWith("age-encryption.org", Encoding.ASCII.GetString(File.ReadAllBytes(archive.Path)[..18]));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(archive.Path)!));

        var unpacked = await DecryptAndUnpackAsync(archive.Path, identity);
        Assert.Equal("<key/>", File.ReadAllText(Path.Combine(unpacked, "data-protection-keys", "key-1.xml")));
        await using var restored = new SqliteConnection($"Data Source={Path.Combine(unpacked, "aggregator.db")};Pooling=False");
        await restored.OpenAsync(Ct);
        await using var query = restored.CreateCommand();
        query.CommandText = "SELECT value FROM data;";
        Assert.Equal("in the wal", await query.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Disposing_the_archive_leaves_nothing_behind()
    {
        await RequireToolsAsync();

        await CreateDatabaseAsync();
        WriteKey("key-1.xml", "<key/>");
        var (_, recipient) = await GenerateAgeKeyAsync();
        var archiver = new SqliteBackupArchiver(ConnectionString, KeyDirectory, "age");

        var archive = await archiver.CreateAsync(recipient, Ct);
        archive.Dispose();

        Assert.False(Directory.Exists(Path.Combine(directory, ".backup-tmp")));
    }

    [Fact]
    public async Task An_empty_key_ring_fails_the_backup()
    {
        await CreateDatabaseAsync();
        var archiver = new SqliteBackupArchiver(ConnectionString, KeyDirectory, "age");

        var failure = await Assert.ThrowsAsync<BackupFailedException>(() => archiver.CreateAsync("age1unused", Ct));

        Assert.Contains("key ring", failure.Message);
        Assert.False(Directory.Exists(Path.Combine(directory, ".backup-tmp")));
    }

    [Fact]
    public async Task A_missing_age_binary_is_a_clear_failure_and_leaves_no_plain_files()
    {
        await CreateDatabaseAsync();
        WriteKey("key-1.xml", "<key/>");
        var archiver = new SqliteBackupArchiver(ConnectionString, KeyDirectory, Path.Combine(directory, "no-such-age"));

        var failure = await Assert.ThrowsAsync<BackupFailedException>(() => archiver.CreateAsync("age1unused", Ct));

        Assert.Contains("age", failure.Message);
        Assert.False(Directory.Exists(Path.Combine(directory, ".backup-tmp")));
    }

    [Fact]
    public async Task Files_left_by_a_crashed_run_are_removed()
    {
        await CreateDatabaseAsync();
        var leftover = Path.Combine(directory, ".backup-tmp");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "aggregator.db"), "plain");
        var archiver = new SqliteBackupArchiver(ConnectionString, KeyDirectory, "age");

        await Assert.ThrowsAsync<BackupFailedException>(() => archiver.CreateAsync("age1unused", Ct));

        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public async Task A_snapshot_is_taken_from_a_database_in_use()
    {
        await using var writer = await CreateDatabaseAsync();
        var target = Path.Combine(directory, "copy.db");

        await SqliteSnapshot.WriteAsync(ConnectionString, target, Ct);

        Assert.True(await SqliteSnapshot.IsIntactAsync(target, Ct));
    }

    [Fact]
    public async Task A_refusal_of_the_bucket_says_why()
    {
        using var server = new LocalS3Server(HttpStatusCode.Forbidden, "<Error><Code>AccessDenied</Code><Message>No.</Message></Error>");
        var file = Path.Combine(directory, "archive");
        File.WriteAllText(file, "content");

        var failure = await Assert.ThrowsAsync<BackupFailedException>(() =>
            new S3BackupStorage().UploadAsync(server.Destination, file, "forest/a.age", Ct));

        Assert.Contains("AccessDenied", failure.Message);
        Assert.DoesNotContain(server.Destination.SecretAccessKey, failure.Message);
    }

    [Fact]
    public async Task An_upload_puts_the_file_under_its_key_signed_with_the_access_key()
    {
        using var server = new LocalS3Server(HttpStatusCode.OK, "");
        var file = Path.Combine(directory, "archive");
        File.WriteAllText(file, "content");

        await new S3BackupStorage().UploadAsync(server.Destination, file, "forest/a.age", Ct);

        var request = await server.Request;
        Assert.StartsWith("PUT /bucket/forest/a.age ", request);
        Assert.Contains("AWS4-HMAC-SHA256 Credential=key-id/", request);
        Assert.DoesNotContain(server.Destination.SecretAccessKey, request);
        Assert.Contains("content", request);
    }

    /// <summary>
    /// The SDK streams over HTTPS with chunked signing and adds checksums, and Cloudflare R2 refuses that: only a plain
    /// signed request will do. Plain HTTP does not show this, the SDK behaves differently there.
    /// </summary>
    [Fact]
    public async Task An_upload_over_https_is_a_plain_signed_request()
    {
        using var server = new LocalS3Server(HttpStatusCode.OK, "", useTls: true);
        var file = Path.Combine(directory, "archive");
        File.WriteAllText(file, "content");

        await new S3BackupStorage(new TrustingHttpClientFactory()).UploadAsync(server.Destination, file, "forest/a.age", Ct);

        var request = await server.Request;
        var headers = request[..request.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        Assert.StartsWith("PUT /bucket/forest/a.age ", headers);
        Assert.Matches("(?i)x-amz-content-sha256: [0-9a-f]{64}", headers);
        Assert.DoesNotContain("STREAMING", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aws-chunked", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-amz-trailer", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-amz-checksum", headers, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\r\n\r\ncontent", request);
    }

    /// <summary>Leaves a connection open with a row still in the WAL, like the running aggregator.</summary>
    private async Task<SqliteConnection> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE data (value TEXT); INSERT INTO data VALUES ('in the wal');";
        await command.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    private void WriteKey(string name, string content)
    {
        Directory.CreateDirectory(KeyDirectory);
        File.WriteAllText(Path.Combine(KeyDirectory, name), content);
    }

    private async Task<(string IdentityFile, string Recipient)> GenerateAgeKeyAsync()
    {
        var identityFile = Path.Combine(directory, "identity.txt");
        await RunAsync("age-keygen", "-o", identityFile);
        var recipient = File.ReadAllLines(identityFile).Single(line => line.StartsWith("# public key: ", StringComparison.Ordinal))["# public key: ".Length..];
        return (identityFile, recipient);
    }

    private async Task<string> DecryptAndUnpackAsync(string archivePath, string identityFile)
    {
        var plain = Path.Combine(directory, "plain.tar.gz");
        await RunAsync("age", "--decrypt", "--identity", identityFile, "--output", plain, archivePath);
        var target = Path.Combine(directory, "unpacked");
        Directory.CreateDirectory(target);
        await using var file = File.OpenRead(plain);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, target, overwriteFiles: true, Ct);
        return target;
    }

    private static async Task<(string Out, string Error)> RunAsync(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"{tool} failed: {await error}");
        return (await output, await error);
    }

    private static async Task RequireToolsAsync()
    {
        try
        {
            await RunAsync("age", "--version");
            await RunAsync("age-keygen", "--version");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            const string message = "age and age-keygen are not on PATH.";
            if (Environment.GetEnvironmentVariable("CF_TEST_REQUIRE_SOPS") == "1")
            {
                Assert.Fail(message);
            }

            Assert.Skip(message);
        }
    }

    private sealed class TrustingHttpClientFactory : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
            new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }

    /// <summary>Answers one S3 request and keeps what it received.</summary>
    private sealed class LocalS3Server : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly bool useTls;

        public LocalS3Server(HttpStatusCode status, string body, bool useTls = false)
        {
            this.useTls = useTls;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Destination = new BackupDestination($"{(useTls ? "https" : "http")}://127.0.0.1:{port}", "bucket", "auto", "key-id", "the-secret-key");
            Request = ServeAsync(status, body);
        }

        public BackupDestination Destination { get; }

        public Task<string> Request { get; }

        public void Dispose() => listener.Stop();

        private async Task<string> ServeAsync(HttpStatusCode status, string body)
        {
            using var client = await listener.AcceptTcpClientAsync();
            Stream stream = client.GetStream();
            if (useTls)
            {
                var tls = new SslStream(stream);
                await tls.AuthenticateAsServerAsync(CreateCertificate());
                stream = tls;
            }

            var received = new StringBuilder();
            var buffer = new byte[8192];
            while (!Complete(received.ToString()))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                received.Append(Encoding.Latin1.GetString(buffer, 0, read));
                if (received.ToString().Contains("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)
                    && received.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray());
                }
            }

            var head = $"HTTP/1.1 {(int)status} {status}\r\nETag: \"abc\"\r\nContent-Type: application/xml\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head + body));
            return received.ToString();
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
        }

        private static bool Complete(string request)
        {
            var end = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            var length = request[..end].Split("\r\n")
                .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(line => int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault();
            return request.Length - end - 4 >= length;
        }
    }
}
