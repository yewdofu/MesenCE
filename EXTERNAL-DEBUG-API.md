# MesenCE External Debug API

MesenCE は、起動中の GUI を外部プロセス（MCP サーバー等）から操作・読み取りできるローカルデバッグ API を提供します。これは **コマンドラインオプション `--debugApi` を指定した場合のみ** 有効になる Named Pipe サーバーです。

本機能は現在 SNES 65816（`CpuType.Snes`）のみを対象としています。

---

## 起動方法

```sh
Mesen.exe --debugApi
```

- 固定パイプ名: `mesen-debug-api`
- Named Pipe は `PipeOptions.CurrentUserOnly` を指定して作成され、現在のユーザーアカウントで実行中のプロセスだけが接続できます（現在ユーザー限定）。
- 単一クライアントのみ接続可能。クライアントが切断するとサーバーは次の接続を待ち受けます（再接続可能）。切断されたセッションの保留中の通知・応答は破棄され、後続の新クライアントへは送られません。
- API クライアントが接続している間は「デバッガ利用者」として扱われます。最後のデバッガ画面が閉じても `ReleaseDebugger` は実行されません。切断時に UI のデバッガ画面が無ければ解放されます。

### 接続例（PowerShell）

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'mesen-debug-api', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect()
$writer = New-Object System.IO.StreamWriter($pipe)
$reader = New-Object System.IO.StreamReader($pipe)
$writer.AutoFlush = $true
$writer.WriteLine('{"jsonrpc":"2.0","id":1,"method":"system.getStatus","params":{}}')
$reader.ReadLine()
```

---

## プロトコル

- 改行区切りの **コンパクト JSON-RPC 2.0**。
- 各メッセージは 1 行の JSON。フィールド名は camelCase。
- 要求（Request）と応答（Response）に加え、サーバーから通知（Notification）が送られます。
- 通知・応答の書込みは排他され、要求処理は直列化されます。
- 要求は `id` を含み、応答は同じ `id` を返します。`id` を省略したメッセージは通知とみなされ処理はされますが応答は返しません。
- `jsonrpc` が `"2.0"` でない、`method` が空、または `id` が文字列・数値・null 以外の場合は不正な要求（`-32600`）になります。
- パースエラーは `id: null` の `-32700` で応答します。

### 標準エラーコード

| コード | 内容 |
|--------|------|
| -32700 | パースエラー |
| -32600 | 不正な要求 |
| -32601 | メソッドが見つからない |
| -32602 | パラメータ不正（範囲外・Base64 不正・enum 不正など） |
| -32603 | 内部エラー |
| -32001 | ROM がロードされていない |
| -32002 | 実行が停止していない（先に pause が必要） |
| -32003 | ブレーク待機がタイムアウト |
| -32004 | ブレークポイントが見つからない |
| -32005 | 未対応コンソール（SNES 以外の ROM をロード中） |

エラー応答例:

```json
{"jsonrpc":"2.0","id":2,"error":{"code":-32002,"message":"Execution is not stopped - pause the emulator first"}}
```

---

## 整合性と前提

- **レジスタ・命令・メモリの読み書き** など整合性が必要な操作は、**実行停止中のみ**許可されます（`-32002` でエラー）。先に `debug.pause` を呼んでください。
- ROM 未ロード時は `-32001` でエラーになります。
- 本 API は **SNES のみ**を対象としているため、SNES 以外の ROM をロード中にデバッガを必要とするメソッドを呼ぶと `-32005`（Unsupported console）でエラーになります。
- メモリの読み書きは 1 回あたり最大 **64 KiB** です。
- メモリデータは **Base64** でやり取りします。

---

## メソッド一覧

### system.getStatus

現在の状態を返します。

- パラメータ: `{}`（省略可）
- 応答:

```json
{"jsonrpc":"2.0","id":1,"result":{"romLoaded":true,"console":"Snes","running":true,"paused":false}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `romLoaded` | bool | ROM がロードされているか |
| `console` | string | コンソール種別（`Snes` など、未ロード時は空） |
| `running` | bool | エミュレーションが実行中か |
| `paused` | bool | 実行が停止中か |

---

### debug.pause

エミュレーションを一時停止します。新しい `event.break` 通知を受信するまで（タイムアウト付きで）待機してから応答します。

- パラメータ: `{}`（省略可）
- 応答: `system.getStatus` と同じ形式
- 既に停止している場合は即座に現在の状態を返します。
- エラー: `-32001`（ROM なし）、`-32003`（タイムアウト）

---

### debug.resume

エミュレーションを再開します。実際に実行が再開されたことを示す新しい `event.resumed` 通知を受信するまで（タイムアウト付きで）待機してから、応答の前に `event.resumed` 通知を送信してから現在の状態を返します。

- パラメータ: `{}`（省略可）
- 応答: `system.getStatus` と同じ形式
- 既に実行中の場合（停止していない場合）は即座に現在の状態を返します。
- エラー: `-32001`（ROM なし）、`-32003`（再開待機がタイムアウト）

---

### debug.step

1 命令ステップ実行します。新しい `event.break` 通知を受信するまで待機してから応答します。**実行停止中のみ**許可されます。

- パラメータ: `{}`（省略可）
- 応答: `debug.getCurrentInstruction` と同じ形式
- エラー: `-32001`（ROM なし）、`-32002`（停止していない）、`-32003`（タイムアウト）

---

### debug.getCurrentInstruction

現在の命令を返します。**停止中のみ**。

- パラメータ: `{}`（省略可）
- 応答:

```json
{"jsonrpc":"2.0","id":1,"result":{"pc":32768,"address":32768,"text":"LDA #$00","byteCode":"A9 00"}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `pc` | long | プログラムカウンタ |
| `address` | long | 逆アセンブル行のアドレス |
| `text` | string | 逆アセンブル表示テキスト |
| `byteCode` | string | バイトコード（hex） |

- エラー: `-32001`（ROM なし）、`-32002`（停止していない）

---

### cpu.getRegisters

SNES CPU レジスタを返します。**停止中のみ**。

- パラメータ: `{}`（省略可）
- 応答:

```json
{"jsonrpc":"2.0","id":1,"result":{"a":0,"x":0,"y":0,"sp":65280,"d":0,"pc":32768,"k":0,"dbr":0,"ps":32,"emulationMode":false}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `a` | int | A レジスタ（16bit） |
| `x` | int | X レジスタ |
| `y` | int | Y レジスタ |
| `sp` | int | スタックポインタ |
| `d` | int | D レジスタ |
| `pc` | int | プログラムカウンタ |
| `k` | int | K レジスタ（バンク） |
| `dbr` | int | データバンクレジスタ |
| `ps` | int | プロセッサステータス（フラグバイト） |
| `emulationMode` | bool | エミュレーションモード |

- エラー: `-32001`、`-32002`

---

### cpu.setRegisters

指定したフィールドのみ SNES CPU レジスタを更新します（部分更新）。**停止中のみ**。

- パラメータ: 更新するフィールドのみを含むオブジェクト

```json
{"jsonrpc":"2.0","id":2,"method":"cpu.setRegisters","params":{"pc":32768,"a":1,"ps":32}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `a` | int? | A レジスタ（0〜65535） |
| `x` | int? | X レジスタ（0〜65535） |
| `y` | int? | Y レジスタ（0〜65535） |
| `sp` | int? | スタックポインタ（0〜65535） |
| `d` | int? | D レジスタ（0〜65535） |
| `pc` | int? | プログラムカウンタ（0〜65535） |
| `k` | int? | K レジスタ / バンク（0〜255） |
| `dbr` | int? | データバンクレジスタ（0〜255） |
| `ps` | int? | プロセッサステータス（0〜255） |
| `emulationMode` | bool? | エミュレーションモード |

- 応答: `cpu.getRegisters` と同じ形式（更新後にネイティブから再取得した値）
- 各値はキャスト前に範囲検証され、範囲外は `-32602` になります。
- エラー: `-32001`、`-32002`、`-32602`

---

### memory.list

SNES の利用可能なメモリ領域一覧を返します。サイズが 0 の利用不可領域は除外されます。

- パラメータ: `{}`（省略可）
- 応答:

```json
{"jsonrpc":"2.0","id":1,"result":{"regions":[{"id":"SnesMemory","name":"SnesMemory","size":16777216},{"id":"SnesPrgRom","name":"SnesPrgRom","size":524288},{"id":"SnesWorkRam","name":"SnesWorkRam","size":131072},{"id":"SnesSaveRam","name":"SnesSaveRam","size":65536},{"id":"SnesVideoRam","name":"SnesVideoRam","size":65536},{"id":"SnesSpriteRam","name":"SnesSpriteRam","size":544},{"id":"SnesCgRam","name":"SnesCgRam","size":512},{"id":"SnesRegister","name":"SnesRegister","size":4}]}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `regions[].id` | string | メモリ種別名（`memory.read`/`memory.write` の `type` に指定） |
| `regions[].name` | string | 表示名 |
| `regions[].size` | long | サイズ（バイト） |

- エラー: `-32001`

---

### memory.read

メモリを読み取ります（Base64）。**停止中のみ**。最大 64 KiB。

- パラメータ:

```json
{"jsonrpc":"2.0","id":2,"method":"memory.read","params":{"type":"SnesWorkRam","address":0,"length":16}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `type` | string | メモリ種別（`memory.list` の `id`、大文字小文字は問わない） |
| `address` | long | 開始アドレス |
| `length` | int | 読み取るバイト数（1〜65536） |

- 応答:

```json
{"jsonrpc":"2.0","id":2,"result":{"address":0,"data":"AAECAwQFBgcICQoLDA0ODw=="}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `address` | long | 開始アドレス |
| `data` | string | Base64 エンコードされたデータ |

- エラー: `-32001`、`-32002`、`-32602`（種別不正・範囲外）

---

### memory.write

メモリへ書き込みます（Base64）。**停止中のみ**。最大 64 KiB。

- パラメータ:

```json
{"jsonrpc":"2.0","id":3,"method":"memory.write","params":{"type":"SnesWorkRam","address":0,"data":"AAECAwQFBgc="}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `type` | string | メモリ種別 |
| `address` | long | 開始アドレス |
| `data` | string | Base64 エンコードされたデータ（1〜65536 バイト） |

- 応答:

```json
{"jsonrpc":"2.0","id":3,"result":{"written":8}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `written` | int | 書き込んだバイト数 |

- エラー: `-32001`、`-32002`、`-32602`（種別不正・Base64 不正・範囲外）

---

### breakpoint.list

API クライアントが追加した外部ブレークポイント一覧を返します。

- パラメータ: `{}`（省略可）
- 応答:

```json
{"jsonrpc":"2.0","id":1,"result":{"breakpoints":[{"id":0,"cpu":"Snes","type":"exec","memoryType":"SnesMemory","address":32768,"enabled":true}]}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `breakpoints[].id` | long | API 用安定 ID |
| `breakpoints[].cpu` | string | CPU（`Snes`） |
| `breakpoints[].type` | string | `exec` / `read` / `write` / `readwrite` |
| `breakpoints[].memoryType` | string | メモリ種別 |
| `breakpoints[].address` | long | 開始アドレス |
| `breakpoints[].endAddress` | long? | 終了アドレス（単一アドレスの場合は null） |
| `breakpoints[].enabled` | bool | 有効か |

---

### breakpoint.add

外部ブレークポイントを追加します。

- パラメータ:

```json
{"jsonrpc":"2.0","id":2,"method":"breakpoint.add","params":{"type":"exec","address":32768}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `type` | string? | `exec` / `read` / `write` / `readwrite`（既定 `exec`、これ以外は `-32602`） |
| `memoryType` | string? | メモリ種別（既定 `SnesMemory`） |
| `address` | long | 開始アドレス（0〜4294967295、かつメモリサイズ内） |
| `endAddress` | long? | 終了アドレス（範囲の場合、`address` 以上） |
| `enabled` | bool? | 有効か（既定 true） |
| `condition` | string? | ブレーク条件式（UTF-8 で最大 999 バイト） |

- 応答: `breakpoint.list` の要素と同じ形式
- エラー: `-32001`、`-32602`（不正な type・負数・uint 超過・メモリサイズ超過・条件式が長すぎる）

外部ブレークポイントは **UI 用とは別の非永続リスト** として管理され、コアへは UI / assert / temporary / external を統合して送られます。接続切断時、または API サーバー停止時に自動削除されます。ROM 再ロード時にも再適用されます。

---

### breakpoint.remove

外部ブレークポイントを削除します。

- パラメータ:

```json
{"jsonrpc":"2.0","id":3,"method":"breakpoint.remove","params":{"id":0}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `id` | long | `breakpoint.add` / `breakpoint.list` で返される ID（int 範囲内である必要あり） |

- 応答: `true`
- エラー: `-32004`（見つからない）、`-32602`（ID が int 範囲外）

---

## 通知（Notification）

サーバーは以下の通知を送信します（`id` なし）。

### event.break

実行が停止したとき（pause / step / ブレークポイント）。

```json
{"jsonrpc":"2.0","method":"event.break","params":{"breakType":"breakpoint","cpu":"Snes","pc":32768,"breakpointId":5}}
```

| フィールド | 型 | 説明 |
|-----------|----|------|
| `breakType` | string | `pause` / `step` / `breakpoint` / `break` |
| `cpu` | string | ソース CPU（`Snes`） |
| `pc` | long | 停止後の SNES PC（実行停止後に取得） |
| `breakpointId` | long? | 外部ブレークポイントの API 安定 ID（該当時のみ）。UI/assert/一時ブレークポイントの場合は null |

### event.resumed

実行が再開されたとき。

```json
{"jsonrpc":"2.0","method":"event.resumed","params":{"cpu":"Snes"}}
```

### event.gameLoaded

ゲーム（ROM）がロードされたとき。

```json
{"jsonrpc":"2.0","method":"event.gameLoaded","params":{"romName":"MyGame","consoleType":"Snes"}}
```

### event.emulationStopped

エミュレーションが停止したとき。

```json
{"jsonrpc":"2.0","method":"event.emulationStopped","params":{}}
```

---

## 実装ノート

- JSON シリアライズは `System.Text.Json` のソース生成（`JsonSerializerContext`）を使用しており、NativeAOT 制約に準拠しています。
- 通知コールバック（エミュレーションスレッド上で実行）ではネイティブポインタ内容を値コピーしてキューに投入するだけにとどめ、ROM 情報の取得・JSON シリアライズ・パイプ送信・待機は行いません。送信は専用の通知ワーカースレッドが行います。
- 通知は `ConcurrentQueue` と起床用 `SemaphoreSlim` で管理され、専用ワーカーがリクエストが無くても即時送信します。未接続時は通知を enqueue せず、キューが無制限に増えません。`debug.pause` / `debug.step` は応答の前に保留中の `event.break` 通知を送信し、順序を保証します。`debug.resume` は `DebuggerResumed` / `GameResumed` 通知の新しい世代を `SemaphoreSlim` + カウンタで待ち、通知を Drain して `event.resumed` を応答前に送信してから現在の状態を返します。既に実行中なら即時応答します。
- 通知処理と要求処理は **API ゲート（単一の `SemaphoreSlim`）で直列化**され、`DebugApi` / `BreakpointManager` の操作が要求中に通知と競合しません。要求ワーカーは「通知 Drain → 要求処理 → 通知 Drain」を API ゲート取得中に実行します。通知コールバックは API ゲートを取得しないため、pause/step 中でも break 用セマフォを解放でき、要求ワーカー自身がキュー上の `event.break` を応答前に送信します。要求処理はゲート取得の前後で接続世代と接続状態を再確認し、切断済みセッションのキュー済み要求が応答なしで実行されるのを防ぎます。切断時・終了時は世代無効化後に API ゲートを取得して外部ブレークポイント除去・デバッガ解放・残留通知/break 状態の掃除を行い、次接続前に旧セッションの後片付けを完了させます。
- `event.break` の `breakpointId` は、`BreakpointManager` が `SetBreakpoints` 時に構築する明示的なコア ID → API ID マップを使って解決されます（辞書の列挙順には依存しません）。
- 接続世代 ID によってセッションが分離され、切断されたクライアントの保留中の通知・要求・遅延応答は後続の新クライアントへ送られません。切断時は旧世代のキュー項目を Drain 時に破棄し、キューが無制限に残りません。
- ブレークポイントは `BreakpointManager` の外部リスト（API 用安定 ID ↔ コア `BreakpointId` 対応付け）として管理され、UI 側のイベント解決を壊しません。UI 側リストの構造変更と `SetBreakpoints` のスナップショットは共通構造ロックで保護されます。`SetBreakpoints` 全体（スナップショットからネイティブ `DebugApi.SetBreakpoints` まで）は専用ロックで直列化され、複数呼び出しが古いスナップショットを後からコアへ送る競合を防ぎます。
- ゲーム（ROM）ロード時、クライアント接続中はデバッガを再初期化し、外部ブレークポイントを即座にコアへ再適用します（次の API リクエストまで保留しません）。
