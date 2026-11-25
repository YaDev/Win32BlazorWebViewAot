namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A stream wrapper that disposes its inner stream as soon as it has been read to the end.
	/// </summary>
	/// <remarks>
	/// WebView2 owns the native stream it is handed and releases it on its own schedule, which may be never if the page
	/// stops asking. Closing at end-of-read keeps file handles bounded no matter what the browser does next.
	///
	/// <para>
	/// Seeking is delegated rather than refused. The browser wraps this stream in an <c>IStream</c> and probes it with
	/// <see cref="Seek"/> before reading, so a refusal here surfaced as a <see cref="NotSupportedException"/> on every
	/// single asset request — handled by the interop layer, therefore invisible except as a first-chance break in every
	/// debugging session. A source that cannot seek is closed at end-of-read exactly as before; content with no seek
	/// support is buffered into a <see cref="MemoryStream"/> by <c>WebView2ResourceHandler</c> instead of reaching here.
	/// </para>
	/// </remarks>
	internal sealed class AutoCloseOnReadCompleteStream(Stream innerStream) : Stream
	{
		private readonly Stream _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
		private bool _isClosed;

		public override bool CanRead => !_isClosed && _innerStream.CanRead;

		public override bool CanSeek => !_isClosed && _innerStream.CanSeek;

		public override bool CanWrite => false;

		public override long Length => _innerStream.Length;

		public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }

		public override int Read(byte[] buffer, int offset, int count)
		{
			var read = _innerStream.Read(buffer, offset, count);
			if (read == 0 && !_innerStream.CanSeek)
			{
				CloseRead();
			}

			return read;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0 && !_innerStream.CanSeek)
			{
				CloseRead();
			}

			return read;
		}

		public override void Flush() => _innerStream.Flush();

		public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				CloseRead();
			}

			base.Dispose(disposing);
		}

		private void CloseRead()
		{
			if (_isClosed)
			{
				return;
			}

			_isClosed = true;
			_innerStream.Dispose();
		}
	}
}
