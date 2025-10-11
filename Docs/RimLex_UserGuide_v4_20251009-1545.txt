RimLex ユーザーガイド **v4**
更新: **2025-10-09 15:45 (JST)**
対象: RimWorld 1.5 / 1.6, .NET Framework 4.7.2, Harmony 2.x
実装版: **0.10.0-rc6a（収集安定版・/n 正規化・アトミックI/O）**
**Based on SHA256 (prev v1: RimLex_UserGuide_v1_20251009-1420.txt)**: `830d6f3036f4f02a4e91bde2f3d550dc6ffd708174f5e9a8a313471b4add58d9`

> 【プロジェクトの理念と保存目的】
> 本書は単なる説明書ではなく、**開発環境そのものを保存する装置**。将来、別の開発者やAIが引き継いでも**方針・設計思想・運用構造**が変わらず継続できるよう、章立て・用語・手順の粒度を維持し、**情報を端折らない**。
> 目的は「コードを残す」ではなく、**開発の文脈ごと未来に渡す**こと。

---

## ■ できること

* 画面に**実際に描画された英語UI**を検出・収集（Label / Button / Tooltip / FloatMenu / Gizmo）。
* **辞書（`Dict/strings_ja.tsv`）**にある原文は**その場で日本語へ置換**（ApplyDictAtRuntime）。
* **未訳一覧**から辞書を育てる運用に最適化（/n 一行・テンプレ生成・再読込ホットリロード）。
* **/n 正規化**：辞書／収集で `/n` と `\n` を**同一視**して照合。
* **数字の形状化**：収集時に数字を `#` として扱い、訳側 `#` に**元の数値を復元**。
* **画面BL/WH**・**自己参照除外**：RimLex 設定画面や開発者ログは恒久で対象外。
* **低侵襲 Harmony**：失敗時は WARN を出して**素通り**（UIを壊さない）。
* **アトミックI/O＋デバウンス**：収集・整理・雛形の出力は安全に更新。

> 対応UIの代表：`Widgets.Label` / `Listing_Standard.Label` / `Widgets.ButtonText` / `TooltipHandler.TipRegion(string/TipSignal)` / `FloatMenuOption` / `Command.LabelCap`。
> `Listing_Standard.Slider` は安全化のみ（**収集対象外**）。

---

## ■ 最短手順（はじめての翻訳）

1. **RimLex を有効化** → ゲーム起動。
2. **Mod 設定 → RimLex** を開く。起動直後に `RimLex.log` に **`Patched:` が6系統**出ていればOK。
3. ゲーム画面で英語UIに**マウスを乗せる**（ツールチップ等）→ 収集が走る。
4. 設定画面で **「未訳一覧を生成」** → `_All/untranslated.txt` が更新される（件数トーストが出る）。
5. `Dict/strings_ja.tsv` を開き、`英語原文<TAB>日本語訳` を**追記**（/n・`#`に注意）。
6. **保存** → `WatchDict=true` なら**即時反映**。無効なら **「辞書を再読み込み」**。
7. 画面へ戻ると置換が適用され、次回以降は**既訳を自動除外**して未訳だけが伸びる。

> ヒント：辞書は **タブ区切り**。改行は `/n` で一行化。数字は `#` テンプレを活用。

---

## ■ ファイル構成（出力と辞書）

```
Export/
├─ _All/
│   ├─ texts_en_aggregate.txt   … 英語原文の集約。軽量追記＋「整理」で再構築（先頭に rebuilt_at）
│   ├─ untranslated.txt         … 未訳のみ（/n 一行）
│   └─ grouped_by_mod.txt       … Mod別サマリ（見やすい配列）
├─ PerMod/
│   └─ <ModName>/
│       ├─ texts_en.txt         … /n 一行（収集）
│       └─ strings_en.tsv       … timestamp_utc,mod,source,scope,text
└─ SelfTest/                    … 動作確認用ダミー出力

Dict/
├─ strings_ja.tsv               … 統合辞書（英語<TAB>日本語, /n・#対応）
├─ strings_ja_template.tsv      … 未訳からの雛形（UIボタンで生成）
└─ Index/
    └─ en_provenance.tsv        … （v4計画）恒久由来Index：`key_shape → {mods, first_seen, last_seen, count}`
```

---

## ■ 設定画面（ボタンと表示）

* **辞書を再読み込み** … `Dict/strings_ja.tsv` を即時反映（`WatchDict=true`でも明示的に可能）
* **未訳一覧を生成** … `_All/untranslated.txt` を再構築（処理件数をトースト表示）
* **整理** … `texts_en_aggregate.txt` と `grouped_by_mod.txt` を再構築
* **雛形を作成** … `Dict/strings_ja_template.tsv` を未訳から作る
* **フォルダを開く（3種）** … `Dict/` / `Export/PerMod/` / `Export/_All/` をエクスプローラで開く
* **収集をリセット** … `Export/` を再生成（**辞書は消えない**／**Index は恒久（v4計画）**）
* **ログを開く** … `RimLex.log` を開く
* **SelfTest** … `Export/SelfTest/ok.txt` を出力して I/O を確認
* 画面上部：**置換 / 収集 / 除外 / I/O** の**セッション**／**合計**カウンタが自然に増減（放置で暴走しない）

> v4準備中のUI（実装段階で順次有効化）：
> **「PerMod から由来Indexを再構築」**（`Dict/Index/en_provenance.tsv` の再集計）、
> **「MOD別辞書を書き出し」**（`Dict/PerMod/<Mod>/strings_ja.tsv` の生成）、
> **「MOD別辞書から統合を復元」**（衝突は `Dict/_conflicts.tsv` へ）。

---

## ■ 辞書の書き方

* **基本形**：`英語原文<TAB>日本語訳`（タブ区切り）。
* **改行**：辞書・収集とも `/n` を使って**一行**に表記。照合時は `/n` と `\n` を同一視。
* **数字の形状キー**：数字は `#` に置換した形で辞書登録可能。描画時に**左から順に元値を復元**。

  * 例）`Valid range: # - #` → `有効範囲：#～#`
* **句読点**：英語のピリオドは日本語では「。」に統一。
* **固定ルール**：Mod名・URL・作者名・バージョン番号など**固有名詞は訳さない**。
* **英語UI専用**：日本語支配率が高い行は収集しない（ノイズ扱い）。

> 例：
> `Enable Vaginal Drip Graphics	膣ドリップ表示を有効化。`
> `Valid range: # - #	有効範囲：#～#`
> `Press /n to confirm	/n で確定。`

---

## ■ よくある質問（FAQ）

**Q1. どのModでも翻訳されますか？**
A. **英語で描画されるUIテキスト**なら基本OK。テクスチャ化された文字や特殊 OnGUI は対象外の場合があります。

**Q2. スライダーの値など動的な数字は？**
A. 数値スパム抑止のため、スライダーの**収集はしません**。固定ラベルは置換されます。

**Q3. 既に日本語UIの画面に影響しますか？**
A. いいえ。**英語UI専用**です。日本語支配行は収集対象外です。

**Q4. 衝突（同英語→異訳）が心配。**
A. v4では `Dict/_conflicts.tsv` に出力予定（**計画**）。現行でもログで WARN を確認し、辞書を整理してください。

**Q5. 置換されない箇所があります。**
A. `/n` と `#` の扱い、画面BL/WH、自己参照除外を確認。`_All/untranslated.txt` に原文があれば、辞書追記で解決します。

---

## ■ 上級者向け（RimLex.ini の主なキー）

```
ApplyDictAtRuntime=true
ExportPerMod=true
EmitAggregate=true
ExportMode=Both
DictPath=%ModDir%/Dict/strings_ja.tsv
LogPath=%ModDir%/RimLex.log
ExportRoot=%ModDir%/Export
PerModSubdir=PerMod
MinLength=2
ExcludePatterns=^\s*$|^https?://|^[0-9]+$|^[-–—…\.]+$|^[A-F0-9]{8}(-[A-F0-9]{4}){3}-[A-F0-9]{12}$
IncludedWindows=
ExcludedWindows=EditWindow_Log,Page_ModsConfig,Dialog_DebugTables
PauseAggregate=false
AggregateDebounceMs=250
WatchDict=true
ShowDebugHUD=false
LogExcludedScreens=true
```

* `WatchDict=true`：辞書ファイルの**ホットリロード**。
* `ExcludePatterns`：URL/空行/純数字/記号列/GUID などの**ノイズ**を除外。
* `IncludedWindows` / `ExcludedWindows`：**画面ホワイト/ブラックリスト**（自己参照は恒久除外）。

---

## ■ 30秒点検リスト（健全性チェック）

* 起動直後 `RimLex.log` に **`Patched:` が6系統**出ている。
* **「未訳一覧を生成」**と**「整理」**が完走し、**件数トースト**が表示される。
* ツールチップにマウスを乗せると **`_All/texts_en_aggregate.txt` が伸びる**（/n 一行）。
* **フォルダを開く**（`Dict/` / `Export/PerMod/` / `Export/_All/`）が正しい場所を開く。
* `PerModSubdir` を変更→保存→再起動後もフォルダ構成が**反映**される。
* 画面上部の **置換/収集/除外/I/O カウンタ**が自然に増減し、**放置で暴走しない**。
* RimLex 設定画面や `EditWindow_Log` 等は**常に対象外**（自己参照ループ対策）。

---

## ■ トラブルシュート

**症状**：置換されない／一部だけ英語。

* **確認**：`_All/untranslated.txt` に原文があるか → あれば辞書追記。
* **/n**：辞書・原文の改行表記が不一致 → `/n` で統一。
* **#**：数字を含む場合は**形状キー**を検討。
* **画面除外**：BL/WH の設定に引っかかっていないか。
* **ログ**：`RimLex.log` の WARN/ERROR を確認（`Patched:` ライン数も目安）。

**症状**：未訳が暴走して増える。

* **原因**：スライダー等の**動的値**。
* **対策**：スライダーの収集は仕様で**無効**。UIで値を固定して再生成。

**症状**：I/O エラー／ファイルが壊れる。

* **対策**：アトミック書換の `.tmp` 残骸があれば削除。`Export/` を **「収集リセット」** で再生成。辞書は消えません。

**報告時に添付**：`RimLex.log` / `Player.log` / `_All/untranslated.txt` / 再現手順 / 使用Mod一覧。

---

## ■ 既知の仕様・制限

* **特殊 OnGUI／テクスチャ文字**は収集・置換できない場合あり。
* UIの独自キャッシュで、**初回は英語→次フレームで日本語**に変わることがある。
* ゲーム更新でパッチ対象署名が変わると、一時的に未パッチ（`Patched:` が6未満）になる場合がある。

---

## ■ 更新履歴（ユーザー視点の重要点）

* **rc6a**：/n 正規化の強化、数字 `#` 形状キー、画面BL/WH・自己参照除外、アトミックI/O・デバウンス、未訳テンプレ生成。
* **v4 ドキュメント**：辞書管理フェーズ（由来Index / MOD別辞書分割・統合 / 衝突・ソート・陳腐化）の運用を**明文化**。UI には順次反映予定。

---

## ■ 今後の予定（次フェーズのご案内）

* **恒久由来インデックス**（`Dict/Index/en_provenance.tsv`）：`key_shape（/n＋数字#） → {mods, first_seen, last_seen, count}` を累積（**Export リセット非対象**）。
* **MOD別辞書の分割 / 統合**：`Dict/PerMod/<Mod>/strings_ja.tsv` を書き出し、逆変換で `Dict/strings_ja.tsv` を再構築（**衝突は `Dict/_conflicts.tsv`＋ログ WARN**）。
* **辞書ソート / 重複キー検出 / 陳腐化レポート**：`Alpha / ByModAlpha` ソート、同英語→異訳のレポート、`last_seen` 閾値での陳腐化検出。
* （研究）ドロップダウン候補の**事前収集**、Gizmo説明文の網羅。

---

**問い合わせ・バグ報告**
`RimLex.log`、`Export/_All/untranslated.txt`、再現手順、使用Modリスト、ゲームバージョン（1.5/1.6）を添えてください。

**-- END OF USER GUIDE (v4) --**
