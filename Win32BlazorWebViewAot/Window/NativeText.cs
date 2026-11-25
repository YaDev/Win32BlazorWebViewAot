// A UTF-16 buffer this assembly owns: allocated here, freed here.
//
// DirectN's `PWSTR.From(string)` hands out a pointer to memory that lives in the managed allocation space, and
// `PWSTR.Dispose` releases it with a native free. That mismatch is not a leak but heap corruption — the process dies with
// STATUS_HEAP_CORRUPTION (0xC0000374), usually at the next unrelated allocation rather than at the offending call, which
// makes it one of the worst failures to trace after the fact. Verified standalone: `PWSTR.From` alone survives a full
// compaction; disposing what it returned crashes immediately, with no window code involved.
//
// So nothing here calls `PWSTR.Dispose`. The buffers are allocated with `NativeMemory` (plain malloc under
// DisableRuntimeMarshalling, AOT-safe) and freed with the matching `Free`. Windows copies both the class name at
// registration and the caption/title at creation, so freeing as soon as the call returns is correct.

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
