# 行番号テスト自動化プラン

## 背景

mdp のクリック時に報告される行番号が実際のファイル行番号とずれている。
コードブロック内外の両方でずれが発生している。

## 現在わかっていること

- `LineTrackingRenderer.cs:125` の `sourceLine` 計算に問題がある
- `obj.Line` は Markdig の 0-indexed 行番号（fenced code block の開きフェンスを指す）
- 現在の式: `obj.Line + 1 + (obj is FencedCodeBlock ? 1 : 0) + i`
- コードブロック以外の行でもずれが発生 → コードブロック固有の問題ではない可能性
- `LineTrackingRenderer.cs` にはコードブロック以外のレンダラ（QuoteBlock 等）もあり、それぞれ `data-line` を設定している
- JS 側（`GetElementContent.js`, `PointingEventHandlers.js`）は `data-line` をそのまま使うだけ

## テスト方針

### 1. テスト用 Markdown ファイルを用意

さまざまな要素を含むファイルを作成:
- 見出し（h1〜h3）
- 段落テキスト
- Fenced code block（言語指定あり/なし）
- インデントコードブロック
- リスト（箇条書き/番号付き）
- 引用ブロック
- テーブル
- 空行
- 連続するコードブロック
- ネストした引用内のコードブロック

### 2. 期待値ファイルを生成

各テスト用 Markdown から「この行のテキストは L{n}」という期待値マッピングを生成。
ファイルの各行を読み、内容と行番号（1-indexed）のペアを作る。

### 3. レンダリング結果を検証

Markdig + LineTrackingRenderer で HTML をレンダリングし、生成された `data-line` 属性を抽出。
各 `data-line` の値と、対応するテキストの実際のファイル行番号を比較。

### 4. テスト実装

- `CheckExtensions` プロジェクトまたは新しいテストプロジェクトに実装
- Markdig のパイプラインを構築し、LineTrackingRenderer でレンダリング
- HTML から `data-line` 属性を正規表現またはパーサーで抽出
- 抽出したテキスト + 行番号を期待値と照合
- ずれがあればテスト失敗として報告

### 5. テスト対象ファイル

- 自作のテスト用 Markdown（上記の要素を網羅）
- README.md（SlackDrive, PowerShell.MCP 等の実プロジェクト）
- docs/ 配下の実ドキュメント

## 修正箇所の候補

- `MarkdownPointer/LineTrackingRenderer.cs` — コードブロック、引用ブロック等のレンダラ
- `CheckExtensions/LineTrackingRenderer.cs` — 同様の実装がある場合
