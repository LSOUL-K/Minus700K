# Contributing to Minus700K / 開発への参加

Japanese and English reports, suggestions, and contributions are welcome.
日本語・英語での不具合報告、改善提案、翻訳への協力を歓迎します。

## Reports and suggestions / 不具合・機能要望

Use the repository's **Issues → New issue** page and choose the bug report or feature request form. Search existing issues first. Include the version or commit, Windows version, steps to reproduce, and what you expected to happen.
**Issues → New issue**から、不具合報告または機能要望を選んでください。既存の報告を確認し、使用バージョン・Windowsのバージョン・再現手順・期待した動作を記入します。

Do not attach account credentials, tokens, `config.json`, recordings, screenshots of private material, or entire diagnostic folders. If an error excerpt is useful, remove personal details before sharing it. You do not need to publish your email address.
認証情報・トークン・`config.json`・録音・個人情報を含む画像・診断フォルダー全体は添付しないでください。エラーの抜粋を共有するときは個人情報を取り除いてください。メールアドレスの公開は不要です。

## Build from source / ソースからのビルド

Use Windows x64 with .NET Framework 4.8. The build uses the compiler included with .NET Framework; a separate .NET SDK is not required. Run the commands from a clean checkout in a local folder.
Windows x64と.NET Framework 4.8を使用します。追加の.NET SDKは不要です。ローカルの新しい作業フォルダーに取得したソースのルートで実行してください。

```powershell
.\build.ps1
```

This compiles `bin\Minus700K.exe` without downloading dependencies or signing in. To prepare a runnable developer installation, run:
このコマンドは依存ソフトの取得やログインを行わず、`bin\Minus700K.exe`を作成します。実際に動作させる開発環境の準備には、次のコマンドを使用します。

```powershell
.\setup.ps1
```

Setup creates local settings from `config.example.json`, prepares the official Codex CLI when needed, downloads the verified speech-recognition engine and model, and builds the app. It requires internet access. Sign in with your own account through the application's Connection tab.
セットアップは設定例から個人用設定を作成し、必要なら公式Codex CLIを導入したうえで、検証済みの音声認識エンジン・モデルの取得とビルドを行います。インターネット接続が必要です。アプリの「接続」タブから、自分のアカウントでログインしてください。

```powershell
.\build-installer.ps1 # dist\Minus700K-Setup.exe and its SHA256 file
.\package-source.ps1  # source archive in dist
```

The installer build prepares its compiler and upstream components if needed. Do not replace a running app's executable; close it first or pass `-OutputPath bin\Minus700K.check.exe` to `build.ps1`.
インストーラー作成時は、必要に応じてコンパイラーと依存コンポーネントを取得します。実行中のアプリを上書きせず、先に終了するか、`build.ps1`に`-OutputPath bin\Minus700K.check.exe`を指定してください。

## Validation / 動作確認

```powershell
.\tests\verify.ps1
```

Use an isolated development installation. The default checks include local diagnostics and Codex transport/storage checks; some checks require the installed Codex CLI. `-IncludeCodex` adds live AI requests that use your own account's allowance. Run the installer-specific tests only in an environment intended for installation testing. Documentation-only changes need text and link checks.
独立した開発用環境で確認してください。通常のテストにはローカル診断とCodexの接続・保存先の確認が含まれ、一部にはCodex CLIの導入が必要です。`-IncludeCodex`は自分のアカウントの利用枠を使う実接続テストを追加します。インストーラー用テストは導入確認用の環境で実施してください。文書だけの変更では、文章とリンクの確認で十分です。

## Translations / 翻訳

See [LOCALIZATION.md](LOCALIZATION.md). Japanese and English are supported. Preserve resource keys and format placeholders, and update both catalogs when adding or changing text. English speech-command rules have their own regression tests; do not treat these regular expressions as ordinary prose translations.
[LOCALIZATION.md](LOCALIZATION.md)に従い、リソースのキーと書式の引数を維持してください。対応言語は日本語・英語です。文言を追加・変更する場合は両方のカタログを更新し、英語の音声指示判定ルールは専用テストでも検証してください。
