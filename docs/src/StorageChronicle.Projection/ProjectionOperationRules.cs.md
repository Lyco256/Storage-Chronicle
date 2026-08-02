# ProjectionOperationRules.cs

Canonical operationをActivity/Diffの主操作へ決定的に分類する。Delete、Recycle、Restore、Move、Copy、Create、Rename、DataWrite系、Share、Cloud、Metadataの順序を固定し、意味的なDiff状態へ変換する。

read-only ETW観測はDurable Eventの履歴を変更せずActivity境界で分断しない。Unknown/Gapは独立表示の入力として扱い、Copyは記録済みプロパティの厳密な意図がある場合だけ選ぶ。UI色やAvalonia型は持たない。

主なテストはDiff優先順位、移動元/移動先の関連ID、UnverifiedGap独立表示。
