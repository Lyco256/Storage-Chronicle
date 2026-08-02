# EventStackView.axaml

## 役割

Compiled Bindingを有効にしたAvalonia実UI。モード、順序、Live、filter、保存filter、virtualized rows、展開children、詳細品質、process navigation、ページ操作を表示する。

## アクセシビリティ・入力

行・filter・ページ・詳細・processリンクにAutomationProperties名とtooltipを付ける。View code-behindがAvalonia KeyをUI中立`EventStackKey`へ変換し、ViewModelが操作を処理する。

## 不変条件・関連テスト

行リストはVirtualizingStackPanelを使用し、表示ページ外の行を生成しない。Headless Avaloniaテストが実Viewをmeasure/arrangeし、RowsListとcompiled bindingのDataContextを確認する。
