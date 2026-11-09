# Minus700KのGitHub公開準備

## 現在の段階

このリポジトリは公開準備用です。プログラム本体・実行ファイル・正式リリースはまだ登録していません。READMEの詳細な操作説明は開発版についてのものです。

公開先：[LSOUL-K/Minus700K](https://github.com/LSOUL-K/Minus700K)。準備中はPrivate（非公開）で管理します。

## 用意したもの

- 日英の紹介文と、開発中・正式リリース前であることの表示
- 既存のMITライセンスと第三者コンポーネントの説明
- 開発参加ガイド、日英の不具合報告・機能要望フォーム、Pull requestテンプレート
- ローカル設定、認証情報、録音・ログ・編集履歴などをGit管理から外す設定
- [GitHub Sponsorsの紹介文案](SPONSORS-DRAFT.md)と[寄付ボタンの設定例](FUNDING.yml.example)

## GitHub画面で設定する項目

リポジトリ右側のAbout欄で説明文とTopics（検索用タグ）を編集できます。以下は設定候補です。

**Description**

Ask AI by voice without leaving your game or app. Free Windows tool under development.

**Topics**

`windows` `voice-assistant` `speech-recognition` `text-to-speech` `chatgpt` `gaming` `productivity` `open-source`

URL欄は公式の案内サイトが用意できるまで空欄で構いません。現在、寄付の受付は未設定です。

## プログラム完成後に行うこと

1. 最新の開発内容に合わせてREADME、英語の概要、動作要件を更新する。
2. ソースコード、翻訳データ、必要なアイコン、ビルド・セットアップ用スクリプト、テストを追加する。
3. アップロードするファイル一覧を確認する。`config.example.json` は配布用、実際の `config.json` は個人設定。後者や認証情報・録音・画像・会話ログは含めない。`.gitignore` はブラウザーの手動アップロードには適用されないため、作業フォルダー全体をそのままアップロードしない。
4. 配布予定の状態を別フォルダーで確認し、導入と基本動作を検証する。
5. Windows用の配布物、導入手順、変更点を用意する。既存の `package-source.ps1` はソースZIPを作るもので、完成したインストーラーではない。
6. GitHub Sponsorsの受け取り設定が整ったら寄付リンクを有効にする。
7. 実演動画やスクリーンショットを用意する。READMEに実在する画像・動画・ダウンロード先だけを掲載する。
8. 一般公開する内容を確認し、SettingsでPublicへ変更する。初版をReleasesに登録し、告知する。

準備中はリリース済みと誤解されるバージョンタグやダウンロードボタンを作りません。正式版の公開日・バージョン番号は未定です。

## GitHubの基本

- **Code**：説明書とファイル。最初にREADMEが表示されます。
- **Issues**：不具合や機能要望。フォームは「New issue」で選べます。
- **Pull requests**：変更の提案と確認。
- **Releases**：完成した配布物と更新内容。
- **Settings**：公開範囲などの設定。

参考：[リポジトリ作成](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository)、[Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)、[Issueフォーム](https://docs.github.com/en/communities/using-templates-to-encourage-useful-issues-and-pull-requests/syntax-for-issue-forms)。
