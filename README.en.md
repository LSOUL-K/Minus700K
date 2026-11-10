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

The current development target is Windows 10/11 x64 with .NET Framework 4.8, a microphone, and a suitable installed Windows speech voice. The app uses the official Codex CLI and the user's own ChatGPT sign-in and eligible plan. The app is free; access to the AI service and its usage limits are governed separately.

Minus700K supports Japanese and English, including the interface, notifications, AI instructions, and spoken search and history commands. Changing **General → Language** also sets **Advanced → Show advanced settings → Speech recognition language** to the selected language and **Audio → Voice** to **Match the application language**. The app selects an installed voice for that language, falling back to the Windows default voice if none is available. You can choose a different speech recognition language or voice afterward. Select **Save and apply** to apply the changes; closing without saving leaves the active settings unchanged.

Custom instructions, application instructions, and search keywords share one saved configuration across languages. When you change the app language, any of these fields that still match a Japanese or English default switch to the selected language's default, including the text shown in Settings. Edited text and empty fields are kept as written. Examples of English commands include “Search the web for this,” “Don't search the web,” “Tell me more,” and “What did I ask two questions ago?” See [the localization guide](LOCALIZATION.md) for development details.

## Getting started

1. Open **`Minus700K-Setup.exe`** and follow the installer. Speech recognition files are included, and Codex CLI is installed automatically if needed.
2. Start **Minus700K** from the Windows Start menu. Its icon appears in the notification area.
3. Double-click that icon to open **Settings**, then select **Connection → Continue with ChatGPT**. Sign in and grant permission in your browser to complete the connection.

Windows may ask for administrator approval when installing a required speech recognition component.

This is a development installer; a stable public release is not available yet.

## Privacy and control

- A question starts when you press the recording shortcut.
- Screen capture can be disabled in Settings.
- Questions and enabled screen captures are sent to the configured AI service. This is not a fully offline assistant.
- Conversation saving is off by default and can be enabled in Settings. Previous questions are not consulted while saving is off. Diagnostic storage is also configurable. Do not include recordings, screenshots, account details, or access tokens in public bug reports.

## Support development
This app was born from losing $700K in the AI stock crash. Any support is deeply appreciated. Elon Musk, please send help.
- **Ko-fi:** [Donate on Ko-fi](https://ko-fi.com/lsoul)
- **Bitcoin:** [Donate with your wallet](bitcoin:bc1q4kvzh4dthqpcrerzh6deww9zw7m220cd05ufj5?message=Minus700K%20donations)
Bitcoin address (for copying):
```text
bc1q4kvzh4dthqpcrerzh6deww9zw7m220cd05ufj5
```

You can also help by reporting reproducible bugs, suggesting improvements, and contributing translations. Japanese and English contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

The application source and original assets are licensed under [MIT](LICENSE). Dependencies and external services have their own terms; see [third-party notices](THIRD-PARTY-NOTICES.md).

Minus700K is an independent project and is not an official OpenAI application.
