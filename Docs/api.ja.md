# API

[English](api.md)

exe が 127.0.0.1 で出す API。画面が使う。
処理の中身は、[開発の手引き](development.ja.md)にある。

概要が 1 行で済まない呼び出しは、表の下の見出しに、本文（リクエストの本文）・レスポンス・エラーを書く。

## 目次

- [イベント](#イベント)
- [ジョブ](#ジョブ)
- [プロジェクト](#プロジェクト)
- [地図表示とタイル](#地図表示とタイル)
- [道の編集](#道の編集)
- [POI](#poi)
- [スタイル](#スタイル)
- [スタイルのプレビュー](#スタイルのプレビュー)
- [書き出し](#書き出し)
- [ゲーム](#ゲーム)
- [アプリの設定とダイアログ](#アプリの設定とダイアログ)

## イベント

`/api/events` の SSE で、次のものを送る。

| イベント | 中身 |
|---|---|
| `job` | ジョブのスナップショットの全体。1 秒に 4 回まで |
| `jobLog` | ログの 1 行ごと |
| `gameUse` | ゲームを手で使っているもの。変わるたびと、つないだとき |
| `project` | 開いているプロジェクト。変わるたびに、全部のタブへ |
| `settings` | 最近の一覧 |
| `precheck` | 事前確認（`POST /api/game/precheck`） |

`gameUse` は、[開発の手引き](development.ja.md#ゲームを使うのは-1-つずつ)の「ゲームを使うのは 1
つずつ」を参照。

## ジョブ

進み具合は、`/api/events` の SSE でも出す（`job`、`jobLog`）。

| 呼び出し | 概要 |
|---|---|
| `POST /api/jobs` | 実行を始める |
| `GET /api/jobs/current` | 動いている（か最後の）実行のスナップショット。一度もなければ 204 |
| `POST /api/jobs/current/stop` | 実行を止める |
| `PUT /api/jobs/current/workers` | 並列数を変える |
| `GET /api/jobs/current/units` | ステップごとの単位 |

### `POST /api/jobs`

- 本文: `{ "project": "<パス>", "workers": 12, "scale": [qx, qy], "recalibrate": false }`
  必要なのは、`project` だけ。
- レスポンス: 202 とスナップショット。
- 409 `RUNNING` / `BUSY`
- 409 `GAME_CHECKING` / `GAME_STARTING`: ゲームでの作業のある実行で、事前確認かリソースの起動が、
  ゲームを使っている間。
  実行のフォルダは、残さない。
- 404 / 400: プロジェクトの誤り。

### `POST /api/jobs/current/stop`

- 本文: `{ "mode": "boundary" }` か `"now"`。
- レスポンス: スナップショット。409 `NO_JOB`。

### `PUT /api/jobs/current/workers`

- 本文: `{ "workers": 7 }`
- レスポンス: 見積もりを出し直したスナップショット。409 `NO_JOB`。

### スナップショット

`JobSnapshot` は、次のものを持つ。

- 状態（running、stopping、stopped、done、failed）。
- 並列数の上限。
- 動いているスレッドの数と、終わり次第止めるスレッドの数。
- ステップごとの、単位の数（全部・済み・失敗・打ち切り）と、残り時間。
  待っているステップは、計画の値。
  動いているステップは、済んだ単位の平均を、スレッドの数で割った値。
- 作業中の単位と、その段階。
- 失敗。
- exe の CPU の割合。
- 空きと全体のメモリ。
- 読んでいるもの。

### `GET /api/jobs/current/units`

ステップごとに、見つけた残りの単位と、そのうち済み・失敗・打ち切りのものを返す。
地図のブロックの色分けに使う。

ステップが単位を数えている間は、`preparing` になる。先に縮尺の補正などを測るときも。

## プロジェクト

| 呼び出し | 概要 |
|---|---|
| `GET /api/project` | 開いているプロジェクト |
| `POST /api/project/open` | プロジェクトを開く |
| `POST /api/project/new` | プロジェクトを作る |
| `POST /api/project/close` | プロジェクトを閉じる |
| `PATCH /api/project` | プロジェクトの値を変える |
| `GET /api/project/blocks` | プロジェクトの枠の全ブロック |
| `POST /api/project/retake` | 再取得の印を付ける・外す |
| `GET /api/project/plan?workers=` | やることの表 |
| `GET /api/project/checks` | 出力する地図に必要な前提条件（✓ / ✗ / まだ確認できない） |
| `POST /api/project/postals/fetch` | 郵便番号データを今すぐ取得する |
| `GET /api/projects/recent`、`POST /api/projects/recent/remove` | 最近のプロジェクトと名前 |

### `GET /api/project`

開いているプロジェクトを返す。なければ 204。次の項目を含む。

- `ownStyles`: 地図に使える、このプロジェクトの自分のスタイル。id と名前 1 つ。
- `frame`: プロジェクトの地図の枠。
  - `bx0`・`by0`: 最初のブロックの列と行。左か上にセルを足すと、マイナスになる。
  - `cols`・`rows`
  - `west`・`north`・`east`・`south`: 端（m）。
- `frameNeeded`: 範囲のブロックを枠の中に収めるのに必要な、辺ごとの最小のセル数。
  `top`・`bottom`・`left`・`right`。
- `rangeOutside`: 標準の枠の外にある、範囲のブロックの数。
- `rangePresets`: 範囲のプリセットごとに、次の項目。
  - `id`
  - `land`・`water`: 陸と水域のブロック数。
  - `missingLand`・`missingWater`: 範囲にまだないブロックの数（追加した後の区分で）。
  - `missingOutside`: そのうち、プロジェクトの枠の外の数。
  - `needs`: プリセットを収めるのに必要な、標準の枠からの辺ごとのセル数。
  - `west`・`north`・`east`・`south`: ブロックの端（m）。

### `POST /api/project/open`

- 本文: `{ "path": "<ファイル>" }`
- レスポンス: プロジェクト。404 / 400。

### `POST /api/project/new`

- 本文: `{ "path", "name", "workFolder", "preset", "satellite", "atlas", "roadmap", "language" }`
- レスポンス: プロジェクト。409 `EXISTS`。
- 道路の編集は、同梱の編集のコピーにする。
  まとまりの名前は、`language`（画面の言語）の名前にする。

### `PATCH /api/project`

本文には、次の項目のうち、変えるものだけを入れる。

```
{
  "name", "satellite",
  "atlas": { "enabled", "styles": ["<スタイルの id>", ...], "languages": ["en", "ja"] },
  "minimapMap", "minimapOutside", "roadmap", "heightQuality",
  "parallel": 12,
  "serverPreset",
  "range": { "include": [...], "exclude": [...], "reset": true, "preset": "cayoPerico" },
  "stopResources": { "list": [...], "preset": false },
  "serverResources": [...],
  "console": { "host", "port" },
  "postals": "<URL かファイル。\"\" は既定>",
  "extraCells": { "on": true, "top", "bottom", "left", "right" },
  "cayoPerico": true
}
```

- `atlas`: スタイルは、同梱のスタイルの id か、このプロジェクトの自分のスタイルの id。
  言語には、英語が必ず入る。
- `heightQuality`: `""` は既定に戻す。
  地図を変えて、選んだ品質が合わなくなったときも、既定に戻す。
- `range.preset`: 範囲のプリセットのブロックを足す。範囲にあるものは、そのまま。
  ほかの範囲の変更の後に当てる。
- `stopResources`: `preset: true` は、プリセットの一覧に戻す。
- `extraCells`: `on: false` は、足したセルをなくす（`range.extraCells` を null に）。
- パス: プロジェクトのフォルダの中のパスは、相対で残す。
- ミニマップ: なくなる地図がミニマップなら、ミニマップを外す。

レスポンスはプロジェクト。エラーは次のとおり。

- 400 `INVALID`:
  - 読めるファイルのない自分のスタイル。
  - `extraCells` で、上限を超える値と、範囲のブロックが枠の外に出る値（先に範囲から除外する）。
  - `range.preset` で、知らないプリセットと、ブロックの一部がプロジェクトの枠の外にあるとき
    （同じ変更の `extraCells` を先に当てる）。
- 409 `LOCKED`: 今の実行のステップが読んでいる入力のとき。

### `GET /api/project/blocks`

プロジェクトの枠の全ブロックを返す。
北西のブロック `bx0`・`by0` から `cols` × `rows`（標準の枠は 0・0 から 32×48）。

- ブロックごとに 1 文字か 1 つの数を、行ごとに:
  範囲、既定の範囲、データ項目、再取得の印、衛星タイルが最新か。
- セル。
- `satelliteSea`: 衛星地図の縮小タイルが外洋を塗った色（`#rrggbb`。まだ作っていなければ null）。
  画面は、衛星地図を出している地図（計画・進捗、地図表示、道路と POI の編集）の、
  タイルのない所の背景に使う。

### `POST /api/project/retake`

- 本文: `{ "blocks": [...], "retake": true }` か、`{ "all": true }`（すべての印を外す）。
- レスポンス: プロジェクト（SSE の `project` も）。
- 400 `INVALID`: 範囲の外のブロック。
- 409 `LOCKED`: 範囲を読む実行のステップが、待っているか実行中の間（`range` の `PATCH` と同じ）。
  範囲を読むステップは、撮影・採取、オルソ補正、道路グラフ、地表、地域の色、道、衛星地図の縮小タイル。
- 409 `BUSY`: ほかの実行（別の画面かコマンド）が、作業フォルダを使っている間。

### `GET /api/project/plan?workers=`

- やることの表。
- 実行できる行と、それぞれ今すぐ実行できる単位の数。
- このプロジェクトの実行中（か直前）の実行を始めたときの、行ごとの残り（`atStart`: `runId`、`remaining`）。

### `POST /api/project/postals/fetch`

郵便番号データを今すぐ取得して、作業フォルダにコピーする（ラベルのステップと同じ）。
前提条件 `postals` を返す。
取得できなければ 422 `POSTALS`。

## 地図表示とタイル

| 呼び出し | 概要 |
|---|---|
| `GET /api/project/tiles/{set}/{z}/{x}/{y}.png?p=<鍵>&v=<番号>` | 作業フォルダのタイル |
| `GET /api/project/before` | 前回の書き出しの後に、タイルを書き換えた地図 |
| `GET /api/project/before-tiles/{地図}/{z}/{x}/{y}.png` | 書き換える前のタイル |
| `GET /api/project/point?x=&y=` | ある地点の、採取と地表の値 |

### `GET /api/project/tiles/{set}/{z}/{x}/{y}.png?p=<鍵>&v=<番号>`

作業フォルダのタイルを返す。
毎回確認する（[開発の手引き](development.ja.md#地図のタイル)の「地図のタイル」）。

### `GET /api/project/before`

前回の書き出しの後に、タイルを書き換えた地図を返す（`maps`）。地図ごとに、次の項目。

- `map`
- `tiles`: 残したタイルの数。
- `z8`: z8 のタイル。x, y, ... の並び。
- `faintZ8`: そのうち、違いの見えないもの。
- `areas`: そのまとまり。西・北・東・南 m、タイルの数、`faint`（どのタイルも違いが見えない）。
  見えるものが先で、大きい順。

### `GET /api/project/before-tiles/{地図}/{z}/{x}/{y}.png`

前回の書き出しの後に書き換えたタイルの、書き換える前の中身を返す。
なければ、今のタイルを返す（`tiles` と同じ扱い）。

### `GET /api/project/point?x=&y=`

ある地点の、採取と地表の値を返す。ないものは null。

`block`・`scanned`・`material`・`materialClass`・`height`・`water`・`zone`・`zoneName`・`zoneJa`・
`street`・`streetJa`・`onRoad`・`landcover`・`building`

## 道の編集

| 呼び出し | 概要 |
|---|---|
| `GET /api/project/road-editor` | 道の編集の状態 |
| `GET /api/project/road-editor/paths` | ゲームの道のデータ |
| `GET /api/project/road-editor/node?key=`、`.../link?from=&to=` | ノード、リンクの記録の値 |
| `GET /api/project/road-editor/ground?x=&y=` | ある地点の地面の採取 |
| `GET /api/project/road-editor/bundled` | 同梱の道路の編集 |
| `PUT /api/project/road-editor/edits` | 道の編集を保存する |
| `POST /api/project/road-editor/preview` | その編集で次の実行が作る道の形 |
| `POST /api/project/road-editor/game-files` | ゲームファイルを今すぐ読む |
| `GET /api/project/road-editor/shapes/{map}/{z}/{x}/{y}.png?v=` | 道だけのタイル |

### `GET /api/project/road-editor`

道の編集の状態を返す。

- `unavailable`: `noRoadMaps`・`noGameFiles`・null。
- `file`: プロジェクトの `roadEdits`。
- `edits`: 中身。
- `problem`
- `notApplied`: `kind`・`key`・`reason`。
- `pathsVersion`
- `shapesVersion`: null は、道の形がまだない。
- `shapesLeft`
- `maps`: 道のタイルの色に選べる地図。

### `GET /api/project/road-editor/paths`

ゲームの道のデータを、列の形で返す（[開発の手引き](development.ja.md#道の編集)の「道の編集」）。
409 `NO_GAME_FILES`。

### `GET /api/project/road-editor/node?key=`、`.../link?from=&to=`

ノード、リンクの記録の値をすべて返す（道のデータのとおり）。ないときは 404。

### `GET /api/project/road-editor/ground?x=&y=`

ある地点の地面の採取を返す: `block`・`scanned`・`material`・`materialClass`・`height`・`water`

### `GET /api/project/road-editor/bundled`

同梱の道路の編集を、ファイルの形のまま返す。画面が、まとまりを取り込むのに使う。

### `PUT /api/project/road-editor/edits`

- 本文: 道の編集のファイルの中身。
- レスポンス: 保存して、道の編集の状態を返す。
- 編集のファイルのないプロジェクトは、初めての保存でファイルを作って、プロジェクトに付ける。
- 400 `INVALID`: 問題をすべて返す。
- 409 `LOCKED`

### `POST /api/project/road-editor/preview`

- 本文: 道の編集のファイルの中身。
- レスポンス: `{ "id", "seconds", "provisional" }`。その編集で、次の実行が作る道の形。
  `provisional` は、地表のないブロックがあり、仮の形であること。
- 409 `SUPERSEDED`: 新しいリクエストが来た。
- 409 `NO_GAME_FILES`

### `POST /api/project/road-editor/game-files`

ゲームファイルを今すぐ読む（[開発の手引き](development.ja.md#ゲームファイル)の「ゲームファイル」）。

- レスポンス: `{ "changed", "pathsChanged", "areas", "nodes", "links", "seconds" }`
- 409 `LOCKED`: ゲームファイルか地図データのステップが実行中。
- 409 `GAME_FILES`: 読めない理由。

### `GET /api/project/road-editor/shapes/{map}/{z}/{x}/{y}.png?v=`

道だけを、地図の色で描いたタイルを返す。
`v` は、道の形の版か、プレビューの id。
道の形がなければ 404。

## POI

| 呼び出し | 概要 |
|---|---|
| `GET /api/poi/icons` | MDI のアイコン（`data/mdi-icons.json` のまま） |
| `GET /api/project/poi` | POI の画面の最初のデータ |
| `PUT /api/project/poi?language=` | POI の編集を保存する |
| `POST /api/project/poi/sample` | POI スタイルを描いた PNG |
| `GET /api/project/poi/images?path=` | POI スタイルが指定している PNG |
| `POST /api/project/poi/images?name=` | POI スタイルの PNG を置く |
| `POST /api/project/poi/import` | CSV か JSON から POI を読む |

### `GET /api/project/poi`

POI の画面の最初のデータを返す。

- `folder`・`stylesFile`: プロジェクトが付けているまま。null は、なし。
- `set`: `folders`、`points` つきの `groups`、プロジェクトの `styles`。
  ファイルが読めないときは null。
- `bundledStyles`・`bundledGroups`
- `fonts`・`background`: 最初のアトラスのスタイルのもの。`fonts` は言語ごと。
- `problem`

### `PUT /api/project/poi?language=`

- 本文: 編集した `set` 全体。
- レスポンス: `GET /api/project/poi` と同じもの。
- 初めて必要になったときに、プロジェクトファイルの横に `poi/` と `poi-styles.json` を作る。
  `poi/` は同梱のまとまりから作る。名前は、`language`（画面の言語）の名前にする。
- 400 `INVALID`: 理由をすべて返す。何も書かない。
- 409 `LOCKED`: 実行のステップが POI を読んでいる間。

### `POST /api/project/poi/sample`

- 本文: `{ "style", "label", "language", "zoom" }`
- レスポンス: そのスタイルを、地図と同じ描き方で描いた PNG（z8・z7・z6）。
  POI のラベルは、その言語のもの。
- 400 `INVALID`

### `GET /api/project/poi/images?path=`

POI スタイルが指定している PNG を返す（POI スタイルのファイルのフォルダの中のもの）。

### `POST /api/project/poi/images?name=`

- 本文: PNG のバイト。
- レスポンス: `{ "image" }`。スタイルが指定するパス。
- PNG は、POI スタイルのファイルの横の `poi-icons/` に置く。
  同じ絵は同じ名前、違う絵は `-2`… を付けた名前にする。
- PNG でなければ 400 `INVALID`。

### `POST /api/project/poi/import`

- 本文: `{ "name", "text" }`。名前が `.csv` で終われば CSV、ほかは JSON。
- レスポンス: `{ "points", "skipped": [{ "line", "reasons": [{ "code", "value" }] }], "name" }`

## スタイル

| 呼び出し | 概要 |
|---|---|
| `GET /api/styles/schema` | スタイルエディタの項目の表（`data/style-schema.json` のまま） |
| `GET /api/styles/shared` | 共通のスタイル（`id`・`name`・`base`・`bundled`・`maps`・`problem`） |
| `GET /api/fonts` | この PC のフォントの名前（名前の順） |
| `GET /api/project/styles` | スタイルの一覧 |
| `GET /api/project/styles/{id}` | スタイルの値 |
| `PUT /api/project/styles/{id}` | スタイルを保存する |
| `POST /api/project/styles` | 別のスタイルからスタイルを作る |
| `POST /api/project/styles/import?id=&language=` | スタイルのファイルを読み込む |
| `PUT /api/project/styles/{id}/name` | スタイルの名前を変える |
| `DELETE /api/project/styles/{id}` | スタイルを削除する |
| `POST /api/project/styles/{id}/shared?overwrite=` | 共通のスタイルにコピーする |
| `GET /api/project/styles/{id}/file` | スタイルのファイルのまま |

### `GET /api/project/styles`

- `folder`: プロジェクトの `styles`。
- `styles`: 同梱のアトラスのスタイル、プロジェクトのスタイルの順。
  - `id`・`name`
  - `base`: 同梱は null。
  - `bundled`
  - `maps`: 使っている地図。
  - `problem`: 読めないファイルの理由。
- `zoneNames`: ゲームファイルの地区の名前。コード → `en`・`ja`。読み込む前は空。

### `GET /api/project/styles/{id}`

スタイルの値を返す。なければ 404。

- `values`: 重ねた値。
- `changes`: 元から変えた値。同梱は空。
- `baseValues`: 元のスタイルの値。同梱は null。
- `maps` ほか。

### `PUT /api/project/styles/{id}`

- 本文: スタイル全体（画面が直した `values`）。
- レスポンス: 保存したスタイル。ファイルは、元との差だけ。
- 400 `INVALID`: 問題をすべて返す。何も書かない。
- 409 `LOCKED`: 実行中のステップが、このスタイルの地図を読む。
- 404: 同梱のスタイルも。

### `POST /api/project/styles`

- 本文: `{ "from", "shared", "id", "name" }`
- レスポンス: 別のスタイル（同梱、プロジェクトの、`shared` なら共通の）から作ったスタイル。
- 最初のスタイルは、`styles` のフォルダを作って、プロジェクトに付ける。
- 400 `INVALID`: id の形、名前がない。
- 409 `ID_TAKEN`
- 404

### `POST /api/project/styles/import?id=&language=`

- 本文: スタイルのファイル。
- レスポンス: 読み込んだスタイル。
- `id` を付けると、別の id にする。
- スタイル全体のファイルの英・日の名前は、`language` の名前 1 つにする。
- 400 `INVALID`: 問題をすべて返す。
- 409 `ID_TAKEN`

### `PUT /api/project/styles/{id}/name`

- 本文: `{ "name" }`
- レスポンス: 名前を変えたスタイル。id はそのまま。

### `DELETE /api/project/styles/{id}`

- レスポンス: スタイルの一覧。
- 409 `IN_USE`: 出力する地図が使っている。
- 404: 同梱のスタイルも。

### `POST /api/project/styles/{id}/shared?overwrite=`

共通のスタイルにコピーして、共通のスタイルの一覧を返す。
同じ id があれば 409 `EXISTS`（`overwrite=true` で上書き）。

### `GET /api/project/styles/{id}/file`

スタイルのファイルを、そのまま返す（保存用）。同梱のスタイルは、同梱のファイル。

## スタイルのプレビュー

| 呼び出し | 概要 |
|---|---|
| `GET /api/styles/sample` | 見本の土地の状態 |
| `POST /api/styles/sample` | 見本の土地を作り始める |
| `GET /api/styles/sample/overview.png` | 見本の土地の全体の小さな絵 |
| `GET /api/project/styles/preview` | プロジェクトの地図のデータでのプレビューの状態 |
| `PUT /api/project/styles/preview/places` | プロジェクトの場所の一覧を保存する |
| `POST /api/project/styles/preview` | 見本の土地かプロジェクトの窓を描く |
| `GET /api/project/styles/preview/tiles/{id}/{x}/{y}.png` | 描いた窓の z8 のタイル |
| `POST /api/project/styles/preview/pick` | ある点の色の出どころ |

### `GET /api/styles/sample`

見本の土地の状態を返す。

- `state`: `none`・`making`・`ready`・`failed`。
- `progress`: 作っている間の 0〜1。
- `message`: できなかった理由。
- `frame`: 土地の枠。西・北・東・南、m。
- `place`: 初めに出す窓の中心。
- `sizes`: 窓の一辺の選べる値、m。

### `POST /api/styles/sample`

見本の土地を作り始めて、見本の土地の状態を返す。
できているときと、作っている間は、何もしない。
並列は、開いているプロジェクトの `parallel`。なければ CPU の半分。

### `GET /api/styles/sample/overview.png`

見本の土地の全体の小さな絵を返す。
PostalCodeMap 風の地面の画像を、幅 600 px にしたもの。
できる前は 404。

### `GET /api/project/styles/preview`

開いているプロジェクトの地図のデータでの、プレビューの状態を返す。

- `state`: `none`・`ready`。
- `waiting`: none の間、プロジェクトにまだないもの（道の形・地区・地表）。
- `frame`: 描けるブロックの枠。西・北・東・南、m。
- `blocks`: プロジェクトの枠のブロックごとに 1 文字。北西の角から、行ごと。
  `1` は描ける、`0` は描けない。
- `map`: プロジェクトの枠。`GET /api/project` の `frame` と同じ形。
- `recommended`・`places`: 推奨の場所と、プロジェクトの場所。`name`・`x`・`y`・`ready`。
- `sizes`

### `PUT /api/project/styles/preview/places`

- 本文: `[{ "name", "x", "y" }, ...]`。プロジェクトの場所の一覧全体。
- レスポンス: プロジェクトの `previewPlaces` と、プレビューの状態。
- 名前は前後の空白を除き、中心は m に丸める。場所がなければ null。
- 名前のない場所は 400 `INVALID`。

### `POST /api/project/styles/preview`

見本の土地かプロジェクトの窓を描く。

本文: `{ "values", "x", "y", "size", "language", "side", "source" }`

- `values`: スタイル全体。
- `x`・`y`: 窓の中心（m）。
- `size`: 窓の一辺。`sizes` のどれか（近いものにする）。
- `language`: ラベルの言語。`en`・`ja`。
- `source`: 描くもの。`sample`（既定）・`project`。
- `side`: 画面の側。`left`・`right`。

レスポンス: `{ "id", "bounds", "seconds" }`

- `id`: タイルを読む id。
- `bounds`: 窓の端。西・北・東・南。
- `seconds`: 描くのにかかった秒数。

並列は、開いているプロジェクトの `parallel`。

エラー:

- 400 `INVALID`: スタイルの問題をすべて返す。
- 409 `NOT_READY`: 見本の土地がまだない。
  `project` では、プロジェクトに道の形・地区・地表がまだない。
- 409 `NO_PROJECT`: `project` で、プロジェクトを開いていない。
- 409 `SUPERSEDED`: 同じ側に、新しいリクエストが来た。
- 409 `PREVIEW`: その窓を描けない。

### `GET /api/project/styles/preview/tiles/{id}/{x}/{y}.png`

描いた窓の z8 のタイルを返す。ないタイルは 404。

- 側ごとに、新しい 2 つの描画を残す。
- id は使い回さないので、`Cache-Control: private, max-age=3600` で返す。

### `POST /api/project/styles/preview/pick`

ある点の色の出どころを返す。

本文: `{ "values", "x", "y", "cx", "cy", "size", "language", "source" }`

- `values`: スタイル全体。
- `x`・`y`: 調べる点（m）。
- `cx`・`cy`・`size`: 窓の中心と一辺。
- `language`: ラベルの言語。
- `source`: 描くもの。

レスポンス:

- `color`
- `steps`: 上から、いちばん上で覆うものまで。
  - `kind`・`color`
  - `cover`: 画素を覆う割合。
  - `factor`: 陰影。
  - `class`: 道の格。
  - `paint`・`band`・`bed`・`text`
  - `rule`: 建物の色の決まり。`zone`・`region`・`default`。
  - `region`
- `point`: `zone`・`zoneEn`・`zoneJa`・`ground`・`water`・`building`。
- `ground`: 地面の画像が混ぜたもの。
  - `kind`: `ground`・`region`・`tone`・`trees`。
  - `id`・`color`・`share`

エラー:

- 400 `INVALID`
- 409 `NOT_READY`・`NO_PROJECT`
- 409 `PREVIEW`: 範囲のブロックの外。

## 書き出し

処理の中身は、[開発の手引き](development.ja.md#ミニマップと書き出し)の「ミニマップと書き出し」。

| 呼び出し | 概要 |
|---|---|
| `GET /api/project/export` | 書き出せるものと、保存した選択 |
| `POST /api/project/export/check` | 選択を確認する |
| `POST /api/project/export` | 書き出しの実行を始める |
| `POST /api/project/export/show` | フォルダをエクスプローラーで開く |
| `POST /api/project/convert/check` | 編集した画像の変換の選択を確認する |
| `POST /api/project/convert` | 編集した画像の変換の実行を始める |

### `GET /api/project/export`

- タイルのある地図。枚数・大きさ・ズーム、ズームごとの枚数と大きさ、最新か。
- ミニマップのテクスチャの、作った数と全体（`made`・`total`）。シート、全体図、標準の枠の外 `extra`。
- 島の地図（`islandMap`、`islandLandMissing`）。
- ミニマップのリソースがゲームのファイルから読むもの（内装の地図の一覧、島の地図）を、
  この PC で読めないときは、`game` = `gta`・`keys`。
- 編集用のファイルとして書き出せる地図（`editable.maps`: `map`・`ready`・`upToDate`）と、
  枠の縦横のブロック数（`blocksX`・`blocksY`。絵は、z6 で 1 ブロック 256 px）。
- 保存した選択と、そのリソース名。
  編集用のファイルの地図は、保存した選択に入らない（`editableMaps` は空）。
- 書き出せない理由。
- 書き出し先の記録。
- 前回変換した、編集した画像（`picture`: `file`・`map`）。
  `file` は、画像の場所。`map` は、元の地図の id。

### `POST /api/project/export/check`

- 本文: `{ "folder", "maps": [...], "zip", "baseUrl", "maxZoom", "minimap", "resourceName" }`（どれか）
- 編集用のファイル: `"editableMaps": [...]`（書き出す地図。なければ書かない）、
  `"editableZoom"`（6 か 7）、`"editableFormats"`（`psd`・`svg` の一覧）、`"language"`（`ja` か `en`。レイヤーの名前）。
  - `editableFormats` がなければ、保存した形式を使う。
  - 地図を選んで、形式が空の一覧のときは、書き出せない（`NO_EDITABLE_FORMAT`）。
- レスポンス: `GET /api/project/export` と同じ形で、その選択について。

### `POST /api/project/export`

- 本文: `POST /api/project/export/check` と同じ。
- 選択をプロジェクトに残し、書き出しの実行を始める。レスポンスは、スナップショット。
- 編集用のファイルは、ズームレベルと形式を残す。地図は残さない。
- 実行のステップは `export`。編集用のファイルを書くときは、`export.layers` と `export.files` が続く。
- 書き出せなければ、400 `EXPORT_<理由>`。

### `POST /api/project/export/show`

- 本文: `{ "path" }`
- そのフォルダを、エクスプローラーで開く。

### `POST /api/project/convert/check`

- 本文: `{ "folder", "file", "map", "tiles", "minimap", "resourceName", "baseUrl" }`（どれか）
  - `file`: PNG の画像。
  - `map`: 元の地図の id。なければ、画像のファイル名・ミニマップの対象の地図・最初の地図の順に決める。
  - `tiles`・`minimap`: Web タイルと、ミニマップのリソースを出力するか。なければ、出力する。
  - `folder`・`baseUrl`: なければ、プロジェクトに残した書き出しの値。
- レスポンス:
  - `picture`: 画像の `file`・`width`・`height`・`zoom`（6 か 7）と、変換できない理由 `problem`。
    `problem` は、`NOT_FOUND`・`NOT_PNG`・`INTERLACED`・`BAD_SIZE`。
    画像を選んでいなければ、`picture` は null。
  - `map`: 使う地図。
  - `guessedMap`: 画像のファイル名から分かる地図。分からなければ null。
  - `problems`: 変換できない理由。
  - `resourceName`: ミニマップのリソースに付く名前。

### `POST /api/project/convert`

- 本文: `POST /api/project/convert/check` と同じ。
- 画像の場所と元の地図をプロジェクトに残し、変換の実行を始める。レスポンスは、スナップショット。
- 実行のステップは `convert.tiles`・`convert.textures`・`convert.files`。
  ミニマップのリソースを出力しないときは、`convert.textures` がない。
- 変換できなければ、400 `EXPORT_<理由>`。

## ゲーム

事前確認、描画設定、撮影用リソースの API。
処理の中身は、[開発の手引き](development.ja.md#ゲーム-接続事前確認撮影採取)の「ゲーム:
接続・事前確認・撮影・採取」。

| 呼び出し | 概要 |
|---|---|
| `GET /api/game/precheck` | いちばん新しい事前確認 |
| `POST /api/game/precheck` | 事前確認を実行する |
| `GET /api/game/precheck/files/{name}` | 事前確認の結果のファイル |
| `GET /api/game/render` | 描画設定 |
| `POST /api/game/render/apply` | 描画設定を書き換える |
| `POST /api/game/render/restore` | 控えを書き戻す |
| `POST /api/game/resource/start` | リソースの起動 |
| `GET /api/game/fivem` | この PC で、FiveM が動いているか |
| `GET /api/capture-resource` | 撮影用リソースの名前と版、印 |
| `GET /api/capture-resource/found?folder=` | そのフォルダにある `fxmapgen-capture` の版（なければ null） |
| `POST /api/capture-resource/save` | 撮影用リソースを書き出す |
| `GET /api/capture-resource/zip` | `fxmapgen-capture.zip`（中は `fxmapgen-capture/` 1 つ） |
| `POST /api/project/capture-placed` | 利用者の印を付ける・外す |

### `GET /api/game/precheck`

開いているプロジェクトの、いちばん新しい事前確認を返す。なければ 204。

その後にアプリが撮影用リソースを止めていれば、`resourceStoppedUtc`（その時刻）が付く。
確認していないものとして扱う。

### `POST /api/game/precheck`

- 事前確認を実行した結果を返す。描画の安定待ちの測定を含めて、15 秒ほど。
- 409 `GAME_BUSY`: 実行が、ゲームを使っている。
- 409 `GAME_STARTING`: リソースの起動の間。
- 409 `GAME_CHECKING`: ほかの事前確認の間。
- SSE `precheck`。

### `GET /api/game/precheck/files/{name}`

その結果の `shot.png`・`shot-marked.png`・`console.log`・`report.json` を返す。

### `GET /api/game/render`

描画設定を返す。場所、あるか、FiveM が動いているか、差分、控えの一覧。

### `POST /api/game/render/apply`

控えを保存してから、書き換える。409 `FIVEM_RUNNING`、404 `NO_FILE`。

### `POST /api/game/render/restore`

- 本文: `{ "backup": "<名前>" }`。なければ、いちばん新しい控え。
- 控えを書き戻す。

### `POST /api/game/resource/start`

リソースの起動（[開発の手引き](development.ja.md#リソースの起動)の「リソースの起動」）。

- `state`: `noConsole` / `running` / `started` / `notStarted`。
- `resource`: 応答した名前。版も返す。
- `expected`: exe の版。
- `console`: ゲームのコンソールの行。
- 409 `GAME_BUSY`: 実行が、ゲームを使っている。
- 409 `GAME_CHECKING`: 事前確認の間。
- 409 `GAME_STARTING`: ほかの起動の間。

### `GET /api/game/fivem`

`{ "running" }` を返す。この PC で、FiveM が動いているか。
お知らせを消すために使う。プロセスを見るだけ。

### `GET /api/capture-resource`

撮影用リソースの名前と版、開いているプロジェクトの印（`placed`）を返す。

### `POST /api/capture-resource/save`

- 本文: `{ "folder", "replace" }`
- 書き出す（`written`）。開いているプロジェクトに、印を付ける。
- フォルダがあって、`replace` がなければ、409 `EXISTS`。

### `POST /api/project/capture-placed`

- 本文: `{ "placed" }`
- 利用者の印を付ける・外す。

## アプリの設定とダイアログ

| 呼び出し | 概要 |
|---|---|
| `GET /api/settings`、`PUT /api/settings` | アプリの設定（`settings.json`） |
| `GET /api/settings/gamefiles?gta=&keys=` | そのフォルダで見つかるもの |
| `POST /api/dialog/file` | 選んだファイル（Windows のファイル選択ダイアログ） |

### `GET /api/settings`、`PUT /api/settings`

本文とレスポンスは、アプリの設定（`settings.json`）。

```
{ "recentProjects", "language", "theme", "jobListWidth", "gtaFolder", "keysFolder", "guides" }
```

### `GET /api/settings/gamefiles?gta=&keys=`

そのフォルダで見つかるものを返す。
[開発の手引き](development.ja.md#ゲームファイル)の「ゲームファイル」を参照。

### `POST /api/dialog/file`

- 本文: `{ "save": true, "initial", "title", "kind" }`
- レスポンス: 選んだファイル。
- `kind` がなければ、プロジェクトファイルを選ぶ。
  `"roadData"` なら、サーバーの道路データ（zip・ynd）を選ぶ。
  `"picture"` なら、PNG の画像を選ぶ。
