// Nothing window-management happens here on purpose. The caption is Windows', so there is no drag region to wire up and
// no min/max/close to post back to .NET — the borderless sample (Win32BlazorWebViewAot.HtmlBorderless) shows that protocol
// instead. What this page does show is JavaScript calling into C#: see the "Calls into .NET" section below.

let clicks = 0;
const countButton = document.getElementById('btn-count');
countButton.addEventListener('click', () => {
	clicks++;
	countButton.textContent = `Clicked ${clicks} time(s)`;
});

document.getElementById('btn-viewport').addEventListener('click', () => {
	// innerHeight is the client area, i.e. the window minus the caption: a smaller number than the window height is the
	// proof that the page was given the space below the title bar rather than the whole window.
	writeLine(`client ${window.innerWidth}x${window.innerHeight}, dpr ${window.devicePixelRatio}`);
});

// --- Bringing the .NET channel up ------------------------------------------------
// The host page loads _framework/blazor.webview.js with autostart="false", so nothing is connected until Blazor.start() runs.
// Autostart exists to find components and render them; this page has none, and a start that fails leaves the document
// unattached — every call then comes back as "Cannot receive IPC messages when no page is attached".
//
// Start once, keep the promise, and let each caller await it: a click during startup queues behind the handshake instead of
// throwing or being dropped.
const channel = window.Blazor
	? Blazor.start()
	: Promise.reject(new Error('blazor.webview.js did not load — check that the build supplied _framework/blazor.webview.js'));

// The assembly that declares the [JSInvokable] methods (DotNetBridge.cs). A string, so a typo here fails at runtime rather
// than at compile time — which is why it is named once, in one place.
const ASSEMBLY = 'Win32BlazorWebViewAot.Html';

/// Awaited call into .NET: the shape to use for anything real.
function callDotNet(method, ...args) {
	return channel.then(() => DotNet.invokeMethodAsync(ASSEMBLY, method, ...args));
}

// --- Calls into .NET -------------------------------------------------------------
document.getElementById('btn-machine').addEventListener('click', () => {
	// GetMachineName is a plain synchronous C# method, and it is still called with invokeMethodAsync: the synchronous
	// DotNet.invokeMethod throws "isn't supported" on this channel. Await the value instead — nothing is lost but the ability
	// to block the page.
	callDotNet('GetMachineName')
		.then((name) => writeLine(`machine ${name}`))
		.catch((error) => writeLine(`JS -> .NET failed: ${error}`));
});

document.getElementById('btn-sum').addEventListener('click', () => {
	callDotNet('SumAsync', 21, 21)
		.then((sum) => writeLine(`21 + 21 = ${sum} (awaited; the clock kept ticking through the 250 ms delay)`))
		.catch((error) => writeLine(`JS -> .NET failed: ${error}`));
});

document.getElementById('btn-facts').addEventListener('click', () => {
	callDotNet('GetRuntimeFactsAsync')
		.then((facts) => writeLine(facts.join('\n')))
		.catch((error) => writeLine(`JS -> .NET failed: ${error}`));
});

// Handshake: one awaited call proves the path works end to end before anyone clicks. Each method also logs on the .NET side,
// so "a line appeared in the page" and "C# actually ran" stay two separate confirmations.
callDotNet('GetRuntimeFactsAsync')
	.then((facts) => writeLine(`.NET is up — ${facts[0]}`))
	.catch((error) => writeLine(`JS -> .NET failed: ${error}`));

function writeLine(text) {
	const readout = document.getElementById('readout');
	readout.hidden = false;
	readout.textContent += `${text}\n`;
}

const clock = document.getElementById('clock');
setInterval(() => { clock.textContent = new Date().toLocaleTimeString(); }, 1000);
clock.textContent = new Date().toLocaleTimeString();
