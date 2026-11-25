// The page side of the window protocol. chrome.webview.postMessage is WebView2's built-in JS -> .NET channel;
// Program.cs listens on WebMessageReceived and maps these strings onto Win32Window operations.
const send = (message) => window.chrome?.webview?.postMessage(message);

// --- Title bar: drag to move, double-click to maximize/restore -------------------
const titlebar = document.getElementById('titlebar');

// mousedown must reach .NET while the button is still down — that is what keeps Windows tracking the pointer.
titlebar.addEventListener('mousedown', (event) => {
	if (event.button === 0) {
		send('win:drag');
	}
});

titlebar.addEventListener('dblclick', () => send('win:maximize'));

// Caption buttons must click, not drag: swallow their mousedown before it bubbles to the bar.
for (const button of document.querySelectorAll('.caption-buttons')) {
	button.addEventListener('mousedown', (event) => event.stopPropagation());
}

document.getElementById('btn-min').addEventListener('click', () => send('win:minimize'));
document.getElementById('btn-max').addEventListener('click', () => send('win:maximize'));
document.getElementById('btn-close').addEventListener('click', () => send('win:close'));

// --- Ordinary page proof-of-life --------------------------------------------------
let clicks = 0;
const countButton = document.getElementById('btn-count');
countButton.addEventListener('click', () => {
	clicks++;
	countButton.textContent = `Clicked ${clicks} time(s)`;
	document.title = `Win32BlazorWebViewAot HTML Sample — clicks ${clicks}`;
});

document.getElementById('btn-viewport').addEventListener('click', () => {
	writeLine(`client ${window.innerWidth}x${window.innerHeight}, dpr ${window.devicePixelRatio}`);
});

// --- The other channel: calls into .NET that want an answer ----------------------
// Everything above is fire-and-forget on chrome.webview.postMessage. DotNet.invokeMethodAsync is the opposite: typed
// arguments in, a value back. Use it whenever the page needs to know what .NET decided.

// Start blazor.webview.js once (the host page loads it with autostart="false" — this page has no components to render, and an
// autostart that fails leaves the document unattached so every call answers "Cannot receive IPC messages when no page is
// attached"). Callers await this promise instead of racing it.
const channel = window.Blazor
	? Blazor.start()
	: Promise.reject(new Error('blazor.webview.js did not load — check that the build supplied _framework/blazor.webview.js'));

// The assembly that declares the [JSInvokable] methods (DotNetBridge.cs). A string, so a typo here fails at runtime rather
// than at compile time — which is why it is named once, in one place.
const ASSEMBLY = 'Win32BlazorWebViewAot.HtmlBorderless';

function callDotNet(method, ...args) {
	return channel.then(() => DotNet.invokeMethodAsync(ASSEMBLY, method, ...args));
}

document.getElementById('btn-machine').addEventListener('click', () => {
	// GetMachineName is a plain synchronous C# method and still comes back as a promise: the synchronous
	// DotNet.invokeMethod throws "isn't supported" on this channel.
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

// The proof that this is a two-way channel: .NET sets the caption and reports what it actually settled on, so an empty or
// whitespace title comes back as a refusal instead of a blank bar.
document.getElementById('btn-title').addEventListener('click', () => {
	const requested = document.getElementById('title-input').value;
	callDotNet('SetWindowTitleAsync', requested)
		.then((applied) => writeLine(`caption is now "${applied}" (asked for "${requested}")`))
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
