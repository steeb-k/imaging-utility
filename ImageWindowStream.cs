using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ImagingUtility
{
    // A read-only, seekable stream backed by RandomAccessImage with an optional window (offset/length)
    internal sealed class ImageWindowStream : Stream
    {
        private readonly RandomAccessImage _img;
        private readonly long _baseOffset;
        private readonly long _length;
        private long _position;

        public ImageWindowStream(RandomAccessImage img, long baseOffset = 0, long? length = null)
        {
            _img = img ?? throw new ArgumentNullException(nameof(img));
            long available = Math.Max(0, img.Length - baseOffset);
            _baseOffset = Math.Max(0, baseOffset);
            _length = length.HasValue ? Math.Min(length.Value, available) : available;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            if (_position >= _length) return 0;
            int toRead = (int)Math.Min(count, _length - _position);
            await _img.ReadAsync(_baseOffset + _position, buffer, offset, toRead, cancellationToken);
            _position += toRead;
            return toRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0) target = 0;
            if (target > _length) target = _length;
            _position = target;
            return _position;
        }

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
