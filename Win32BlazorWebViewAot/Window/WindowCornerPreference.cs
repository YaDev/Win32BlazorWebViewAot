namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// How DWM rounds the corners of a window, applied through <c>DwmSetWindowAttribute</c>.
	/// </summary>
	/// <remarks>
	/// Values mirror <c>DWM_WINDOW_CORNER_PREFERENCE</c>. Windows 10 and earlier ignore the attribute, so a host that
	/// cares about square corners on those systems gets them anyway — which is the same visual result as asking for it.
	/// A maximized window is never rounded by DWM regardless of what is set here.
	/// </remarks>
	public enum WindowCornerPreference
	{
		/// <summary>Let the system decide, which on Windows 11 means rounded.</summary>
		Default = 0,

		/// <summary>Square corners.</summary>
		DoNotRound = 1,

		/// <summary>Rounded with the standard radius.</summary>
		Round = 2,

		/// <summary>Rounded with the smaller radius used for compact windows.</summary>
		RoundSmall = 3,
	}
}
