# RimLex – UI Text Localizer / Codex Brief (v4, rc6a)

> **Scope**: 現行実装（0.10.0‑rc6a）の仕様と機能だけを記載。将来計画（辞書管理フロー等）は本書に含めません。
> **Targets**: RimWorld 1.5 / 1.6, .NET Framework 4.7.2, Harmony 2.x

---

## 1) これは何をする？

RimLex は **ゲーム画面に実際に描画された英語UI**テキストを検出・収集し、**TSV辞書**に基づいて **実行時に日本語へ置換**するランタイム型ローカライザです（Apply‑at‑Runtime）。翻訳キーやXMLが無いModでも、ラベル／ボタン／ツールチップ／右クリック／Gizmo等に自然な日本語を適用できます。

---

## 2) 対応UIとパッチ方式（Harmony）

**低侵襲・フェイルオープン**。対象署名が見つからなければ WARN を出して素通りします。

* **Label**: `Widgets.Label(Rect, string)`、`Listing_Standard.Label(string, …)`
* **Button**: `Widgets.ButtonText(Rect, string, …)`
* **Tooltip**: `TooltipHandler.TipRegion(Rect, string)`／`TipRegion(Rect, TipSignal)`
* **Context**: `FloatMenuOption::.ctor(string, Action, …)`
* **Gizmo**: `Command.LabelCap.get`
* **Slider**: `Listing_Standard.Slider(float,float,float)` → **安全化のみ（収集しない）**

> いずれも **Prefix/Postfix フック**で描画直前の文字列を捕捉します。辞書ヒット時は即時置換（`ApplyDictAtRuntime=true`）、ヒットしなければ未訳として収集します。

---

## 3) 文字列正規化と辞書照合

**照合は O(1)**（辞書を `Dictionary<string, string>` にロード）。

* **改行**: 入力側の `\n`／`/n`／`/ n` を **実改行 `\n` に正規化**し、外部ファイルへは **`/n` 一行表記**で書き出し。
* **数字の形状キー**: 収集時に数値を `#` に置換した **shape** を並行管理。辞書のキーに `#` を含む場合は、描画時に **左から順に元値を復元**します。
* **空白**: 連続空白を1つに圧縮（軽量）。

> 例: `Valid range: # - #` → `有効範囲：#～#`（実描画時に `#` へ数値を復元）。

---

## 4) ノイズ除外と画面除外（暴走防止）

* **文字列ノイズ**: 空行／URL／数字・記号だけ／GUID／CJK支配（日本語UI）等を排除。
* **動的スパム抑止**: 数値を `#` 化した **同形状の短時間連打**を検出して一時ミュート（スライダー等の暴走回避）。
* **画面除外**:

  * 既定BL: `EditWindow_Log`, `Page_ModsConfig`, `Dialog_DebugTables`。
  * RimLex **自身の設定画面**は **常時自己参照除外**。
  * INI から **Whitelist/Blacklist** を拡張可。

> 除外イベントはまとめてログ集計（デバウンス）。

---

## 5) 収集と出力（Per‑Mod／集約）

**未訳のみを取りこぼしなく一度だけ**追記します（セッション内重複抑止＋短時間デバウンス）。すべての書換は **アトミックI/O** で実施。

```
Export/
├─ _All/
│   ├─ texts_en_aggregate.txt   … 英語原文の集約（軽量追記／「整理」で再構築、先頭に rebuilt_at）
│   ├─ untranslated.txt         … 未訳のみ（/n 一行）
│   └─ grouped_by_mod.txt       … Mod別ビュー
├─ PerMod/
│   └─ <ModName>/
│       ├─ texts_en.txt         … /n 一行の原文
│       └─ strings_en.tsv       … `timestamp_utc\tmod\tsource\tscope\ttext`
└─ SelfTest/                    … 動作確認用ダミー出力
```

**辞書**: `Dict/strings_ja.tsv`（`英語<TAB>日本語`／`/n` 表記／`#` 形状キー対応）。

**モード**: `ExportMode = TextOnly | Full | Both`（既定 Both）

**集約デバウンス**: `AggregateDebounceMs`（既定 250ms）。`PauseAggregate=true` で一時停止。

---

## 6) 辞書ファイル（TSV）の書式

* 形式: `英語原文<TAB>日本語訳`（タブ区切り／BOMなしUTF‑8推奨）。
* 改行: `/n` で一行化（照合は `/n` と `\n` を同一視）。
* 数字: `#` 形状キーを使用可（描画時に復元）。
* 固有名詞（Mod名／URL／作者名／バージョン等）は **翻訳しない**。

> 監視を有効化（`WatchDict=true`）しておくと **保存だけでホットリロード**されます。

---

## 7) 設定UI（Mod 設定 → RimLex）

**上部**: 直近セッションと通算の **置換／収集／除外／I/O** カウンタ。

**トグル・項目**

* 即時反映（ApplyDictAtRuntime）
* Per‑Mod出力（ExportPerMod）
* 集約出力（EmitAggregate）
* ExportMode（TextOnly / Full / Both）
* PerModSubdir（`PerMod`/`Mods`/`ByMod`）
* MinLength／ExcludePatterns 表示
* PauseAggregate／AggregateDebounceMs 調整
* 辞書監視（WatchDict）／除外画面のログ出力（LogExcludedScreens）／デバッグHUD（予告枠）

**操作ボタン**

* **未訳一覧を生成**（`_All/untranslated.txt` など再構築。件数をトースト表示）
* **整理（MOD別/集約）を生成**（集約とMod別ビューを再構築）
* **未訳からTSV雛形を作成**（`Dict/strings_ja_template.tsv`）
* **フォルダを開く**：`_All`／`PerMod`／`Export`
* **収集をリセット**（`Export/` を再生成。**辞書は残す**）
* **セッションキャッシュをクリア**（メモリ上の二重書き防止を消去）
* **ログをクリア**（`RimLex.log` を空に）
* **辞書を再読み込み**（行数をトースト表示）
* **動作確認（SelfTest）**（`Export/SelfTest/ok.txt` を出力）

---

## 8) INI（`RimLex.ini`）主要キー（抜粋）

```ini
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

> `DictPath`/`ExportRoot` などは `%ModDir%` 変数で解決されます。UIから保存すると相対化されます。

---

## 9) ログと可観測性

* 起動直後：`Patched:` ラインが **6系統** 出ていれば主要フックが有効。
* 操作時：未訳／集約／Mod別の **生成件数**を INFO で記録。
* ランタイム：除外画面の統計（`LogExcludedScreens=true` で集計）。
* 設定UI：**置換／収集／除外／I/O** のセッション＆通算メトリクスを常時表示。

---

## 10) ビルドと参照解決（プロジェクト）

* **Target**: .NET Framework **4.7.2**
* **出力**: `Assemblies/RimLex.dll`
* **Harmony**: ゲーム側 `0Harmony.dll` を優先利用（無い場合は `HarmonyDir`）。
* **外部化 Props**: `RimWorldDir.props` で `GameManaged`（および任意の `HarmonyDir`）を指定。
* **参照ガード**:

  * `EnsureGameManaged`：`Assembly-CSharp.dll` が見つからない場合 **ビルドエラー**
  * `ShowConfig`：解決された実パスをビルド前に出力

> 参照パスは **Propsが最優先**。固定 HintPath を置いても Props 指定が上書きします。

---

## 11) リポジトリ構成（推奨）

```
<RimLex>/
├─ About/ About.xml
├─ Assemblies/ RimLex.dll
├─ Dict/ strings_ja.tsv, strings_ja_template.tsv, _conflicts.tsv, _stale.tsv, Index/
├─ Export/ _All/, PerMod/, SelfTest/
├─ Source/
│   ├─ RimLex.csproj
│   ├─ RimWorldDir.props
│   ├─ Config.cs
│   ├─ MenuUI.cs
│   ├─ ModInitializer.cs
│   ├─ NoiseFilter.cs
│   ├─ Normalizer.cs
│   ├─ TranslatorHub.cs
│   ├─ UIPatches.cs
│   └─ UTFBuilder.cs
└─ RimLex.ini
```

---

## 12) ソース責務（要約）

* **ModInitializer.cs**: Mod本体。INIロード、ログ初期化、NoiseFilter初期化、TranslatorHub I/O初期化、Harmonyパッチ適用、設定UI実装。
* **MenuUI.cs**: プレースホルダ（設定UIは ModInitializer で実装）。
* **Config.cs**: INI 読み書き、辞書監視（WatchDict）、パス解決、ExcludePatternsなど既定値保持。
* **Normalizer.cs**: 空白圧縮など軽量正規化（内部の改行正規化／`#` 形状化は TranslatorHub 側が担当）。
* **NoiseFilter.cs**: 文字列ノイズ除外、画面BL/WH、自己参照除外、動的スパム抑止、除外ログ集計。
* **TranslatorHub.cs**: 辞書ロード／ホットリロード、O(1)置換、未訳収集（Per‑Mod／Current／_All 集約）、雛形生成、アトミックI/O、メトリクス。
* **UIPatches.cs**: UIフック群（Label／Button／Tooltip／FloatMenu／Gizmo／Slider保護）。
* **UTFBuilder.cs**: 予約（現行は未使用）。

---

## 13) 既知の制限とフェイルセーフ

* **スライダーの収集は無効**（動的数値で暴走しやすいため）。
* 特殊 OnGUI／テクスチャ文字など **文字列でない描画**は対象外のことがあります。
* 置換失敗・パッチ失敗時は **原文のまま表示**して継続（UIを壊さない）。
* 一部UIは **初回フレームで英語→次フレームで日本語**に変わる場合があります。

---

## 14) クイックスタート

1. Mod を有効化してゲーム起動 → 設定UIを開く（起動直後ログに `Patched:` が6系統あることを確認）。
2. 英語UI（ツールチップ等）にマウスオン → 収集が伸びる。
3. **未訳一覧を生成** → `Export/_All/untranslated.txt` 更新。
4. `Dict/strings_ja.tsv` に **`英語<TAB>日本語`** で追記（`/n` と `#` に注意）。
5. 保存（`WatchDict=true` なら即時反映／もしくは **辞書を再読み込み**）。

---

### Versioning

* **実装**: 0.10.0‑rc6a（収集安定／`/n` 正規化／アトミックI/O）
* **文書**: v4（本書）

---

> **Filename suggestion**: `RimLex_Codex_Brief_v4.md`
