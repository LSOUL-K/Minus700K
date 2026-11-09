# Contributing to Minus700K / 開発への参加

Minus700K is under development. Japanese and English reports, suggestions, and contributions are welcome. A stable release is not available yet. The source code will be added to this repository when it is ready; the build instructions below apply after that upload.

Minus700Kは開発中です。日本語・英語での不具合報告、改善提案、翻訳への協力を歓迎します。ソースコードは準備が整い次第、このリポジトリへ追加します。以下のビルド手順はソース追加後に利用できます。

## Reports and suggestions / 不具合・機能要望

Use the repository's **Issues → New issue** page and choose the bug report or feature request form. Search existing issues first. Include the version or commit, Windows version, steps to reproduce, and what you expected to happen.

**Issues → New issue**から、不具合報告または機能要望を選んでください。既存の報告を確認し、使用バージョン・Windowsのバージョン・再現手順・期待した動作を記入します。

Do not attach account credentials, tokens, `config.json`, recordings, screenshots of private material, or entire diagnostic folders. If an error excerpt is useful, remove personal details before sharing it. You do not need to publish your email address.

認証情報・トークン・`config.json`・録音・個人情報を含む画像・診断フォルダー全体は添付しないでください。エラーの抜粋を共有するときは個人情報を取り除いてください。メールアドレスの公開は不要です。

## Code changes / コードの変更

1. Discuss substantial changes in an issue before starting.
2. Fork the repository and work on a separate branch.
3. Keep each pull request focused on one change. Explain the problem, resulting behavior, and validation performed.
4. Update the relevant documentation and translations when behavior or visible text changes.

大きな変更は先にIssueで相談し、Forkしたリポジトリの作業用ブランチで進めてください。Pull requestには変更理由・変更後の動作・実施した確認を記載します。挙動や表示文言を変更した場合は、関連する説明と翻訳も更新してください。

The build uses the Windows .NET Framework compiler. See [README.md](README.md) for dependencies and setup. Stop the running app before replacing its executable, or build a separate verification executable:

```powershell
.\build.ps1 -OutputPath bin\Minus700K.check.exe
.\tests\verify.ps1 -Executable bin\Minus700K.check.exe
```

Run checks relevant to the change. Documentation-only changes need a review of text and links, not an application rebuild. Do not run live AI tests unless you intend to use your own account's usage allowance.

変更に関係する確認を行ってください。文書だけの変更では、文章とリンクの確認で十分です。実際のAI接続テストは、自分のアカウントの利用枠を使う意図がある場合にのみ実施してください。

## Translations / 翻訳

See [LOCALIZATION.md](LOCALIZATION.md). Preserve resource keys and format placeholders. English and Simplified Chinese are incomplete; do not describe partial translations as fully supported languages.

[LOCALIZATION.md](LOCALIZATION.md)に従い、リソースのキーと書式の引数を維持してください。英語・簡体字中国語は準備中です。

## Working together / やり取りについて

Be respectful, focus feedback on the work, and avoid sharing anyone's private information. Contributions to the project are provided under its [MIT license](LICENSE). Submit only material you have the right to contribute.

相手を尊重し、作業内容について具体的にやり取りしてください。貢献内容にはプロジェクトの[MITライセンス](LICENSE)が適用されます。提供する権利のある内容だけを提出してください。
