# Minus700K

**Ask AI by voice without leaving your game or app.**

[日本語・詳しい使い方](README.md) | [Contributing](CONTRIBUTING.md)

**Under development — not yet a stable release.** Features, settings, and installation steps may change. The GitHub repository is being prepared for release; the source code and Windows download will be added when they are ready.

Minus700K is a free Windows tray application that lets you ask questions about the app you are using and hear the answers aloud. Keep your game or work window in the foreground while asking for help.

## How it works

1. Hold **Alt+Q** and speak. When screen capture is enabled, Minus700K captures the foreground window once.
2. Release **Alt+Q** to send your question.
3. Listen to the spoken answer while continuing your activity.

Use **Alt+E** to cancel recording, processing, or playback, and **Alt+R** to replay the last answer. These are the default shortcuts; they can be changed in Settings.

Screen capture is optional. Minus700K provides answers and guidance; it does not operate the target app for you.

## Availability and languages

The current development target is Windows x64 with .NET Framework 4.8, a microphone, and a suitable installed Windows speech voice. The app uses the official Codex CLI and the user's own ChatGPT sign-in and eligible plan. The app is free; access to the AI service and its usage limits are governed separately.

Japanese is the primary development language. English and Simplified Chinese translations are in progress; untranslated text may fall back to Japanese. Choosing an interface language does not automatically change speech-recognition settings. See [the localization guide](LOCALIZATION.md) for the current scope.

Developer setup instructions are in the [Japanese README](README.md). A stable Windows download is not available yet.

## Privacy and control

- A question starts when you press the recording shortcut.
- Screen capture can be disabled in Settings.
- Questions and enabled screen captures are sent to the configured AI service. This is not a fully offline assistant.
- Conversation and diagnostic storage are configurable. Do not include recordings, screenshots, account details, or access tokens in public bug reports.

## Support development

Minus700K is free to use. Optional donations are planned to support development and maintenance; they will not be required to use the app. A donation link will be added once the receiving account is ready.

You can also help by reporting reproducible bugs, suggesting improvements, and contributing translations. Japanese and English contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

The application source and original assets are licensed under [MIT](LICENSE). Dependencies and external services have their own terms; see [third-party notices](THIRD-PARTY-NOTICES.md).

Minus700K is an independent project and is not an official OpenAI application.
