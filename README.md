# CSIndexer

Windows上のC#コードをRoslynでセマンティック解析し、検索用の事実をSQLiteへ保存する`.NET 10` CLIです。

```powershell
dotnet build CsIndex.sln --configuration Release

src\CsIndex.Cli\bin\Release\net10.0-windows\win-x64\csindex.exe index MySolution.sln
src\CsIndex.Cli\bin\Release\net10.0-windows\win-x64\csindex.exe callers "Player::Play()"
```

実装範囲、CLI、DB、既知の制限は次を参照してください。

- [仕様](docs/SPEC.md)
- [CLI](docs/CLI.md)
- [DB schema](docs/DB_SCHEMA.md)
- [実装状況](docs/IMPLEMENTATION_STATUS.md)
- [既知の制限](docs/KNOWN_LIMITATIONS.md)
- [作業一覧](TASKS.md)
