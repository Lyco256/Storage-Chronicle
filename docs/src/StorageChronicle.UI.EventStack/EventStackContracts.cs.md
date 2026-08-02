# EventStackContracts.cs

## 役割

UI専用の検索クエリ、ページ、展開行、詳細、保存済みfilter、Projection境界を定義する。共有契約を変更せず、`IVirtualizedPageSource<EventStackRow>`向けadapterも提供する。

## 不変条件

`EventStackQuery`はMode/Page/PageSize/Sort/Filter/ExpandedGroupsを一つの状態として渡す。`EventStackPage`はページ内のグループと子を保持し、projectionが境界を分断しない。payloadやOS file accessを契約に含めない。

## 依存関係・失敗動作

DomainのEventStackRow、EventQuality、EventOriginなどとUI.Sharedのページ契約だけに依存する。projectionの失敗はasync例外として呼び出し側へ伝達する。

## 関連テスト

Fake Projectionを使うViewModelテストで全query状態とbounded pageを検証する。
