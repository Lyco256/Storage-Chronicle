# EventStackView.axaml.cs

## 役割

AvaloniaのKeyEventをUI中立な`EventStackKey`へ変換し、ViewのLoaded時に一度だけ非同期初期化を依頼する薄いcode-behind。I/OやProjection呼び出しは持たず、DataContextのViewModelへdelegateする。

## 失敗動作・テスト

未対応キーは無視し、対応キーだけHandledにする。初期IPC失敗はStatusMessageへ報告する。Headless実UIテストでView生成とキーボード経路を検証する。
