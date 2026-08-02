# EventStackView.axaml.cs

## 役割

AvaloniaのKeyEventをUI中立な`EventStackKey`へ変換する薄いcode-behind。I/OやProjection呼び出しは持たず、DataContextのViewModelへdelegateする。

## 失敗動作・テスト

未対応キーは無視し、対応キーだけHandledにする。Headless実UIテストでView生成とキーボード経路を検証する。
