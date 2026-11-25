// UTF-16 buffers this assembly allocates and frees itself: NativeMemory allocation, matching Free, never PWSTR.Dispose.
// The rule is load-bearing — see "Native text buffers" in doc/DESIGN.md.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A null-terminated UTF-16 string in unmanaged memory, paired with the free that matches its allocation.
	/// </summary>
	internal readonly unsafe struct NativeText : IDisposable
	{
		private readonly nint _pointer;

		private NativeText(nint pointer) => _pointer = pointer;

		/// <summary>The buffer as the <c>PWSTR</c> the Win32 signatures ask for.</summary>
		public PWSTR Value => new(_pointer);

		/// <summary>Copies <paramref name="text"/> into unmanaged memory, terminating it with a null wchar.</summary>
		public static NativeText From(string? text)
		{
			text ??= string.Empty;

			// +1 for the terminator the APIs require and `string` does not carry.
			var size = ((nuint)text.Length + 1) * sizeof(char);
			var pointer = (char*)NativeMemory.Alloc(size);
			fixed (char* source = text)
			{
				Buffer.MemoryCopy(source, pointer, size, (nuint)text.Length * sizeof(char));
			}

			pointer[text.Length] = '\0';
			return new NativeText((nint)pointer);
		}

		/// <summary>Releases the buffer. Safe to call on an instance that was never filled in.</summary>
		public void Dispose()
		{
			if (_pointer != 0)
			{
				NativeMemory.Free((void*)_pointer);
			}
		}
	}
}
