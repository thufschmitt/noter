# Longhand for Windows

A native Windows prototype of Longhand: one endless ruled notebook page. Write with a pen. When you pause for about two seconds, or tap **Ask**, Claude writes its answer in blue handwriting on the lines just below your ink, and anything further down moves to make room.

It uses Windows Ink (`InkCanvas`), so pen strokes are drawn by the system on its low-latency path, with pressure, palm rejection and the pen's eraser end built in.

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- Visual Studio 2022 17.14 or later, or Visual Studio 2026, with the **Universal Windows Platform tools** workload. This is UWP on modern .NET 10.
- An Anthropic API key

## Build and run

1. Open `Longhand/Longhand.csproj` in Visual Studio.
2. Pick your platform (x64 or ARM64) and press F5. Visual Studio restores the Anthropic NuGet package, deploys the app and starts it.
3. Click the gear button and paste your Anthropic API key. It's stored in Windows Credential Manager, not in any file.

If Visual Studio won't load the project file, create a new project from the **UWP Blank App** template (the .NET one), name it `Longhand`, then:
- Add the `Anthropic` NuGet package.
- Copy `MainPage.xaml`, `MainPage.xaml.cs` and the `Services` folder into the new project.
- Add `<PublishAot>false</PublishAot>` to the new project's csproj.

## Installing and updating

Every push to `main` builds signed packages and publishes them as a GitHub Release. A small GitHub Pages site next to it holds the `.appinstaller` files that tell Windows which version is newest; they point at the packages on the Release.

One-time setup of the repository: Settings → Pages → Source: **GitHub Actions**.

One-time setup of each PC:

1. Trust the certificate, in an administrator terminal: `certutil -addstore TrustedPeople Longhand.cer`
2. Open the `.appinstaller` link for your PC (x64 or ARM64) and choose Install. They are `https://thufschmitt.github.io/noter/Longhand-x64.appinstaller` and `https://thufschmitt.github.io/noter/Longhand-arm64.appinstaller`.

After that, starting the app from the Start menu or a tile checks for a newer version first and asks to install it before the app opens. (A taskbar or desktop shortcut skips the prompt and updates silently instead.) Windows also checks every few hours in the background. The update only shows up once the release workflow has finished, which takes a few minutes. Installing the `.msix` from a Release by hand works too, but doesn't update itself.

## Using it

| | |
|---|---|
| Pen or mouse | Writes |
| Touch | Scrolls (the hand button switches fingers to writing) |
| Pause about 2 s | Claude answers. The small bar in the Ask button fills while it waits. |
| Ask / Ctrl+Enter | Answers right away. While an answer is coming, the button reads Stop. |
| Eraser button or the pen's eraser end | Erases whole strokes |
| Ctrl+Z | Undoes the last stroke or erase |
| Conversations button | Opens the list of conversations: start a new one, switch, rename, or delete (tap twice). Ctrl+N starts a new one. |

Each conversation is its own page. They save themselves to the app's local folder, and the app reopens in the one you were last in. Conversations are fully separate: Claude only sees the open one. Titles come from the first thing Claude heard, and you can rename them. A conversation you never write in disappears when you move away from it, and switching while Claude is answering stops that answer. The page from before conversations existed becomes the first one.

## How it talks to Claude

- Only the ink written since the last answer is sent, as a PNG of black ink on white (`Services/InkSnapshot.cs`).
- Answers are written at the end of the page, not under the ink that prompted them. That lets you annotate an old answer: when new ink overlaps earlier replies, the picture also shows those replies in blue and the writing they answered in grey, with your new ink in black on top, and Claude is told the black ink is an annotation.
- Earlier turns go along as text: Claude's one-line transcription of what you wrote, plus its reply. Claude keeps the context without old images being re-sent.
- Model: Haiku 5.5 by default, so replies come back quickly; the gear dialog also offers Sonnet 5.5 and Opus 5.5 (Opus and Sonnet are asked for low effort to keep the wait short). Replies are streamed onto the page as it's written (`Services/ClaudeService.cs`).
- Claude can search the web and fetch pages (Anthropic's server-side tools, up to 5 uses of each per answer). While one runs, the answer line shows what it is doing in blue ink.
- Symbols: the gear dialog has a Symbols section for your own shortcuts, such as an envelope that means "add what I wrote to my Todoist inbox". A symbol is a name and what it means, plus an optional example drawing that you draw once on a small pad (it's saved as a small picture). There are no built-in symbols. On every Ask, the list goes to Claude along with your handwriting; when the writing contains one of them, Claude does what it means (using its tools, such as a connected Todoist), and the one-line transcription in the history notes it in brackets, like `[envelope symbol]`. Claude is told to act only when it is reasonably sure it saw the symbol, and otherwise to say what it thinks it saw and ask. Up to 20 symbols. Symbols are saved as you edit them, in the app's local folder, and apply to every conversation.
- MCP registry: the connected-apps dialog can search the official MCP registry (registry.modelcontextprotocol.io) and add any server found there that is hosted online. Servers that you would have to run on your own PC, that sit on a private network, or that need a header other than `Authorization: Bearer` are left out (and counted), because Claude reaches MCP servers from Anthropic's side. When a server is added the app asks it how it lets you in: nothing (usable straight away), a browser sign-in (the OAuth flow above), or an access token that you get from the service and paste once ("Add token"; stored in Credential Manager). A server whose address has blanks, such as an account or region, asks for them first. Anyone can publish to the registry, so only add servers you trust.
- Nextcloud calendar: the link button's dialog has a Nextcloud section. Type your server's address and Connect: your browser opens Nextcloud's own sign-in page (Login Flow v2), you approve it, and Nextcloud hands the app an app password, so nothing is typed or pasted. The app password is stored in Windows Credential Manager. While connected, Claude has a read-only `calendar_events` tool: the app asks your Nextcloud over CalDAV (repeating events are expanded by the server) and hands Claude the agenda for the range it asked for, in your time zone. All your event calendars are readable, and only the events Claude asks for are sent to Anthropic. Disconnect revokes the app password on the server. It only reads: nothing is created, changed or deleted.
- Drawings: Claude can draw by writing an SVG in its reply, which the page shows under the words, in the blue ink (it follows the light and dark themes) and scaled to the ruled lines. Drawings are checked first (no scripts, external images or oversized files), saved with the conversation, and when you write on top of one the picture sent to Claude includes it, like its words. In the history Claude only sees a short "[I drew: …]" note, not the SVG.
- Connected apps: the link button opens a list of MCP servers (a preset for Todoist, or any https address). Sign in opens your browser; the sign-in is a standard OAuth flow that the app runs itself (the server is found automatically, the app registers itself with it, and tokens are renewed in the background). The browser comes back to the app through `callback.html` on the update site and the `longhand://` address, so the first sign-in only works once the update site is deployed. Anthropic's servers call the MCP servers, so they must be reachable from the internet. Tokens are stored in Windows Credential Manager next to the API key.
- Server-side refusal fallback is on (`fallbacks: "default"`). If a safety check declines a request, the API answers it with a suitable fallback model in the same stream instead of returning nothing.

## Web version

The same notebook runs in a browser, from a server on Linux (or anywhere .NET 10 runs). The server runs the shared C# code
(`Longhand.Core`: asking Claude, replies, drawings, conversations, symbols, saving); the browser shows the page on an HTML canvas,
with the handwriting font, and does the two things only a browser can: measure text and draw the picture Claude reads.

```
export ANTHROPIC_API_KEY=sk-ant-…          # or add the key with the gear button in the page
dotnet run --project Longhand.Web           # then open http://127.0.0.1:5080
# on Nix: nix shell nixpkgs#dotnet-sdk_10 -c dotnet run --project Longhand.Web
```

- Options: `--urls http://127.0.0.1:5080` (or `LONGHAND_URLS`) and `--data <folder>` (or `LONGHAND_DATA`; the default is `~/.local/share/longhand`). Conversations, symbols, the chosen model and a key typed into the page are kept there; the key file is readable only by you.
- Use one tab at a time: each open tab runs its own copy of the page and saves it, so two tabs on the same conversation overwrite each other.
- **There is no sign-in.** It listens on loopback by default and refuses requests addressed to another name (DNS rebinding) and writes from another site. To use it from another device, tunnel to it (`ssh -L 5080:127.0.0.1:5080 host`) rather than exposing it.
- Write with a pen or mouse; fingers scroll unless you turn on the hand button. The pen's eraser end works, as does the eraser button. Replies, drawings, annotation of earlier replies, undo, conversations and symbols work as in the Windows app, and the pause-to-ask is the same.
- Not in the web version yet: connected apps (MCP), the registry search and the Nextcloud calendar. The server could run them as they are, so they are mostly UI to add.
- The code is shared like this: `PageSession` (in `Longhand.Core`) is the page: strokes waiting to be answered, notes, asking, undo, saving, conversations. It talks to its screen through `IPageView` and to storage through `IPageData`. The Windows `MainPage` is one implementation (XAML and Windows Ink), `WebConnection` the other (JSON over a WebSocket to `wwwroot/app.js`). A conversation saved by one isn't read by the other: the web version keeps ink as JSON points rather than ISF.
- Checks: `dotnet run --project tools/WebCheck/WebCheck.csproj` plays a browser over a real WebSocket against a fake API; `tools/BrowserCheck/run.sh` drives headless Chrome over the DevTools protocol (needs node 22).

## What was checked, and what wasn't

This was written on Linux, where UWP can't be built: the Windows build on CI is what compiles the XAML page, and what it does on a real device is only known when someone runs it. Checked here:

- `tools/CoreCheck` compiles the Claude service and reply parser against the real Anthropic package and runs parser checks. With `LONGHAND_FAKE_API` set, it also streams a reply from a local fake of the API, which confirms the request shape and the streaming loop.
- `tools/WinRtCheck` compiles the ink snapshot, credential storage and page saving code against the Windows SDK API definitions.
- `tools/CoreCheck` also plays the notebook page (`PageSession`) against a fake screen and a fake API: asking, annotating, undo, conversations, saving, switching mid-answer.
- `tools/WebCheck` starts the web server and plays a browser over a real WebSocket; `tools/BrowserCheck` drives headless Chrome through the real page. Both run on CI.

Expect a round of small fixes on the first Windows build. The most likely spots:
- The `.csproj` properties for UWP on .NET 9
- `TextNudge` in `MainPage.xaml.cs`, which lines Claude's text up with the ruled lines

The handwriting font is **Ink Free**, which comes with Windows 10 1803 and later. If it's missing, the app falls back to Segoe Print.
