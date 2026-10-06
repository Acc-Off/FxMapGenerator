# スタイルの形（data/styles/&lt;id&gt;.json）

アトラス地図と道路地図の見た目を決めるファイル。
コードの正は、`src/FxMapGenerator.Core/Styles/MapStyle.cs`。
同梱のスタイルは、3 つ。

| id | 名前 | 使う地図 |
|---|---|---|
| `postalcodemap` | PostalCodeMap 風（PostalCodeMap style） | アトラス（`atlas-postalcodemap-<言語>`） |
| `regional` | 地域色（Regional colors） | アトラス（`atlas-regional-<言語>`） |
| `roadmap` | 道路地図（Road map） | 道路地図（`roadmap`） |

- `postalcodemap`: 材質ごとの色。自然の地面どうしを、ぼかして混ぜる。
- `regional`: 地域の色に、材質を淡い濃淡として重ねる。

ファイルの決まり:

- UTF-8 の JSON。
- 読むときに、中身を確認する。おかしな所があれば、理由をすべて挙げて止める。
- 長さは m、面積は m²、色は `#rrggbb`。

## 全体

| 欄 | 意味 |
|---|---|
| `format` | 形の版（1） |
| `id`、`name` | スタイルの id と名前 |
| `credit` | 書き出しのクレジットに入れる文（なくてもよい） |
| `background` | 地図の背景の色（セルを描くときの下地） |
| `groundPaints` | 地面の種類ごとに、塗る色の名前 |
| `groundRaster` | 地面の画像（1 m。ベクタの層の下に敷く） |
| `shade` | 陰影。ないスタイルは、陰影をかけない |
| `sea` | 水の深さの帯 |
| `canopy` | 木の茂み |
| `regions` | 地域の色 |
| `buildings` | 建物の色の決め方 |
| `contours` | 等高線。ないスタイルは、描かない |
| `labels` | 文字。ないスタイルは、文字を描かない |
| `paint` | 色 |

- `id`、`name`:
  - 同梱のスタイルの名前は、`en`・`ja`。画面の言語で出し、書き出しの地図の名前は `en`。
  - 自分のスタイルの名前は、文字 1 つ。書き出しの地図の名前も、書いたとおり。
  - 書き出しの地図の名前は、`Atlas map (<名前>)`。英語でない地図は、`Atlas map (<名前>, <言語>)`。
- `credit`: 例は、見た目の出どころ（`project-format.ja.md` の「クレジット」）。
  名前と `credit` を直しても、描き直しにはならない。
- `background`: 範囲の外の外洋は、この色ではなく、海の最も深い帯の色で塗る（下の `sea`）。
- `groundPaints`:
  - 地面の種類は、`none`、`urban`、`grass`、`dirt`、`sand`、`beach`、`rock`、`vegetation`、`snow`、
    `waterMaterial`、`defaultMaterial`。
  - 塗る色の名前は、`paint.ground` の名前か、`water`。
  - 同じ名前の種類は、まとめて 1 つの層になる。
  - `defaultMaterial` は、材質が `DEFAULT` の地面。
    ゲームが固有の材質を付けていない、地形・岩・小物など。
- `groundRaster`: ないスタイルは、地面をベクタの層で塗る（下）。

## groundRaster

### `mode: "blend"`

- 地面の種類ごとの色（`groundPaints` → `paint.ground`）。
- `natural` の種類（線路は除く）どうしを、`method` のやり方でなじませる。
  `natural` にない種類（舗装や硬い面）は、くっきりのまま。
- 同梱の「PostalCodeMap 風」の `natural` は、自然の種類と `defaultMaterial`。
  山の斜面などにある材質 `DEFAULT` の地面を、まわりの地面となじませる。
  `method` は `detail`（`width` 10、`amount` 0.25）。
- 値は、方法ごとのオブジェクトに持つ。
  選んでいない方法のオブジェクトも、置いておける。
  切り替えたときの値になり、変えても地図の作り直しは要らない。
- ぼかしは、自然の地面どうしのガウスぼかし。

`method` の値:

- `none`: なじませない（地面の種類ごとの色のまま）。
- `blur`: ぼかしだけ。`blur.width`（m）。
- `patches`: 小さなまとまりの色を替えてから、`patches.edgeWidth` m でぼかす。
  - 替えるのは、同じ色の自然の地面のまとまり（上下左右でつながる所）で、`patches.minArea` m² 未満のもの。
  - まわりの自然の地面でいちばん多い色に替える。変わらなくなるまで、最大 8 回。
- `detail`: `detail.width` m でぼかした色に、ぼかす前の色との差を、`detail.amount`（0〜1）の分だけ戻す。
- `brush`: 自然の地面の色に桑原フィルターをかけてから、`brush.edgeWidth` m でぼかす。
  桑原フィルターは、点を角に持つ一辺 `brush.radius` + 1 m の 4 つの正方形のうち、
  色のばらつきがいちばん小さいものの平均。

### `mode: "regions"`

- 地域の色に、材質を濃淡として重ねる。
  地域の色は、`regions`、全域の `data/regions-<key>.grid`。`regions` の中身ごとに 1 つ。
- `tones` の順に、その材質の割合（ガウスぼかし `toneBlur` m）だけ、`color` へ `strength` の分だけ寄せる。
  順は、`paved`（`urban`、`defaultMaterial`）・`dirt`・`sand`（`sand`、`beach`）・`rock`・`snow`。
- 木（`vegetation`）の所は、`treeDarkening` 倍に暗くする。

## shade

1. 高さ（1 m）から、建物・木・高架を抜いて、周りの地面で埋める。
2. ガウスぼかし（`blur` m）をかける。
3. `lights` の光（方位 `azimuth`°、重み `weight`）で照らす（高度 `altitude`°）。
4. 平らな所を 1 として、地面の色に `1 + strength ×（明るさ − 平ら）/ 平ら` を掛ける。

## sea

- 水の深さ（採取の水面 − 水底）を、`bands`（m、増える順）で帯に分ける。
- 水底の種類（岩とそれ以外 = 砂）ごとに塗る。
  `rockMinArea` より小さい岩のまとまりは、砂にする。
- 色は、`paint.sea.sand` と `paint.sea.rock`（帯の数 + 1 色、浅い方から）。
- 範囲の外の外洋は、`paint.sea.sand` の最後の色（最も深い帯）で塗る。
  範囲の端のブロックの海が、段なくそのまま続いて見えるように。
  範囲の外の外洋とは、縮小タイルの範囲の外、ミニマップの未取得のブロックを塗るとき、
  書き出しのビューアの背景。

### 透ける水

- `paint.waterOpacity` と `paint.sea.opacity` が 1 より小さい所は、
  背景と地面の絵を消して、その色と不透明度で塗る。
  建物・道・文字・POI は、その上に不透明に描く。
- そのときだけ、描いた絵（タイル）に透明が残る。透明のあるタイルは、RGBA の PNG。
- 範囲の外の外洋は、最後の帯の不透明度で塗る。
- 書き出しのビューアの背景は、最後の帯の色。そのため、透けた所には、その色が見える。
- ゲーム内のミニマップでは、次のようになる。
  - DXT5（ESC の地図）は、透明のまま。後ろのゲームの画面が透ける。
  - DXT1（レーダー）は、透明でない所を、すべて不透明にする。
    透明な所には、下に敷く全体図（`minimap_lod_128`）が見える。
- 同梱の「PostalCodeMap 風」と「地域色」は、PostalCodeMap の絵から測った値。
  どちらのスタイルの `credit` も、Virus_City を挙げる。
  帯は、200 m まで 16 本。浅い帯はほぼ不透明、深い帯ほど薄く、200 m より深い所は透明。
- 「道路地図」は、全部 1。

## canopy

- `mode`:
  - `none`: 描かない。
  - `tint`: `tint.color` を `tint.alpha` で重ねる。
  - `dots`・`hatch`: `color` の点か斜線を、`spacing` m ごと、大きさ `size` m。
- `minArea` より小さい木の茂みのまとまりは、描かない。

## regions

| 欄 | 意味 |
|---|---|
| `colors` | 地域の名前と色（この順が地域の番号） |
| `zones` | 地区のコード → 地域の名前 |
| `blur` | 地域の境目のぼかし（ガウスの m） |
| `sea` | まわりに陸のない所に使う地域 |

`zones` の表にない地区の陸は、いちばん近い決まった陸の地域になる。

## buildings

建物（つながった建物のマスのまとまり）ごとに、1 色。
色の名前（`paint.buildings` の名前）は、次の順で決める。

1. 重心の地区の `byZone`。
2. なければ、その地区の地域の `byRegion`。
3. なければ、`default`。

## contours

`interval` m ごとの等高線を、`color` の線（幅 `width` m）で描く。

## labels

地図の文字（番地、地区名、通り名）の置き方と見た目。

- 文字は、言語ごとに全域で 1 回置いて、`data/labels-<言語>-<key>.json` に書く（ステップ「文字」）。
  描画は、それを描くだけ。
- `key` は、この節から色・縁取り・`minPixels` を除いた中身の、SHA-256 の先頭 12 桁。
  同じ置き方のスタイル（同梱の `postalcodemap` と `regional`）は、同じファイルを使う。
- 色と縁取りを変えても、置き直さない。
- 大きさ（em）・間隔は、m。

| 欄 | 意味 |
|---|---|
| `fonts` | 言語ごとのフォント |
| `clearance` | 置いた文字のまわりに空ける幅 |
| `minPixels` | 描くときに、これより小さく見える文字は描かない（px） |
| `postal.color` / `size` / `sizeByZone` | 番地の色と大きさ |
| `zone.color` / `size` / `spacing` / `weight` / `outline` / `outlineWidth` | 地区名 |
| `zone.minArea` | 地区名を置く地区の、最小の広さ（m²） |
| `street.color` | 通り名の色 |
| `street.classes.<道の格>` | 道の格ごとの値 |
| `street.routeNumbers` | 路線番号を、通り名に入れる |
| `street.expand` | 英語の通り名の略語を書き戻す表（単語ごと、表の順に） |

- `fonts`: `en` は必須。ほかの言語がなければ、`en`。番地は、いつも `en` のフォント。
- `postal`: 番地のファイルに大きさがなければ、番地の位置のゲームの地区の `sizeByZone`。
  なければ、`size`。
- `zone`: 英語は大文字。字間 `spacing`。
- `zone.minArea`: 地区のいちばん大きなまとまりがこれより小さい地区には、名前を置かない。
- `street.classes.<道の格>`: `highway`・`major`・`street`・`minor`・`track` ごとに、次の値を持つ。
  - `size`、`weight`。
  - `repeat`: この長さごとに 1 つ。
  - `sameNameGap`: 同じ名前どうしの最小の間隔。
  - `maxGlyphTurn`: 字と字の間で線が曲がってよい角度（度）。
  - `letterSpacing`: 字の後ろに足す間隔（em）。
- `street.routeNumbers`: `data/routes.json` の路線の通り。英語の名前が一致する通りだけ。

同梱の既定の値:

- 番地の大きさは、3 段。
  postal code map の元の地図の大きさを、ゲームの地区ごとに多い方で決めた。
  - 24.41 m: 市街の地区。既定。
  - 30.52 m: パレト、サンディ海岸、砂漠、山の一部など 18 地区。
  - 36.62 m: グレイプシード、ハーモニー、サン・チアンスキー山地など 13 地区。
- 高速道路（`highway`）の大きさ 25.5 m は、17 m の 1.5 倍。
  上下線をまとめた太い線に合わせた大きさ。

置き方（コードで決まっている規則）:

1. 番地と、アトラス地図に出す POI が、先に場所を取る。重ならないかは、調べない。
2. 地区名: 地区のいちばん大きなまとまりの重心に置く。
   - 場所が空いていなければ、30 m おきに 90 m まで、近い順にずらす。
   - それでもだめなら、0.8 倍、0.65 倍に小さくして、もう一度探す。
3. 通り名: 格の高い名前から。同じ格なら、道が長い名前から。
   - 同じ名前の道をつなぐ。
   - 上下線に分かれた高速道路などは、真ん中の線にまとめる（80 m 以内で並ぶ所）。
   - 50 度より急な角で切る。
   - 高速道路の名前は、ほかの名前の高速道路・幹線と並んで走る所（45 m 以内）には、
     ほかに書ける所がないときだけ書く。
   - 線の長さを `repeat` で割った数だけ、線の上に均等に置く。置く所は、5 m おきに探す。
   - どの文字も、それまでに置いた文字と重ならない所だけに置く（1 m の格子で判定）。
     入らなければ、0.8 倍にして、もう一度探す。

## paint

| 欄 | 意味 |
|---|---|
| `ground` | 地面の色（名前 → 色） |
| `water` | 水の色（深さの帯の下地） |
| `waterOpacity` | 深さの帯を塗らない水の不透明度（0〜1） |
| `sea` | 深さの帯の色と不透明度 |
| `buildings` | 建物の色（名前 → 色） |
| `rail` | 線路の色 |
| `roads` | 道 |

- `waterOpacity`: ないときは 1。スタイルエディタでは 0〜100 %。
  1 より小さいと、その所は後ろが透けて見える（上の「透ける水」）。
- `sea`: 色は `sand`、`rock`。不透明度は `opacity`。
  `opacity` は、帯ごとに 0〜1。砂の底と岩の底で同じ。帯の数 + 1 個。
  ないときは、全部 1。スタイルエディタでは 0〜100 %。
- `roads`:
  - `road`・`highway`: `fill` と、縁取りの `casing`。縁取りの幅は `casingWidth`。
  - `track`: 未舗装の道の `color` と、線の幅 `width`。
  - `tunnel`: トンネル。縁の破線の `color`・`width`・`dash`、中の `fill` と `fillAlpha`。
    `fillAlpha` は 0〜1。スタイルエディタでは 0〜100 %。
    ないスタイルは、トンネルを描かない。

## 自分のスタイル（`styles/<id>.json`）

スタイルエディタで作るスタイル。

- 同梱のアトラスのスタイル（`postalcodemap`・`regional`）を元にし、元から変えた値だけを持つ。
- プロジェクトの `styles` のフォルダに、1 つのスタイルを 1 つのファイル（ファイル名 = id）で置く。
  フォルダは、最初のスタイルを作ったときに、プロジェクトファイルの横に `styles` を作る。

```json
{
  "format": 1,
  "id": "dusk",
  "name": "夕方",
  "base": "regional",
  "shade": { "strength": 1 },
  "paint": { "roads": { "road": { "fill": "#fff8e8" } } }
}
```

| 欄 | 意味 |
|---|---|
| `format` | 形の版（1） |
| `id` | スタイルの id |
| `name` | 名前（文字 1 つ。必須） |
| `base` | 元の同梱のスタイルの id（`postalcodemap` か `regional`） |
| ほかの欄 | 元から変えた値。スタイルのファイルと同じ場所に書く |

- `id`: 半角の英小文字と数字。先頭は英字、32 文字まで。同梱のスタイルの id と重ならない。
  地図の id（`atlas-<id>-<言語>`）とフォルダの名前になるので、後から変えない。
- `name`: 画面に出し、書き出しの地図の名前にも、書いたとおりに使う。

**重ね方**

- 元のスタイルの上に、次のように重ねる。
  - オブジェクトは、キーごとに重ねる。
  - ほかの値（リストも）は、丸ごと置き換える。
  - `null` は、そのキーを消す。例: `"credit": null` で、クレジットなし。
- 変えていない値は、元のスタイルのものを使う。
  アプリの更新で同梱のスタイルが変わると、それに従う。
- 略語の表（`labels.street.expand`）は、表の順に適用するので、リストと同じく丸ごと置き換える。
  変えたときは、表の全部を、順番どおりに書く。
- 地区ごとの表は、キーごとに重ねる。

**読むとき**

- 重ねた結果をスタイルとして読んで、確認する。おかしな所があれば、理由をすべて挙げる。
- 地面の画像の作り方（`groundRaster.mode`）は、元のスタイルのまま。変えられない。

**書くとき**

- 自分の欄（`format`・`id`・`name`・`base`）、変えた値の順（元のスタイルの欄の順）に書く。
  UTF-8・LF・字下げ 2。
- 画面の保存は、直したスタイル全体と元のスタイルの差を書く。
  元と同じ値に戻した所は、ファイルから消える。
  数は値で比較し、`5.0` と `5` は同じ。

**読み込みと複製**

- 読み込み（画面の「読み込み…」）は、この形のファイルのほか、スタイル全体のファイルも読める。
  スタイル全体のファイルは、同梱のスタイルと同じ形で、`base` のないもの。
- そのときは、`groundRaster.mode` が同じ同梱のスタイルを元にし、違う値だけを持つスタイルにする。
  名前が `en`・`ja` のときは、画面の言語の名前 1 つにする。
- 画面の「複製…」も、名前は、その時の画面の言語で「〜 のコピー」から始める。

**共通のスタイル**

- アプリの設定のフォルダの `styles` に、同じ形で置く（画面の「共通のスタイルに保存」）。
  フォルダは `%LOCALAPPDATA%\FxMapGenerator\styles\`。`--data-dir` のときは、その下。
- ほかのプロジェクトで「複製…」の元に選ぶと、そのプロジェクトの `styles` にコピーする。
  後で共通の方を直しても、コピーは変わらない。

**地図に使う**

- プロジェクトのアトラス地図のスタイルの一覧（`maps.atlas.styles`）に、id を書く。
  画面では、「出力するスタイル」に並ぶ。
- プロジェクトの地図の言語（`maps.atlas.languages`）のそれぞれで、地図を作る。
  地図の id は、`atlas-<id>-<言語>`。
- 地図が使っている間は、削除できない。
- 保存すると、変えた値に応じて、そのスタイルの地図の作り直しが、計画・進捗に出る。
  セル描画だけ、前処理から、地域色・ラベルも。ほかのスタイルの地図は、描き直さない。
- 陰影と地域の色は、値の組ごとに作る。
  そのため、同梱のスタイルと違う光や地域の色のスタイルを一緒に出力しても、
  互いの陰影や地域の色で描かれることはない。

**実行**

- 実行は、始めたときに、地図が使う自分のスタイルをコピーして読む
  （`logs/run-<時刻>/inputs/styles/`）。
- そのスタイルを読むステップが残っている実行の間は、画面から保存できない。

## 項目の表（`data/style-schema.json`）

スタイルエディタが、スタイルの値を並べて見せ、直すための表。exe に埋め込む。
スタイルの値を、グループごとの行で持つ。

| 欄 | 意味 |
|---|---|
| `format` | 形の版（1） |
| `groups` | グループの並び |

グループ（`groups` の 1 つ）:

- `id`: `general`・`ground`・`regions`・`shade`・`water`・`buildings`・`roads`・`labels`・`zones`。
- `name`: `en`・`ja`。
- `items`: 行の並び。

行（`items` の 1 つ）:

| 欄 | 意味 |
|---|---|
| `key` | スタイルの中の場所 |
| `heading` | その行の上に出す小見出し（`en`・`ja`。なくてもよい） |
| `name`、`description` | 名前と説明（`en`・`ja`） |
| `control` | 部品 |
| `min`、`max`、`step`、`unit` | 数値の範囲と、刻みと、単位 |
| `choices` | 選択肢（`value` と `name`） |
| `when` | 出す条件 |
| `language` | その言語の地図のための行。なくてもよい |
| `rebuilds` | 変えて保存したとき、そのスタイルの地図で作り直しになるもの |
| `columns` | 表（`control` が `table`）の列 |

- `key`: 例 `paint.roads.road.fill`。
  数の部分は、リストの何番目か。例 `paint.roads.tunnel.dash.0`。
- `description`: 説明は、画面のマウスオーバーの解説。
- `control`: `color`・`number`・`choice`・`check`・`checks`（複数のチェック）・`font`・`text`・`table`。
- `unit`: `m`・`m2`・`deg`・`px`・`em`・`percent`。
  `percent` の値は、スタイルでは 0〜1 のまま。
  スタイルエディタは、100 倍して 0〜100 % で見せ、入力を 100 で割って保存する。
- `when`: `key` の値が `equals` のときだけ出す。
  なじませ方ごとの値と、材質ごとの色のスタイルと地域の色のスタイルで入れ替わる行に使う。
- `language`: `ja` は、日本語の地図を作るプロジェクトでだけ出す。例 `labels.fonts.ja`。
- `rebuilds`: プロジェクト画面の表の行。
  `cells` セル描画、`cells.prep` 前処理、`mapData.regions` 地域色、`mapData.labels` ラベル。
- `columns`: `id` と、行と同じ `name`・`description`・`control`・範囲・`choices`・`rebuilds`。

表は 4 つで、行の `key` で見分ける。

- `regions.zones`: 地区ごとの割り当て。
  列は、地域 = `regions.zones`、建物の色 = `buildings.byZone`、番地の大きさ = `labels.postal.sizeByZone`。
- `sea.bands`: 水深の帯。
  列は、深さ = `sea.bands`、砂の底 = `paint.sea.sand`、岩の底 = `paint.sea.rock`、
  不透明度 = `paint.sea.opacity`。
- `shade.lights`: 光。列は、方位・重み。
- `labels.street.expand`: 略語・書き戻す語。

同じ場所の行が 2 つあるもの（`paint.water`、`regions.zones`）は、`when` で分けてある。
スタイルの種類で、作り直すものが違うため。

`rebuilds` は、テスト（Core の `StyleSchemaTests`）で確認する。

- 同梱のアトラスのスタイルごとに、出る行の値を 1 つずつ変えて、
  セル描画・前処理・地域色・ラベルのそれぞれの判定と比較する。
  判定は、描画のハッシュ、セルの地図データの要るもの、地域色の節、ラベルの置き方の `key`。
- 同じテストが、次のことも確認する。
  - スタイルのすべての値に、行があること。
    `format`・`id`・`name`・`groundRaster.mode`・`canopy` を除く。
  - 出る行の値が、範囲に入ること。
