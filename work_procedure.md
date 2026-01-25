# MCP Server 実装 作業手順書

## 概要
MarkdownPointer を MCP Server として配布できるようにする。PowerShell.MCP と同様の Proxy パターンを採用し、NuGet + MCP Registry で配布する。

## アーキテクチャ
```
MCP Client (Claude Desktop, VS Code, etc.)
    ↓ stdio (MCP Protocol)
MarkdownPointer.Mcp.exe  ← 新規実装
    ↓ Named Pipe (MarkdownPointer_Pipe)
MarkdownPointer.exe      ← 既存（変更なし）
```

## 提供する MCP Tools
| Tool 名 | 説明 | パラメータ |
|---------|------|-----------|
| `show_markdown` | Markdown ファイルを開く | `path`, `line?` |
| `show_markdown_content` | Markdown テキストを表示 | `content`, `title?` |
| `get_tabs` | 開いているタブ一覧を取得 | なし |

## 作業手順

### Phase 1: 基本実装 ✅
1. MarkdownPointer.Mcp プロジェクト作成
2. MCP SDK (ModelContextProtocol) 導入
3. Named Pipe クライアント実装
4. MCP Tools 実装
5. ビルド確認

### Phase 2: テスト
1. MCP Inspector で接続確認
2. 各ツールの動作確認
3. MarkdownPointer 自動起動の確認
4. エラーハンドリングの確認

### Phase 3: NuGet 配布準備
1. csproj に NuGet Tool 設定追加
2. WPF アプリ（MarkdownPointer.exe）のバンドル設定
3. server.json 作成（MCP Registry 用）
4. README 更新

### Phase 4: リリース
1. dotnet publish でパッケージ作成
2. ローカルテスト（dnx コマンド）
3. NuGet.org へパブリッシュ
4. MCP Registry へ登録

## 品質基準
- MCP Inspector で全ツールが正常に動作すること
- MarkdownPointer 未起動時に自動起動できること
- エラー時に適切なメッセージを返すこと


## ビルドコマンド
```powershell
C:\MyProj\MarkdownPointer\Build-Deploy.ps1
```
- プロセスの停止、ビルド、デプロイを自動で行う

## コミットポリシー
- テスト通過 AND ユーザーレビュー承認後にコミット

## 進捗更新ルール
- 作業が進むたびに work_progress.md を即時更新

## 学習事項
- dnx: .NET 10 で追加された npx 相当のコマンド。NuGet パッケージを即時実行可能
- NuGet.org が MCP Server を公式サポート（mcpserver パッケージタイプ）
- MCP Registry はメタレジストリ。実コードは npm/PyPI/NuGet 等で配布