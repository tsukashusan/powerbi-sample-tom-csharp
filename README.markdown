# powerbi-sample-tom-csharp

このリポジトリは、Power BI のセマンティックモデルに対して、SQL Server を使う Structured DataSource と DirectQuery テーブルを TOM / TMSL / TMDL で更新するコンソールアプリです。

上から順に読めば、初めてこのコードを見る C# に慣れた人でも、何を設定し、どう実行するかを追えるようにしています。

## 何をするアプリか

このアプリは次の流れで動きます。

1. `.env` を読み込む。
1. Power BI ワークスペースへ XMLA 接続する。
1. 対象セマンティックモデルを開く。
1. Structured DataSource を作成または再利用する。
1. 必要な DirectQuery テーブルを作成または更新する。
1. 選択したモードでモデル更新を保存する。

更新モードは `MODEL_UPDATE_MODE` で切り替えます。

## 実行前に必要なもの

- Power BI ワークスペースに XMLA 接続でアクセスできること
- 対象データセットを更新できる権限があること
- `.env` に接続情報と対象モデル情報があること

## まず設定するファイル

このアプリは [fabric-tom/.env.example](fabric-tom/.env.example) を元に [fabric-tom/.env](fabric-tom/.env) を作って使います。

`.env` は実行時設定です。ソースコードを変更せずに接続先や動作を変えられます。

### 必須設定

| Key | 内容 |
| --- | --- |
| `WORKSPACE_NAME` | Power BI のワークスペース名 |
| `TENANT_ID` | Entra ID のテナント ID |
| `APP_ID` | XMLA 接続に使うアプリケーション ID |
| `APP_SECRET` | XMLA 接続に使うアプリケーションシークレット |
| `TARGET_DATASET_NAME` | 更新対象のセマンティックモデル名 |
| `SQL_SERVER` | 更新先 SQL Server 名 |
| `SQL_DATABASE` | 更新先 SQL Database 名 |

### 認証設定

`SQL_AUTH_MODE` で SQL への接続方法を選びます。

| 値 | 意味 |
| --- | --- |
| `SqlPassword` | SQL ユーザー名とパスワードで接続する |
| `EntraPassword` | Entra ID のユーザー名とパスワードで接続する |
| `EntraInteractiveMfa` | 対話的にサインインする |
| `EntraServicePrincipal` | サービスプリンシパルで接続する |

必要な追加キーはモードによって変わります。

| Key | 内容 |
| --- | --- |
| `SQL_USER` / `SQL_PASSWORD` | `SqlPassword` 用 |
| `SQL_ENTRA_USER` / `SQL_ENTRA_PASSWORD` | `EntraPassword` 用 |
| `SQL_ENTRA_CLIENT_ID` / `SQL_ENTRA_CLIENT_SECRET` | `EntraServicePrincipal` 用 |
| `SQL_ENTRA_TENANT_ID` | サービスプリンシパルで別テナントを使う場合 |

### DirectQuery 設定

1 つのテーブルを定義する場合は以下を使います。

| Key | 内容 |
| --- | --- |
| `SQL_DIRECTQUERY_TABLE_NAME` | ローカルテーブル名 |
| `SQL_DIRECTQUERY_PARTITION_NAME` | パーティション名 |
| `SQL_DIRECTQUERY_QUERY` | SQL クエリ |
| `SQL_DIRECTQUERY_COLUMNS` | 列定義 |

複数テーブルを追加する場合は `SQL_DIRECTQUERY_TABLE_COUNT` と `SQL_DIRECTQUERY_TABLE_1_*` のような個別定義を使います。

`SQL_DIRECTQUERY_COLUMNS` は `名前:型:元列名` をセミコロン区切りで並べます。たとえば次のように書きます。

```dotenv
SQL_DIRECTQUERY_COLUMNS=ProductKey:Int64:ProductKey;ProductName:String:ProductName
```

## モデル更新モード

モデル更新の方式は `MODEL_UPDATE_MODE` で切り替えます。

| 値 | 意味 |
| --- | --- |
| `TOM` | TOM API でそのまま更新する |
| `TMSL` | TMSL の createOrReplace を使う |
| `TMDL` | TMDL フォルダ API を使う |



## TMDL の使い方

`MODEL_UPDATE_MODE=TMDL` にすると、アプリは次の順番で動きます。

1. 対象モデルを TMDL フォルダに serialize する。
1. そのフォルダから model を deserialize する。
1. `dataSources.tmdl` 内の Structured DataSource を更新する。
1. 変更後のモデルを同じフォルダに serialize する。
1. ローカルの変更をライブモデルへ copy して save する。

`TMDL_DIFF_DIAGNOSTICS=true` の場合は、`dataSources.tmdl` の差分だけをログに出します。

## 資格情報の注意

初回の資格情報設定は、Power BI Service の UI で入力して保存する操作が必要になる場合があります。
このアプリは TOM / TMSL / TMDL でモデルの Structured DataSource を更新しますが、サービス側の資格情報ストアに初回値を確実に書き込めるとは限りません。
そのため、更新時には警告ログを必ず出し、必要であれば Power BI Service のセマンティックモデル設定画面で資格情報を保存してください。

## ログ設定

ログは Serilog を使っています。

### 出力先

| Key | 値 | 内容 |
| --- | --- | --- |
| `LOG_TARGET` | `Console` | 標準出力に出す |
| `LOG_TARGET` | `File` | ファイルに出す |
| `LOG_DIRECTORY` | 任意のパス | `LOG_TARGET=File` のときの出力先フォルダ |

### ログレベル

`LOG_LEVEL` で出力レベルを変えます。

| 値 | 内容 |
| --- | --- |
| `Verbose` | もっとも詳細 |
| `Debug` | デバッグ向け |
| `Information` | 通常運用向け |
| `Warning` | 警告以上 |
| `Error` | エラー以上 |
| `Fatal` | 致命的エラーのみ |

たとえばファイルに詳細ログを残す場合は次のようにします。

```dotenv
LOG_TARGET=File
LOG_DIRECTORY=logs
LOG_LEVEL=Debug
```

## 主要な追加設定

| Key | 内容 |
| --- | --- |
| `SKIP_STRUCTURED_CREDENTIAL_WRITE` | Structured credential の保存をスキップする |
| `CLEAR_STRUCTURED_CREDENTIAL` | Structured credential を空にする |
| `TMDL_FOLDER_PATH` | TMDL の出力先ルート |
| `TMDL_DIFF_DIAGNOSTICS` | `dataSources.tmdl` の差分ログを出すかどうか |

## 実行手順

1. [fabric-tom/.env.example](fabric-tom/.env.example) を参考に [fabric-tom/.env](fabric-tom/.env) を設定する。
1. `fabric-tom` フォルダでビルドする。
1. 必要なモードを指定して実行する。

実行例。

```bash
cd fabric-tom
dotnet build
MODEL_UPDATE_MODE=TMDL dotnet run
```

## よくある確認ポイント

- `SQL_AUTH_MODE=EntraInteractiveMfa` は永続的な資格情報の書き込みには向かないため、保存用の認証では `SqlPassword` または `EntraServicePrincipal` を使ってください。
- `MODEL_UPDATE_MODE=TMDL` で変更が入ると、`tmdl` フォルダに `dataSources.tmdl` が生成されます。
- 旧キーを使っている既存の `.env` はそのまま動きますが、新しく書く場合は非 SQL 設定を `MODEL_UPDATE_MODE` / `LOG_*` / `TMDL_*` に寄せると分かりやすいです。

## ソースの見どころ

主要な実装は次のファイルです。

- [fabric-tom/Program.cs](fabric-tom/Program.cs) は設定の読み込みと起動処理を担当する。
- [fabric-tom/SemanticModelSqlDataSourceUpdater.cs](fabric-tom/SemanticModelSqlDataSourceUpdater.cs) はモデル更新の本体である。
- [fabric-tom/TomStructuredCredentialWriter.cs](fabric-tom/TomStructuredCredentialWriter.cs) は TOM 更新を行う。
- [fabric-tom/TmslStructuredCredentialWriter.cs](fabric-tom/TmslStructuredCredentialWriter.cs) は TMSL 更新を行う。
- [fabric-tom/TmdlStructuredCredentialWriter.cs](fabric-tom/TmdlStructuredCredentialWriter.cs) は TMDL 更新を行う。
