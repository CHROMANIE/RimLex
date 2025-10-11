RimLex – UI Text Localizer
ワークショップ公開用説明テキスト / 概要・特徴・導入方法・翻訳手順
作成: 2025-10-09 15:30 (JST)
対象: RimWorld 1.5 / 1.6, .NET Framework 4.7.2, Harmony 2.x
実装版: **0.10.0-rc6a**（収集安定版・/n正規化対応・アトミックI/O）
ドキュメント版: **v4**
**Based on SHA256 (prev v3: RimLex_ModDescription_20251009-1340.txt)**: `a9f463c2db847ec98345ab467ea055fc27dd17a71be87fba1540c43b7f001a89`

――――――――――――――――――――――――――――――――――――

【このModは何をする？】
RimLex は、**ゲーム画面に実際に描画された英語UI**をその場で収集し、**TSV辞書**にもとづいて**実行時に日本語へ置換**します。
翻訳キーやXMLを持たないMODでも、ボタン・ラベル・ツールチップ・右クリックメニュー・Gizmo などのUIテキストを**自然な日本語**にできます。

* 翻訳は「今見えている文字列」に対して即時適用（**ApplyDictAtRuntime**）。
* 未訳は**自動収集**してファイルに出力（Per-Mod と集約の両方）。
* **/n 正規化**と**数字の # 形状キー**に対応（辞書はテンプレ置換が可能）。
* **辞書ホットリロード**、**アトミック書き換え**、**デバウンスI/O**で堅牢に運用。
* RimLex **自身の設定画面は常時除外**（自己参照ループ対策）。

> このプロジェクトの文書（FunctionalSpec / Source / ModDescription / UserGuide）は**開発環境そのものを保存する装置**です。
> 「コードだけ」ではなく、**開発の文脈ごと未来に渡す**設計で運用しています。

――――――――――――――――――――――――――――――――――――

【主な特徴】

* **リアルタイム辞書置換**：英語→日本語を O(1) で即時置換。
* **未訳の自動収集**：Per-Mod と `_All` 集約へ /n 一行で追記。
* **Per-Mod 出力**：`Export/PerMod/<ModName>/` に英語原文を保存。
* **集約ビュー**：`Export/_All/` に未訳一覧・集約テキスト・Mod別グループを生成。
* **設定画面からワンボタン運用**：辞書再読込／未訳生成／整理／雛形作成／フォルダを開く。
* **安全第一**：失敗は素通り（UIを壊さない）、I/O はアトミック、過剰収集はデバウンス。
* **画面BL/WH**：`EditWindow_Log` / `Page_ModsConfig` / `Dialog_DebugTables` 等は既定ブラックリスト。
* **英語UI専用**：**日本語支配率が高い行は収集しません**（ノイズ除外）。

――――――――――――――――――――――――――――――――――――

【対応UI（代表例）】

* `Widgets.Label` / `Listing_Standard.Label`
* `Widgets.ButtonText`
* `TooltipHandler.TipRegion (string / TipSignal)`
* `FloatMenuOption`（右クリック）
* `Command.LabelCap`（Gizmo）
* `Listing_Standard.Slider`（安全化のみ：**収集対象外**）

※ オーバーロード差異には“ゆるい署名探索”で耐性。見つからない場合はログに WARN を出し、UIは**フェイルオープン**（原文のまま表示）。

――――――――――――――――――――――――――――――――――――

【インストール / ロード順】

1. サブスク／導入後、通常どおり有効化。
2. Harmony 2.x が導入済みなら、**ロード順はどこでも可**（パッチは安全・低侵襲）。
3. ゲーム起動後、**Mod 設定 → RimLex** の画面から動作確認（下記参照）。

> 起動直後、`RimLex.log` に **`Patched:` が6系統**出ていれば OK。

――――――――――――――――――――――――――――――――――――

【辞書（TSV）の書式とコツ】

* 形式：`英語原文<TAB>日本語訳`（**タブ区切り**）。
* **/n 正規化**：辞書や収集で `/n` と `\n` を**同一視**。TXT/TSVは `/n` で一行化。
* **数字の # 形状キー**：例 `Valid range: # - #` → `有効範囲：#～#`。描画時に元の数値を左から復元。
* 文末ピリオドは日本語では「。」へ。
* 固有名詞／Mod名／URL／作者名／バージョン番号は翻訳しない。
* **英語UI専用**：日本語支配の行（CJK率が高い）は収集対象外。

辞書の場所（既定）：`Dict/strings_ja.tsv`
雛形生成：設定画面 → **「雛形を作成」** から `Dict/strings_ja_template.tsv` を出力。

――――――――――――――――――――――――――――――――――――

【出力フォルダ構成（実行時生成）】

```
Export/
├─ _All/
│   ├─ texts_en_aggregate.txt   … 英語原文の集約（軽量追記＋整理で再構築；先頭に rebuilt_at）
│   ├─ untranslated.txt         … 未訳のみ（/n 一行）
│   └─ grouped_by_mod.txt       … Mod別のまとめ
├─ PerMod/
│   └─ <ModName>/
│       ├─ texts_en.txt         … /n 一行の収集
│       └─ strings_en.tsv       … timestamp_utc,mod,source,scope,text
└─ SelfTest/                    … 自己テスト用ダミー出力
Dict/
├─ strings_ja.tsv               … 統合辞書（英語<TAB>日本語）
├─ strings_ja_template.tsv      … 未訳からの雛形
└─ Index/
    └─ en_provenance.tsv        … （v4計画）恒久由来Index
```

――――――――――――――――――――――――――――――――――――

【設定メニュー（Mod 設定 → RimLex）】

* **辞書を再読み込み**：`Dict/strings_ja.tsv` を即時反映（`WatchDict=true`時は変更監視も）。
* **未訳一覧を生成**：`_All/untranslated.txt` を再構築（件数トースト表示）。
* **整理**：`texts_en_aggregate.txt` と `grouped_by_mod.txt` を再構築。
* **雛形を作成**：`Dict/strings_ja_template.tsv` を出力。
* **フォルダを開く（3種）**：`Dict/`, `Export/PerMod/`, `Export/_All/`。
* **収集をリセット**：`Export/` を再生成（**辞書は消えません**）。
* **ログを開く**：`RimLex.log` を開く。
* **SelfTest**：`Export/SelfTest/ok.txt` を出力して I/O を検証。
* 上部カウンタ：**置換 / 収集 / 除外 / I/O**（セッション／合計）が自然に増減（放置で暴走しない）。

――――――――――――――――――――――――――――――――――――

【翻訳作業フロー（最短手順）】

1. ゲームで英語UIを開く（ツールチップにマウスを乗せると `_All/texts_en_aggregate.txt` が伸びる）。
2. 設定画面で **「未訳一覧を生成」** → `_All/untranslated.txt` を更新。
3. `Dict/strings_ja.tsv` に**タブ区切り**で訳を追記（`#`形状キー・/n表記に注意）。
4. 保存すると**即時反映**（`WatchDict=true`／または「辞書を再読み込み」）。
5. 次回以降、未訳収集は**既訳を自動除外**。

――――――――――――――――――――――――――――――――――――

【安全性・互換性・パフォーマンス】

* **Harmony パッチは低侵襲**：失敗時は WARN を出して**素通り**。
* **I/O はアトミック**：`.tmp` に出力→`Move` で置換。
* **デバウンス**：短時間の大量I/Oや同型（#化後）の連打を抑制。
* **互換性**：RimWorld 1.5 / 1.6 系で動作。
* **既定ブラックリスト**：RimLex設定画面／`EditWindow_Log`／`Page_ModsConfig`／`Dialog_DebugTables` は収集・置換の対象外。

――――――――――――――――――――――――――――――――――――

【FAQ】
**Q. どのModにも効きますか？**
A. **英語UIテキスト**であれば、翻訳キーやXMLが無くても反映できます。特殊なカスタム描画で文字列を生成している場合は収集できないことがあります。

**Q. スライダーの値など動的な数字は？**
A. 数値スパム抑止のため、スライダーの**収集は行いません**。固定文言は置換されます。

**Q. 日本語UI（既に翻訳済の画面）にも働きますか？**
A. いいえ。**英語UI専用**です。日本語支配率が高い行は収集対象外です。

**Q. 競合はありますか？**
A. 低侵襲な Prefix/Postfix で実装しており、競合は最小です。問題があれば `RimLex.log` と `Player.log` を添付して報告してください。

**Q. 置換が効かない／一部だけ英語のまま**
A. `/n` と `#` の形状キー、ホワイト/ブラックリスト、自己参照除外に該当していないか確認してください。`_All/untranslated.txt` に原文が出ていれば辞書追記で解決します。

――――――――――――――――――――――――――――――――――――

【既知の制限】

* 特殊な OnGUI 実装や、テクスチャ化された文字など**文字列ではない描画**は収集・置換できません。
* UIの**独自キャッシュ**や**頻繁な再生成**を行うModでは、初回のみ英語→次フレームから日本語になることがあります。
* 署名が将来のゲーム更新で変化した場合、該当箇所が一時的に未パッチとなる可能性があります（ログに `Patched:` が 6 未満）。

――――――――――――――――――――――――――――――――――――

【出力ファイル一覧（再掲）】

* `Export/_All/texts_en_aggregate.txt`（軽量追記＋整理で再構築；先頭に `rebuilt_at`）
* `Export/_All/untranslated.txt`（**未訳のみ**／/n 一行）
* `Export/_All/grouped_by_mod.txt`（Mod別集約）
* `Export/PerMod/<Mod>/texts_en.txt`（/n 一行）
* `Export/PerMod/<Mod>/strings_en.tsv`（`timestamp_utc mod source scope text`）
* `Dict/strings_ja.tsv`（統合辞書）／`Dict/strings_ja_template.tsv`（雛形）

――――――――――――――――――――――――――――――――――――

【ログとサポート】

* 起動時に `RimLex.log` を確認（**`Patched:` 6系統**が目安）。
* 不具合報告は **RimLex.log / Player.log**、可能なら **`Export/_All/untranslated.txt`** とスクリーンショットを添付してください。
* 再現手順・使用Modリスト・ゲームバージョンがあると解析が早くなります。

――――――――――――――――――――――――――――――――――――

【今後（v4：辞書管理フェーズ）】

* **恒久由来インデックス**：`Dict/Index/en_provenance.tsv`（`key_shape` → `{mods, first_seen, last_seen, count}` を累積。**Export のリセットでは消さない**）
* **MOD別辞書の分割/統合**：`Dict/PerMod/<Mod>/strings_ja.tsv` を自動生成／逆変換で `Dict/strings_ja.tsv` を再構築（**衝突は `Dict/_conflicts.tsv` とログ WARN**）。
* **品質と整頓**：重複キー検出（同英語→異訳）、辞書ソート（Alpha / ByModAlpha）、**陳腐化レポート**（IndexやPerModに一度も出てない／`last_seen` が古い）。

――――――――――――――――――――――――――――――――――――

【クレジット / ライセンス】

* 開発：**RimLex Project（えーいち＋GPT）** / Author（About.xml）：**COR+**
* フレームワーク：Harmony 2.x
* ライセンス：ワークショップページ記載に準拠（ソース/配布ポリシーは同ページ参照）
