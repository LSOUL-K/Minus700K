# Third-party components

Minus700K source and original app assets are provided under the MIT license in LICENSE.

Minus700K uses the installed official Codex CLI app-server (Apache-2.0) with an
application-owned Sign in with ChatGPT OAuth token and the user's ChatGPT plan.
Codex is not redistributed with the source package. Its installation and license:
https://developers.openai.com/codex/cli
https://github.com/openai/codex/blob/main/LICENSE

The OAuth implementation uses Windows/.NET libraries, with no client secret,
embedded account, API key, or third-party authentication SDK.
ChatGPT and Codex are OpenAI products. Minus700K is an independent application,
not an official OpenAI application. OpenAI service access is governed separately
by OpenAI's terms and the Sign in with ChatGPT offering; this source license does
not grant service access or remove plan limits.

`setup.ps1` downloads the official Windows CPU build of whisper.cpp from:
https://github.com/ggml-org/whisper.cpp/releases/tag/b5454

whisper.cpp is licensed under MIT. The upstream license is available at:
https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE

The multilingual small-q5_1 Whisper model is downloaded from:
https://huggingface.co/ggerganov/whisper.cpp

Whisper model and code licensing: https://github.com/openai/whisper/blob/main/LICENSE

Windows libraries (DXGI, D3D11, WinMM, System.Speech, Windows Forms) are supplied by the operating system.
