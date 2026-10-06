# リソースとの行の形式（プロトコル 1）

FxMapGenerator（exe）と、サーバーに置く FiveM リソース `fxmapgen-capture` のやりとりの仕様。

コードの正は、次の 2 つ。

- `resource/fxmapgen-capture/client/link.lua` の冒頭の一覧。
- exe の `src/FxMapGenerator.Core/Capture/GameLink.cs`。

## 接続

- exe は、FiveM クライアントのコンソールソケット（既定 `127.0.0.1:29200`）に接続する。
  ゲームのコンソールにコマンドを打つのと同じ形で、`fxmapgen <サブコマンド> ...` を送る。
  ゲームの外（NUI やブラウザ）は、使わない。
- リソースは、応答を `print` する。exe は、ゲームのコンソールに流れる行を、すべて読む。
- ゲームのコンソールへの接続は、同時に 1 つだけ。FxDeck などと同時には使えない。
- ゲームは、しばらく（約 5 秒）何も送られてこないと、接続を切る。
  exe は、応答を待つ間、3 秒ごとに `fxmapgen ping` を送る。

## 行の形

```
[fxmapgen] READY seq=12 x=219.3750 y=-740.6250 gz=46.077 h=8539.785 ...
```

- リソースが出す行は、どれも `[fxmapgen] ` で始まる。
- 接頭辞の後が英大文字の語で始まる行は、プログラム向け。
  `キー=値` を、空白で区切って並べる。値に空白は入らない。
- 知らないキーは、読み飛ばす。キーを足しても、前の exe は壊れない。
- それ以外（小文字で始まる行）は、ログ向けの注記。exe は、会話の記録に残すだけ。
- 使えないとき・使い方の誤りは、`ERROR <理由>`。理由は、英語の文。

## 版

- プロトコルの番号は 1。`HELLO` の `proto=` で確認し、違えば、exe は撮影しない。
- リソースの版（`fxmanifest.lua` の `version`）は、exe の版と同じにする。
  exe は、`HELLO` の `ver=` が自分の版と違えば、撮影しない。
  リソースは exe の中に入っていて、画面からフォルダに書き出すか、zip で受け取る。
- 行やキーを変えるときは、この番号を上げるか、足すだけにする（上の「知らないキーは読み飛ばす」）。

**取り方の番号**（`HELLO` の `capture=`）

- 撮影・採取で取る中身を変えるリソースの変更のたびに、1 つ上げる。
  中身を変える変更は、光線の範囲、待ち方、取る値。
- exe は、ブロックの項目ごとに、取ったときのこの番号を記録する
  （`project-format.ja.md` の `state/blocks.json`）。
  後で取り方が変わったとき、その変更が効くブロックだけを、取り直せるようにするため。

| 番号 | 取り方 |
|---|---|
| 1 | 地面の採取の光線は 1200 m から −500 m まで |

## コマンド

| コマンド | 応答の行 | 中身 |
|---|---|---|
| `fxmapgen hello` | `HELLO` | リソース・サーバー側・権限・人数を確認する |
| `fxmapgen status` | `STATUS` | 今の状態 |
| `fxmapgen res [名前 ...]` | `RES` | リソースの状態 |
| `fxmapgen ping` | なし | 接続を保つだけ |
| `fxmapgen env on` | `ENV on` | 撮影の環境を入れる（下） |
| `fxmapgen env off` | `ENV off`、`REFILL` | 撮影の環境を外す |
| `fxmapgen safe [x y [z]]` | `SAFE` | キャラクターを、地面に下ろす |
| `fxmapgen tile <z> <tx> <ty> <fov> [margin] [quietMs]` | `READY` / `FAIL` | ブロックの真上に、カメラを置く |
| `fxmapgen cam <x> <y> <h> <fov>` | `READY` / `FAIL` | ブロックではない地点の真上 |
| `fxmapgen hmap [step]` | `HMAP` | 今のブロックの高さの格子 |
| `fxmapgen scan ground 8 <tx> <ty> [canopy]` | `MSCAN` | ブロックの地面の採取 |
| `fxmapgen scan roads 8 <tx> <ty>` | `MSCAN` | ブロックの道の採取 |
| `fxmapgen scan stop` | `MSCAN ABORT` | 採取をやめる |

- `hello`: サーバーの応答を、3 秒まで待つ。
- `status`: 撮影の環境、要求番号、天候・時刻、NPC と車の数、キャラクターの位置。
- `res`: 応答は、`RES BEGIN` → `RES` × n → `RES END`。
  名前を挙げたリソース（なければ全部）の状態。
- `env off`: 応答は、`ENV off ...` → `REFILL ...`。
  キャラクターを、元の場所に下ろす。
- `safe`: 応答は、`SAFE ...`。
  場所を指定したときは、その場所の地面に下ろす。
  撮影の環境が入っている間は、使えない。
- `tile`: 落ち着くのを待つ。
- `cam`: 事前確認などで使う。
- `hmap`: 応答は、`HMAP BEGIN` → `HMAP j=...` → `HMAP END`。または `HMAP ABORT`。
- `scan ground`: 応答は、`MSCAN BEGIN` → 行 → `MSCAN END` → `MSCAN DONE`。または `MSCAN ABORT`。
  取るのは、材質・当たった高さ・水面・水の当たり判定。`canopy` で、木の茂みも。
- `scan roads`: 応答は、`scan ground` と同じ。
  取るのは、道路の上か（1 m ごと）、通り名と地区（4 m ごと）。
- `scan stop`: 走っている採取が、`MSCAN ABORT` で終わる。

`tile` の引数:

- `z tx ty`: ブロックを、ズーム `z`（6〜11）のタイル `(tx, ty)` を含む 4×4 タイルとして指す。
  - exe は、z8 だけを使う。z8 のブロックは、281.25 m 四方。
  - 番号の原点は、標準の地図の枠の、北西の角。
    標準の地図の枠は、x −4140〜4860、y 8400〜−5100（ゲームの m、y は北が正）。
  - プロジェクトの枠が標準の枠の西か北に広がるときは、マイナスになる
    （`project-format.ja.md` の「地図の枠・ブロック・名前」）。
- `fov`: カメラの縦の視野角（度）。exe は、2 を使う。
- `margin`（既定 1）: 画面の縦がブロックの `margin` 倍になる高さに、カメラを置く。
  - ブロックの外も少し写して、傾いて写る屋根や斜面を、オルソ補正で拾うため。
  - exe は、1.06 を使う。
  - カメラの高さは、`h = margin × (一辺 / 2) / tan(fov / 2)`。
- `quietMs`（0〜10000、既定 1500）: ゲームの読み込み要求が 0 のまま、これだけ続いたら、落ち着いたとみなす。
  あわせて、最低 10 フレーム。
  exe は、事前確認でこの PC に要る長さを測り、それを送る。

## 撮影の環境（env on / off）

**`env on`**

- NPC と車なし。
- HUD とレーダーなし。
- 正午、快晴。
- 雲なし。雲の層を外す。透明にするだけでは、真上の雲が水面に映る。
- 霧なし。
- 露出の固定。自動露出の下限と上限を、EV −3.0 に。
- 乾いた地面。雨で濡れた地面が、すぐ乾くようにする。
- ほかのスクリプトが始めた、時間をかけた天候の変化は、毎フレーム、すぐ終わる変化で置き換えて止める。
- キャラクターを隠して固定し、体力を保つ。
- 毎フレーム、画面の左上に、ビーコン（下）を描く。

**止めるリソース**

- 画面に描くリソースや、天候を持つリソースは、exe が `env on` の前にクライアントで止める。
  `env off` の後に、`ensure` で戻す。
  対象は、HUD、チャット、天候の同期、NPC の密度。
- どれを止めるかは、サーバーのプリセットと、プロジェクトの設定。

**このリソース自体の起動と停止**

- 起動と停止も、exe がゲームのコンソールから送る。
- `hello` に応答しないときは、`refresh` と `ensure fxmapgen-capture`（画面の「リソースを起動」）。
- 範囲のどのブロックも取り終えたら、止めたリソースを戻した後に、`stop <res>` を送る。
  `<res>` は、`hello` の `res` の名前。
  その後、`hello` に応答しなくなったことを確認する。
- 途中で止めた・失敗があったときは、止めない。
- どれもサーバーのコマンドで、送るプレイヤーに、その権限が要る。

**`env off`**

- 上をすべて外し、キャラクターを、`env on` の前の場所へ戻す。
- 真下への光線が、立っていた高さのすぐ下（2 m 以内）で、当たり判定か水面に当たるまで、固定したまま待つ。
  それから、固定を解く。
  - 読み込まれていない所で解くと、落ちるため。
  - 床や屋根の当たり判定は、その下の地面より遅れて来ることがある。
    先に当たった地面に下ろすと、建物の下に出る。
- 10 秒待っても、すぐ下に当たらなければ、それより下で当たった所に下ろす。
- 何にも当たらなければ、固定のまま `safe=0`。
- `safe` も同じ。
- 撮影の途中でリソースが止められたときは、キャラクターを、元の場所で固定したままにする。
  起動し直して、`fxmapgen safe`。

## 要求番号とビーコン

- 要求番号 `seq` は、`tile` / `cam` のたびに、1 つ増える。
  - `env off` では、増えない。
  - 途中の要求は、`env off` でも新しい要求でも、`FAIL reason=superseded` になる。
  - `READY` / `FAIL` / `HMAP BEGIN` に載る。
- 採取には、これとは別の、採取の番号がある。
  `scan` のたびに 1 つ増えて、`MSCAN` の `BEGIN` / `END` / `DONE` / `ABORT` の `seq` に載る。
- exe は、`tile` を送る前の番号より大きい番号の `READY` / `FAIL` だけを、その要求の応答とする。
  撮り直しの前は、`status` で今の番号を確認する。
  待ちきれずに後から届いた前の応答は、取らない。
- ビーコン: 1920×1080 の画面の左上、上の辺に沿って、4×4 px の四角が 4 つ。

  | 四角 | 意味 |
  |---|---|
  | 0 | 準備済み（今の要求が READY）なら緑 (0, 255, 0)、まだなら赤 (255, 0, 0) |
  | 1〜3 | 要求番号の bit 0〜2。1 なら白、0 なら黒 |

  exe は、撮った画面のビーコンで、その要求が準備済みになった後の画面かを見分ける。
  黒い画面や、前のブロックの画面は、撮り直す。

## 応答の行

**HELLO**

```
HELLO proto=1 ver=0.1.0 res=fxmapgen-capture server=0.1.0 ace=1 players=1 build=3258 capture=1
```

| キー | 意味 |
|---|---|
| `proto` | プロトコルの番号 |
| `ver` | リソース（クライアント側）の版 |
| `res` | リソースの名前 |
| `server` | サーバー側の版。3 秒で応答がなければ `none` |
| `ace` | このプレイヤーに権限 `command.fxmapgen` があるか（0 / 1） |
| `players` | サーバーにいるプレイヤーの数（応答がなければ −1） |
| `build` | ゲームのビルド番号 |
| `capture` | 取り方の番号（上の「版」） |

**STATUS**

```
STATUS env=0 ready=0 busy=0 hmap=0 scan=0 seq=0 weather=EXTRASUNNY hour=12 minute=0 peds=61 vehicles=94 near_peds=12 near_vehicles=20 health=200 dead=0 frozen=0 px=195.8 py=-934.9 pz=30.7 block=none
```

- `env`・`ready`・`busy`・`hmap`・`scan`・`dead`・`frozen` は、0 / 1。
  - `busy`: tile / cam の途中。
  - `hmap`: 高さの格子を送っている途中。
  - `scan`: 採取の途中。
- `weather` は、天候の名前。分からなければ、ハッシュの 16 進。
- `peds` / `vehicles` は、クライアントが知っている NPC と車の数。
  `near_*` は、600 m 以内の数。
- `px py pz` は、キャラクターの位置。
- `block` は、最後の tile のブロック。

**RES**

```
RES BEGIN n=2
RES qbx_hud started
RES chat stopped
RES END
```

状態は、ゲームの `GetResourceState` の値（`started`、`stopped`、`missing` など）。

**ENV / SAFE / REFILL**

```
ENV on
ENV off safe=1 x=195.8 y=-934.9 z=30.7
ENV off already=1
SAFE ok=1 x=-145.5 y=6395.2 z=30.4
REFILL ok=1 via=qbx_core
REFILL ok=0 via=none error=noserver
```

- `safe` / `ok` は、キャラクターを地面に下ろせたか。
- `REFILL` は、`env off` の後に、サーバーが空腹と渇きを満たした結果。
  `via` は、`qbx_core`、`qb-core`。どちらもなければ、`none`。

**READY**

```
READY seq=12 x=219.3750 y=-740.6250 gz=46.077 h=8539.785 fov=2.000 margin=1.060 scene=1255 coll=0 settle=399 quiet=0 fps=60.2 maxreq=47 water=0 veh=0 peds=0 block=z8_60_128
```

| キー | 意味 |
|---|---|
| `seq` | 要求番号 |
| `x`, `y` | カメラの真下の点（ブロックの中心） |
| `gz` | その点の地面（水の上では水面、外洋は 0） |
| `h` | 地面からのカメラの高さ（カメラは `gz + h`） |
| `fov`, `margin` | 受けた引数 |
| `scene` | 場面の読み込みを待った ms |
| `coll` | 中心の真下の当たり判定を待った ms（水の上は待たない） |
| `settle` | 落ち着きを待った ms |
| `quiet` | 落ち着き待ちに使った `quietMs` |
| `fps` | 落ち着き待ちの間の、1 秒あたりのフレーム数 |
| `maxreq` | 落ち着き待ちの間に見た、読み込み要求の最大 |
| `water` | ブロックの 5×5 点のうち、水の点の数 |
| `veh`, `peds` | サーバーが消した車と NPC の数 |
| `block` | ブロックの名前（`cam` では `none`） |

`water` は、25 = 外洋。すべて水なら、場面の読み込み待ちを 2.5 秒で打ち切る。

**FAIL**

```
FAIL seq=13 reason=timeout scene=20000 coll=0 settle=0
FAIL seq=14 reason=refused why=players players=2
FAIL seq=15 reason=noserver
FAIL seq=16 reason=superseded
```

| `reason` | 意味 |
|---|---|
| `timeout` | 待ちの上限を超えた |
| `refused` | サーバーが、ワールドの掃除を拒否した |
| `noserver` | サーバー側が応答しなかった |
| `superseded` | 新しい要求か `env off` が、この要求に取って代わった |

- `timeout`: 場面の読み込み 20 秒、当たり判定 10 秒、落ち着き 15 秒のどれかを超えた。
- `refused`: `why=permission` は権限なし、`why=players` はほかのプレイヤーがいる。

**HMAP**

```
HMAP BEGIN seq=12 z=8 tx=60 ty=128 x0=78.7500 y0=-600.0000 size=281.2500 step=1.000 n=282 block=z8_60_128
HMAP j=0 k=0 43.2 43.1 43.1 ...（100 個まで）
HMAP j=0 k=1 ...
HMAP j=0 k=2 ...
...
HMAP END nohit=18 ms=1578
```

- ブロックの北西の角 `(x0, y0)` から `step` m ごとに、東へ `i`、南へ `j`（0〜`n − 1`）の点の高さ。
- 1 行に 100 個まで。`k` は、行の中の何番目の 100 個か。
  連長で縮めないので、1 行は長くても 820 文字ほど。ゲームが行を切る 1,023 文字に届かない。
- exe は、受け取った格子を確認する。
  値でない字、来なかった行、値の数が `n` に合わない行があれば、不完全として、そのブロックをやり直す。
- 値は、見えている面の高さ（地面と水面の高い方、0.1 m）。
  当たらなかった点は `x`。`END` の `nohit` が、その数。
- 送っている途中で `env off` か次の要求が来ると、`HMAP ABORT` で終わる。
  `END` がないので、exe は不完全として捨てる。
- 高さの格子を送っている間、`tile` / `cam` / `hmap` / `scan` は、`ERROR busy: ...` になる。

**MSCAN（採取）**

- キャラクターが自分で動いて読み込ませるので、カメラも `tile` も要らない。
  撮影に続けて、同じブロックで取ってもよい。
- 撮影の環境が入っていなければ、入れる。
- ブロックは、z8 だけ（`8 <tx> <ty>`、tile と同じ指し方）。
- 受けるのは、どのプロジェクトの枠も入る範囲の、z8 のタイルの番号。
  `tx` −128〜255、`ty` −64〜255。
  標準の枠の 0〜127・0〜191 に、左右に 4 セル・上下に 2 セルまで。
  その外は、使い方の誤り（`ERROR usage: ...`）。
- 行の形は、形式 v=1（`BEGIN` の `v=1`）。

```
MSCAN BEGIN v=1 kind=mat seq=1 block=z8_60_132 z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=1.000 n=282 flags=1 fol=1 chunks=2 pflags=128 settle=300 zmin=-500
MSCAN dict mat 1 282940568
MSCAN mat j=0 k=0 1*40 2*12 1*230
MSCAN hz j=0 k=0 33.5*12 53.5*28 ...
MSCAN water j=0 k=0 .*282
MSCAN fol j=0 k=0 x*282
MSCAN chunk 0,0 surf=33.8 wait=1025 coll=1 quiet=1 req=0
...
MSCAN END kind=mat seq=1 n=282 cells=79524 nohit=0 water=1600 fol=1600 fol2=0 mats=4 scan_ms=950 emit_ms=16 ms=5230
MSCAN DONE seq=1 kind=ground block=z8_60_132 ms=5230
```

地面（`scan ground`）の取り方:

1. ブロックを、2×2 の区画に分ける。
2. 区画ごとに、固定したキャラクターを区画の中心の上に置いて、場面を読み込ませる。
   水だけのブロックは、2.5 秒で打ち切る。
3. 地面（水があれば水面）を見つける。
4. 真下への光線が当たり判定に当たるまで、待つ。水の上は、待たない。
5. 続けて、キャラクターのまわりの当たり判定が読み込まれたとゲームが応答するまで、待つ。3 秒まで。
   - 水だけのブロックは、待たない。
   - 建物の当たり判定は、地面より後に来ることがある。光線だけを待つと、屋根を取りこぼすため。
6. さらに、ゲームの読み込み待ちの数が 0 のまま 0.3 秒続くまで、待つ。5 秒まで。
   - 遠くから飛んだ直後は、まわりを読み込んだと応答した後にも、家の当たり判定が来ることがあるため。
   - 水だけのブロックは、ただ 0.3 秒置く。
7. 区画の点を、1 m ごとに調べる。
   点は、北西の角 `(x0, y0)` から東へ `i`、南へ `j`（0〜`n − 1`）。

地面の行:

- `mat`: 真上から下への光線が当たった、材質の番号。
  - 光線は、`flags=1`、地図の当たり判定。1200 m から −500 m まで。
  - 番号は、`dict mat <番号> <ハッシュ>` で、初めて当たったときに振る。0 = 当たりなし。
- `hz`: その当たった高さ（0.1 m、`x` = 当たりなし）。
- `fol`: 水の当たり判定（`flags=128`: 川・プール・噴水。海は当たらない）の高さ。
- `fol2`: `canopy` のときだけ、木の茂み（`flags=256`）の高さ。`BEGIN` に `pflags2=256`。
- `water`: ゲームの水面の高さ（波なし。`.` = 水なし）。
- `chunk <ci>,<cj> surf= wait= coll= quiet= req=`: 区画ごとの値。
  - `surf`: 見つけた面の高さ。
  - `wait`: 読み込みに待った ms。
  - `coll`: まわりの当たり判定。
    `1` = 読み込まれたと応答した、`0` = 3 秒のうちに応答しなかった、`-` = 聞いていない（水だけのブロック）。
  - `quiet`: 読み込み待ちが 0 のまま続いたか。
    `1` = 続いた、`0` = 5 秒のうちに続かなかった、`-` = 待っていない（水だけのブロック）。
  - `req`: 光線を始めるときの、読み込み待ちの数。

道（`scan roads`）の取り方:

- ブロックの中心で 1 度読み込ませてから、取る。
- `step=4` m ごとに、通り名と地区。
  - 通り名（`street`）: `dict street <番号> <ハッシュ> <名前>` の番号。道のない所は、ハッシュ 0。
  - 地区（`zone`）: `dict zone <番号> <コード> <表示名>`。
- `pstep=1` m ごとに、道路の上か（`onroad`: 0 / 1）。
- `BEGIN` の `n` / `pn` は、それぞれの 1 辺の点の数（71 と 282）。

```
MSCAN BEGIN v=1 kind=road seq=2 block=z8_60_132 z=8 tx=60 ty=132 x0=78.7500 y0=-881.2500 size=281.2500 step=4.000 n=71 pstep=1.000 pn=282
MSCAN dict street 1 -1234567 Fake St
MSCAN dict zone 1 LEGSQU Legion Square
MSCAN street j=0 k=0 1*12 2 1*58
MSCAN zone j=0 k=0 1*43 2*28
MSCAN onroad j=0 k=0 0*20 1*13 0*249
MSCAN END kind=road seq=2 n=71 streets=3 zones=2 onroad=19008 pn=282 wait=1020 scan_ms=640 ms=1700
MSCAN DONE seq=2 kind=roads block=z8_60_132 ms=1700
```

- 1 行の値は、連長で縮める（`値*個数`）。
- ゲームは、クライアントが出した 1 行を、1,023 文字で切る。
  そのため、値の部分が 900 文字を超える所で、`k` を 0 から増やして分ける。
  深い海底で高さが細かく変わる所では、120 個で 1,080 文字になる。
- exe は、受け取った採取を確認する。
  次のものがあれば、不完全として、そのブロックをやり直す。
  - 値でない字。行の途中で切れて残った `-` など。
  - 来なかった行。
  - 点の数が `n` に合わない行。道の行は、それぞれの `n` / `pn`。
- `scan stop`、`env off` で止まると、`MSCAN ABORT seq= kind= block=` で終わる。
  `END` も `DONE` も来ない。exe は、不完全として捨てる。
- 採取の間、`tile` / `cam` / `hmap` / `scan` は、`ERROR busy: a scan is running` になる。
  `tile` / `cam` の途中の `scan` は、`ERROR busy: a tile request is on its way`。
- 道のリンクは、採取しない。ゲームファイルの ynd から取る。
  取り込んだ採取に、道のリンクの行（`edge`）があれば、読むときに飛ばす。

## サーバー側

クライアントは、次のネットイベントで、サーバー側に問い合わせる。
`fxmapgen:reply`（問い合わせの番号と応答）を受ける。

| イベント | 中身 |
|---|---|
| `fxmapgen:hello` | サーバー側の版、権限、プレイヤーの数 |
| `fxmapgen:clearworld` | サーバー上の車と NPC を、すべて消す |
| `fxmapgen:refill` | 空腹と渇きを満たす（`qbx_core`、なければ `qb-core`） |

`fxmapgen:clearworld` は、権限 `command.fxmapgen` がないときと、ほかのプレイヤーがいるときは、拒否する。

権限:

- `add_ace group.admin command allow` のように、`command` を許されたグループは、そのまま使える。
- ほかは、`add_ace <principal> command.fxmapgen allow`。

## exe が書くもの

- `capture/<block>.cam.txt`: その撮影の `READY` の行そのまま（接頭辞つき、1 行）。
- `capture/<block>.hmap`: `HMAP BEGIN` から `HMAP END` までの行（接頭辞なし）。
- `scan/<block>.txt`: `MSCAN` の行（地面と道の採取。読むときは、行の頭を見ない）。
- `capture/<block>.png`: 撮った画面（1920×1080）。
- 実行のフォルダの `console.log`: 送ったコマンドと、届いた行。
  - 高さの格子の行は、BEGIN と END だけ。
  - 採取も、値の行 `MSCAN <種類> j=…` は入れない。BEGIN・辞書・区画・END・DONE だけ。

作業フォルダの形は、[project-format.ja.md](project-format.ja.md)。
