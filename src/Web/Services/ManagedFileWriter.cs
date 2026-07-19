using System.Security.Cryptography;

namespace HeatSynQ.Web.Services;

public static class ManagedFileWriter
{
    public static async Task<ManagedFileWriteResult> WriteAsync(
        Stream source,
        string temporaryPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var ownershipTransferred = false;
        try
        {
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            string checksum;
            await using (var checksumStream = File.OpenRead(temporaryPath))
            {
                checksum = Convert.ToHexString(await SHA256.HashDataAsync(
                    checksumStream,
                    cancellationToken));
            }

            var length = new FileInfo(temporaryPath).Length;
            File.Move(temporaryPath, destinationPath);
            ownershipTransferred = true;
            return new ManagedFileWriteResult(length, checksum);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed record ManagedFileWriteResult(long Length, string ChecksumSha256);
