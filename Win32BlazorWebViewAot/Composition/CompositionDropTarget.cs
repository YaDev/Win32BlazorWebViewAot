// External drag & drop for a composition-hosted WebView.
//
// A composition-hosted browser has no window of its own, so OLE has nothing to hand a drop to: the host must register
// itself as the drop target and forward to ICoreWebView2CompositionController3.DragEnter/DragOver/DragLeave/Drop. This is
// therefore not competing with the page for drops — it is the only object that can receive one.
//
// The interface is implemented through the source-generated COM marshalling ([GeneratedComClass] over DirectN's
// [GeneratedComInterface]), which is what keeps this AOT-safe: no System.Runtime.InteropServices.ComVisible, no
// System.Windows.Forms, no runtime marshaler (the assembly sets DisableRuntimeMarshalling).

using System.Runtime.InteropServices.Marshalling;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// The window's OLE drop target: forwards every drag message to the composition controller of a WebView that has no
	/// HWND of its own.
	/// </summary>
	/// <remarks>
	/// Drag messages arrive in screen coordinates and the browser wants client coordinates, so each one is converted here —
	/// the same conversion the mouse path does, for the same reason.
	/// </remarks>
	[GeneratedComClass]
	internal sealed partial class CompositionDropTarget : IDropTarget
	{
		private readonly HWND _window;

		// The controller comes and goes: a drop can be in flight while the browser dies, and the target outlives
		// initialization failures. Reading it through a accessor keeps this class independent of host lifetime.
		private readonly Func<IComObject<ICoreWebView2CompositionController>?> _controllerAccessor;

		internal CompositionDropTarget(HWND window, Func<IComObject<ICoreWebView2CompositionController>?> controllerAccessor)
		{
			_window = window;
			_controllerAccessor = controllerAccessor;
		}

		HRESULT IDropTarget.DragEnter(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, ref DROPEFFECT pdwEffect)
		{
			if (_controllerAccessor() is not { } controller)
			{
				return S_OK;
			}

			var effect = (uint)pdwEffect;
			var result = controller.DragEnter(pDataObj, (uint)grfKeyState, ToClient(pt), ref effect, throwOnError: false);
			pdwEffect = (DROPEFFECT)effect;

			return result;
		}

		HRESULT IDropTarget.DragOver(MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, ref DROPEFFECT pdwEffect)
		{
			if (_controllerAccessor() is not { } controller)
			{
				return S_OK;
			}

			var effect = (uint)pdwEffect;
			var result = controller.DragOver((uint)grfKeyState, ToClient(pt), ref effect, throwOnError: false);
			pdwEffect = (DROPEFFECT)effect;

			return result;
		}

		HRESULT IDropTarget.DragLeave()
		{
			if (_controllerAccessor() is not { } controller)
			{
				return S_OK;
			}

			// A browser that is already gone must still let the drag end, otherwise the source waits on a cursor that never
			// leaves the window.
			_ = controller.DragLeave(throwOnError: false);
			return S_OK;
		}

		HRESULT IDropTarget.Drop(IDataObject pDataObj, MODIFIERKEYS_FLAGS grfKeyState, POINTL pt, ref DROPEFFECT pdwEffect)
		{
			if (_controllerAccessor() is not { } controller)
			{
				pdwEffect = DROPEFFECT.DROPEFFECT_NONE;
				return S_OK;
			}

			var effect = (uint)pdwEffect;
			var result = controller.Drop(pDataObj, (uint)grfKeyState, ToClient(pt), ref effect, throwOnError: false);
			pdwEffect = (DROPEFFECT)effect;

			return result;
		}

		// HRESULT is a struct, so the success value cannot be a const; DirectN exposes no S_OK field of its own.
		private static readonly HRESULT S_OK = 0u;

		// OLE reports the drag position on the screen; the page lays out in its own client area.
		private POINT ToClient(POINTL screenPoint)
		{
			var point = new POINT { x = screenPoint.x, y = screenPoint.y };
			_ = Native.ScreenToClient(_window, ref point);
			return point;
		}
	}

	/// <summary>
	/// Reference-counts <c>OleInitialize</c>/<c>OleUninitialize</c> per thread.
	/// </summary>
	/// <remarks>
	/// <see cref="Native.RegisterDragDrop"/> needs the OLE apartment, not merely the COM one, and <c>[STAThread]</c> does not
	/// initialize OLE. The count matters because several windows can share a UI thread: uninitializing OLE when the first
	/// window closes would break drag &amp; drop in the ones that remain.
	/// </remarks>
	internal static class OleApartment
	{
		[ThreadStatic]
		private static int _count;

		/// <summary>Adds one OLE user to the calling thread, initializing OLE if this is the first.</summary>
		/// <exception cref="InvalidOperationException">OLE could not be initialized on this thread (not STA).</exception>
		public static void AddRef()
		{
			if (_count > 0)
			{
				_count++;
				return;
			}

			// S_FALSE means OLE was already initialized on this thread by someone else — still ours to release once.
			var result = Native.OleInitialize(IntPtr.Zero);
			if (result.IsError)
			{
				throw new InvalidOperationException(
					$"Drag & drop needs OLE initialized on the window's STA thread; OleInitialize failed with 0x{result.Value:x8}.");
			}

			_count = 1;
		}

		/// <summary>Removes one OLE user from the calling thread, uninitializing OLE when none remain.</summary>
		public static void Release()
		{
			if (_count > 0 && --_count == 0)
			{
				Native.OleUninitialize();
			}
		}
	}
}
