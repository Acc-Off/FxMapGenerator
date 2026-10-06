# fxmapgen-capture

[English](README.md)

[FxMapGenerator](https://github.com/Acc-Off/FxMapGenerator) のゲーム側のリソースです。真上からの撮影と高さの格子を
取り、FxMapGenerator がそれを衛星地図にします。アトラス地図と道路地図のために、地面と道の採取（材質、水、通り名と地区）
も行います。操作は FxMapGenerator がゲームのコンソールから行い、起動と停止も FxMapGenerator が行います。

## 入れ方

1. このリソースは FxMapGenerator に同梱しています。FxMapGenerator の「FiveM 設定・接続検証」画面の「サーバー用
   リソース」で、`fxmapgen-capture` フォルダを選んだフォルダに書き出します。サーバーが同じ PC か共有フォルダなら、
   サーバーの `resources` フォルダを選びます。別の PC のサーバーには、zip をダウンロードしてサーバーの `resources`
   フォルダに展開します。`resources/[fxmapgen]/` のような分類のフォルダの中でもかまいません。フォルダ名は
   `fxmapgen-capture` のままにしてください（FxMapGenerator がこの名前で起動・停止します）。
2. FiveM でサーバーに入ったら、同じ画面の「リソースを起動」で、ゲームのコンソールから `refresh` と
   `ensure fxmapgen-capture` を送ります。`server.cfg` に書く必要はありません。サーバーのコマンドを送る権限がないときは、
   `server.cfg` に `ensure fxmapgen-capture` を書いてサーバーを起動し直してください。
3. 撮影するプレイヤーには権限 `command.fxmapgen` が要ります。`command` を許された管理者（よくある
   `add_ace group.admin command allow`）は、すでに持っています。ほかの人に許すには:
   `add_ace identifier.fivem:<id> command.fxmapgen allow`

撮影・採取が最後まで済むと、FxMapGenerator がリソースを止めます（`stop fxmapgen-capture`）。途中で止めたときは
動いたままにするので、続きをすぐ始められます。フォルダはサーバーに残ります。要らなくなったら消してください。

## サーバーに対して行うこと

- カメラをブロックの上に置くたびに（撮影か高さデータのとき）、**サーバー上のすべての車両と NPC を消します**（ほかのプレイヤーが置いた車両や NPC も含みます）。ほかに誰かが
  サーバーにいる間は、消さずに撮影を断ります。サーバーの複製か、ほかに誰も遊んでいない時間に撮影してください。
- 撮影の間、撮影するプレイヤーのキャラクターは見えなくなり、固定され、倒れないように保たれます。終わると元の場所に
  戻り、空腹とのどの渇きが満たされます（Qbox と QBCore）。

## 撮影が途中で止まったとき

FxMapGenerator が途中で止まり、キャラクターが見えないまま、または固定されたままのときは、ゲームのコンソール（F8）に
次のように入力します。

```
fxmapgen env off
```

`already=1` と返ったときは、次のように入力します。

```
fxmapgen safe
```

キャラクターは、立っていた所のすぐ下の床か地面が読み込まれるまで（10 秒まで）固定されたまま待ち、読み込まれてから立ちます。
読み込まれなければ、それより下で見つかった所に下ろします。何も見つからなければ固定されたままです（`safe=0`。もう一度
`fxmapgen safe` を入力します）。
