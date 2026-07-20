using HeatSynQ.Web.Services;

namespace HeatSynQ.Web.Tests.Administration;

public sealed class ManagedFileWriterTests
{
    [Fact]
    public async Task Failed_streaming_removes_partial_upload_file()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "heatsynq-managed-writer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, "file.upload");
        var destination = Path.Combine(root, "file.bin");

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                ManagedFileWriter.WriteAsync(
                    new FailingReadStream(),
                    temporary,
                    destination,
                    CancellationToken.None));

            Assert.False(File.Exists(temporary));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailingReadStream : Stream
    {
        private bool _returnedData;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_returnedData)
            {
                throw new IOException("Simulated client disconnect.");
            }

            _returnedData = true;
            buffer.Span[0] = 42;
            return 1;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
