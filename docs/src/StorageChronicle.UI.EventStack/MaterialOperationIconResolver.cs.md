# MaterialOperationIconResolver.cs

## 役割

Domainの`OperationIconMeaning`をMaterial Iconsの安定した意味IDへ解決する。Create/Edit/Move/Rename/Delete/ReconcileなどをUIで一貫表示する。

## 不変条件・依存関係

操作名を直接UI文字列へ散在させず、`IOperationIconResolver`として注入可能にする。DomainとUI.Sharedだけに依存し、Storage/Windows APIは参照しない。

## 関連テスト

各行の`IconKey`生成とHeadless実UIの行バインディングで使用される。
