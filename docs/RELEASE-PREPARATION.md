# Minus700KのGitHub公開準備

## 現在の段階

このリポジトリでは、ソースコード、翻訳データ、必要なアイコン、ビルド・インストーラー作成スクリプト、テスト、説明書を管理します。作業中の個人設定・認証情報・録音・画像・会話ログ・バックアップは収録しません。ビルド手順は[開発参加ガイド](../CONTRIBUTING.md)を参照してください。

インストーラーの配布物とGitHub Releasesの登録は、ソースの登録とは別に行います。ソースリポジトリには作業フォルダーの既存EXE・ZIPや依存ソフトのキャッシュを入れません。

公開先：[LSOUL-K/Minus700K](https://github.com/LSOUL-K/Minus700K)。準備中はPrivate（非公開）で管理します。

## 用意したもの

- 日英の紹介文と使い方
- 既存のMITライセンスと第三者コンポーネントの説明
- 開発参加ガイド、日英の不具合報告・機能要望フォーム、Pull requestテンプレート
- ローカル設定、認証情報、録音・ログ・編集履歴などをGit管理から外す設定
- Windowsインストーラーの作成スクリプトと、設定画面からChatGPTに接続する導入手順
- [GitHub Sponsorsの紹介文案](SPONSORS-DRAFT.md)と[寄付ボタンの設定例](FUNDING.yml.example)

## GitHub画面で設定する項目

リポジトリ右側のAbout欄で説明文とTopics（検索用タグ）を編集できます。以下は設定候補です。

**Description**

Ask AI by voice without leaving your game or app. Free, open-source Windows voice assistant.

**Topics**

`windows` `voice-assistant` `speech-recognition` `text-to-speech` `chatgpt` `gaming` `productivity` `open-source`

URL欄は公式の案内サイトが用意できるまで空欄で構いません。現在の寄付先はREADMEに掲載したKo-fiとBitcoinの受け取り先です。GitHub Sponsorsの紹介文と設定例は、将来利用する場合の下書きです。

## 配布・一般公開前に行うこと

1. 最新の開発内容に合わせてREADME、英語の概要、動作要件を更新する。
2. ソースコード、翻訳データ、必要なアイコン、ビルド・セットアップ用スクリプト、テストが同じ版で揃っていることを確認する。
3. アップロードするファイル一覧を確認する。`config.example.json` は配布用、実際の `config.json` は個人設定。後者や認証情報・録音・画像・会話ログは含めない。`.gitignore` はブラウザーの手動アップロードには適用されないため、作業フォルダー全体をそのままアップロードしない。
4. 配布予定の状態を別フォルダーで確認し、導入と基本動作を検証する。
5. `build-installer.ps1` で `dist\Minus700K-Setup.exe` を作成し、`tests\verify-installer.ps1` と別環境で導入を確認する。設定画面の「接続」からChatGPTにログインできることも確認し、変更点をまとめる。ソースZIPは `package-source.ps1` で別途作成する。
6. READMEの寄付先を確認する。GitHub Sponsorsを追加する場合は、受け取り設定が整ってからそのリンクを有効にする。
7. 実演動画やスクリーンショットを用意する。READMEに実在する画像・動画・ダウンロード先だけを掲載する。
8. 一般公開する内容を確認し、SettingsでPublicへ変更する。初版をReleasesに登録し、告知する。

インストーラー定義には現在`0.1.0-dev`のバージョン表記が残っています。配布する版の番号を確認してから、バージョンタグとReleasesの登録を行います。ダウンロードリンクは実際の配布物のURLができてから追加します。

## GitHubの基本

- **Code**：説明書とファイル。最初にREADMEが表示されます。
- **Issues**：不具合や機能要望。フォームは「New issue」で選べます。
- **Pull requests**：変更の提案と確認。
- **Releases**：完成した配布物と更新内容。
- **Settings**：公開範囲などの設定。

参考：[リポジトリ作成](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository)、[Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)、[Issueフォーム](https://docs.github.com/en/communities/using-templates-to-encourage-useful-issues-and-pull-requests/syntax-for-issue-forms)。
