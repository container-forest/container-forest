using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ContainerForest.Aggregator.Application.Backups;

namespace ContainerForest.Aggregator.Infrastructure.Backups;

/// <summary>Puts archives into an S3-compatible bucket (Cloudflare R2, MinIO, AWS). The client is made per upload: settings may change at any time.</summary>
/// <param name="httpClientFactory">For tests against a server with its own certificate.</param>
internal sealed class S3BackupStorage(HttpClientFactory? httpClientFactory = null) : IBackupStorage
{
    public async Task UploadAsync(BackupDestination destination, string filePath, string objectKey, CancellationToken cancellationToken)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = destination.Endpoint,
            AuthenticationRegion = destination.Region,

            // Bucket in the path works everywhere; a bucket in the host name needs DNS for it.
            ForcePathStyle = true,

            // Other S3 servers do not all know the checksums the SDK adds by default.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (httpClientFactory is not null)
        {
            config.HttpClientFactory = httpClientFactory;
        }

        using var client = new AmazonS3Client(new BasicAWSCredentials(destination.AccessKeyId, destination.SecretAccessKey), config);
        try
        {
            await client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = destination.Bucket,
                    Key = objectKey,
                    FilePath = filePath,
                    ContentType = "application/octet-stream",

                    // Over HTTPS the SDK streams with chunked signing, which Cloudflare R2 refuses. This signs the
                    // file's hash instead, in one plain request every S3 server understands.
                    UseChunkEncoding = false,
                },
                cancellationToken);
        }
        catch (AmazonServiceException exception)
        {
            // The code and the status say what is wrong (NoSuchBucket, AccessDenied) and carry nothing secret.
            var reason = exception.ErrorCode ?? exception.StatusCode.ToString();
            throw new BackupFailedException($"The bucket refused the archive: {reason}.", exception);
        }
        catch (Exception exception) when (exception is AmazonClientException or HttpRequestException)
        {
            throw new BackupFailedException($"Could not reach {destination.Endpoint}.", exception);
        }
    }
}
