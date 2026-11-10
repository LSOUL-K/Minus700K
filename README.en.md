# Minus700K

Minus700K is a Windows tool that lets you ask AI questions by voice and hear the answers aloud while keeping your game or work app open. You can attach a screenshot of the current window and ask questions such as “What does this error mean?” or “What should I do next?”

[日本語](README.md) | [Contributing](CONTRIBUTING.md)

## Requirements

- Windows 10/11 (x64) and .NET Framework 4.8
- A microphone, speakers or headphones, and a Windows text-to-speech voice (including a Japanese voice if you use the app in Japanese)
- A ChatGPT account that supports connecting to this app, and an internet connection

No OpenAI API key is required. The AI service is subject to your account's terms and usage limits.

## Getting started

1. Open **`Minus700K-Setup.exe`** and follow the installer. Speech recognition files are included, and Codex CLI is installed automatically if needed.
2. Start **Minus700K** from the Windows Start menu. Its icon appears in the notification area.
3. Double-click that icon to open **Settings**, then select **Connection → Continue with ChatGPT**. Sign in and grant permission in your browser to complete the connection.

Windows may ask for administrator approval when installing a required speech recognition component.

## Basic controls

Keep the game or app you want to ask about in the foreground, then **hold Alt+Q to speak and release it to send your question**. The answer is read aloud, and you can continue working while you wait.

| Shortcut | Action |
|---|---|
| **Alt+Q** | Record while held; release to send |
| **Alt+E** | Cancel recording, answer processing, or speech playback |
| **Alt+R** | Read the last answer aloud again |

You can change these keys in Settings.

Screen capture is ON by default. The app captures the foreground window once when recording starts. If you do not want to send an image, turn off **General → Use screen capture**. If capture fails, your question continues without an image.

To quit, right-click the notification area icon and select **Exit**.

## Settings

Double-click the notification area icon to open Settings.

| Tab | Main settings |
|---|---|
| General | Display language, keyboard shortcuts, screen capture |
| Audio | Microphone, speakers, reading voice, speech rate and volume, sound effects and waiting sounds |
| Search | Web search behavior, automatic retry, search keywords |
| Custom instructions | Preferences such as answer length and writing style |
| History and storage | Saving and referring to conversations, debug logging |
| Connection | ChatGPT sign-in, answer model, usage, sign-out |
| Advanced | Advanced settings such as recording duration, answer timeout, and speech recognition language |

After making changes, select **Save and apply** or press **Ctrl+S**. If recording or answer processing is in progress, wait for it to finish or cancel with Alt+E before saving.

To choose an answer model, select **Connection → Load available models**. If you need to sign in again, authenticate using **Continue with ChatGPT**.

Japanese and English are supported. You can change the language under **General → Application language**.

## Useful tips

- **Search the web:** The default mode is **When needed**. Say “Search the web for this” or include a web search keyword in your question to search. The app automatically searches again if needed. Say “Don't search the web” to skip searching for that question.
- **Ask follow-up questions:** Say “Tell me more” or “What did I ask two questions ago?” to refer to past conversations in this app. Both **Save conversations** and **Refer to previous questions** must be ON under **History and storage**.
- **Adjust the answers:** Enter preferences such as “Keep answers short” or “Explain for a beginner” under **Custom instructions**.

## Data handling

Recorded audio is transcribed on your PC. The question text and information about the foreground app are sent to the AI service, along with an image if screen capture is ON.

- **Conversation saving is OFF by default.** Turn it on under **History and storage** to save conversations in a dedicated Minus700K location on your PC. Saved conversations are used as needed based on your questions and are not deleted automatically. **The app does not add conversations to your ChatGPT chat history or retrieve past conversations from ChatGPT.**
- **Debug logging is OFF by default.** When enabled, it saves full data on your PC, including recorded audio, images, and diagnostic records. You can view these files under **History and storage → Open saved files folder**. They are not deleted automatically.

## How this app came to be

This app was born from losing $700K in the AI stock crash. Any support is deeply appreciated. Elon Musk, please send help.

```text
$0 / $700,000 (0.0000%)
```

- **Ko-fi:** [Donate on Ko-fi](https://ko-fi.com/lsoul)
- **Bitcoin:** [Donate with your wallet](bitcoin:bc1q4kvzh4dthqpcrerzh6deww9zw7m220cd05ufj5?message=Minus700K%20donations)

Bitcoin address (for copying):
```text
bc1q4kvzh4dthqpcrerzh6deww9zw7m220cd05ufj5
```

You can also help by reporting reproducible bugs, suggesting improvements, and contributing translations. Japanese and English contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

The app is free, open-source software available under the [MIT license](LICENSE). For dependency licenses, see the [third-party notices](THIRD-PARTY-NOTICES.md).

Minus700K is not an official OpenAI application.
