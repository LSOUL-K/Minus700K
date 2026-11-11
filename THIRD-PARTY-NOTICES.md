# Third-party components

Minus700K source and original app assets are provided under the MIT license in LICENSE.

Minus700K uses the installed official Codex CLI app-server (Apache-2.0) with an
application-owned Sign in with ChatGPT OAuth token and the user's ChatGPT plan.
Codex is not bundled with Minus700K. If no usable installation is found, the
Minus700K installer (or the developer `setup.ps1`) runs OpenAI's signed installer from
https://chatgpt.com/codex/install.ps1 to install the complete Codex CLI runtime.
Its installation and license:
https://developers.openai.com/codex/cli
https://github.com/openai/codex/blob/main/LICENSE

The OAuth implementation uses Windows/.NET libraries, with no client secret,
embedded account, API key, or third-party authentication SDK.
ChatGPT and Codex are OpenAI products. Minus700K is an independent application,
not an official OpenAI application. OpenAI service access is governed separately
by OpenAI's terms and the Sign in with ChatGPT offering; this source license does
not grant service access or remove plan limits.

The Windows installer includes the official Windows CPU build of whisper.cpp.
The developer scripts download it from:
https://github.com/ggml-org/whisper.cpp/releases/tag/b5454

whisper.cpp is licensed under MIT. The upstream license is available at:
https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE

On first launch, Minus700K downloads the multilingual small-q5_1 Whisper model
if it is not already present. The model is reused after application updates.
It is downloaded from:
https://huggingface.co/ggerganov/whisper.cpp

Whisper model and code licensing: https://github.com/openai/whisper/blob/main/LICENSE

The installer includes upstream license texts in its `licenses` folder.
It is built with Inno Setup (https://jrsoftware.org/), whose license is also included.

When needed, the installer downloads Microsoft's signed Visual C++ v14 x64
Redistributable from https://aka.ms/vc14/vc_redist.x64.exe for whisper.cpp.
This Microsoft component has its own license and is not bundled in the source package.

Windows libraries (DXGI, D3D11, WinMM, System.Speech, Windows Forms) are supplied by the operating system.
