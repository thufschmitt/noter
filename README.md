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

Every push to `main` builds signed packages and publishes them in two places: a GitHub Release, and a GitHub Pages site that lets Windows update the app by itself.

One-time setup of the repository: Settings → Pages → Source: **GitHub Actions**.

One-time setup of each PC:

1. Trust the certificate, in an administrator terminal: `certutil -addstore TrustedPeople Longhand.cer`
2. Open the `.appinstaller` link for your PC (x64 or ARM64) and choose Install. They are `https://thufschmitt.github.io/noter/Longhand-x64.appinstaller` and `https://thufschmitt.github.io/noter/Longhand-arm64.appinstaller`.

After that the app checks for a newer version each time it starts, and every few hours in the background. An update installs when the app is next closed. Installing the `.msix` from a Release by hand works too, but doesn't update itself.

## Using it

| | |
|---|---|
| Pen or mouse | Writes |
| Touch | Scrolls (the hand button switches fingers to writing) |
| Pause about 2 s | Claude answers. The small bar in the Ask button fills while it waits. |
| Ask / Ctrl+Enter | Answers right away. While an answer is coming, the button reads Stop. |
| Eraser button or the pen's eraser end | Erases whole strokes |
| Ctrl+Z | Undoes the last stroke or erase |
| Bin (click twice) | Clears the page |

The page saves itself to the app's local folder and is restored when you reopen the app.

## How it talks to Claude

- Only the ink written since the last answer is sent, as a PNG of black ink on white (`Services/InkSnapshot.cs`).
- Earlier turns go along as text: Claude's one-line transcription of what you wrote, plus its reply. Claude keeps the context without old images being re-sent.
- Model: `claude-opus-5-5` at low effort, which keeps the wait short for notebook-sized replies, streamed onto the page as it's written (`Services/ClaudeService.cs`).
- Claude can search the web and fetch pages (Anthropic's server-side tools, up to 5 uses of each per answer). While one runs, the answer line shows what it is doing in blue ink.
- Optional connected apps: the gear dialog takes remote MCP servers, one per line as `name https://address token`. Anthropic's servers call them, so they must be reachable from the internet, and the token is a bearer token you create yourself (an API key or personal access token; OAuth sign-in isn't supported). The list is stored in Windows Credential Manager next to the API key.
- Server-side refusal fallback is on (`fallbacks: "default"`). If a safety check declines a request, the API answers it with a suitable fallback model in the same stream instead of returning nothing.

## What was checked, and what wasn't

This was written on Linux, where UWP can't be built, so the XAML page has never been compiled or run. Checked:

- `tools/CoreCheck` compiles the Claude service and reply parser against the real Anthropic package and runs parser checks. With `LONGHAND_FAKE_API` set, it also streams a reply from a local fake of the API, which confirms the request shape and the streaming loop.
- `tools/WinRtCheck` compiles the ink snapshot, credential storage and page saving code against the Windows SDK API definitions.

Expect a round of small fixes on the first Windows build. The most likely spots:
- The `.csproj` properties for UWP on .NET 9
- `TextNudge` in `MainPage.xaml.cs`, which lines Claude's text up with the ruled lines

The handwriting font is **Ink Free**, which comes with Windows 10 1803 and later. If it's missing, the app falls back to Segoe Print.
