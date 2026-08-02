# Crc32C.cs

## 役割

セグメントレコードのフレームヘッダーとpayloadに対するCRC32Cを計算する内部実装である。

## 不変条件

CRCはレコードフレームに格納され、読み出し時に必ず再計算して比較する。SHA-256やファイル内容ハッシュの代替には使わない。

## 失敗動作とテスト

不一致はセグメントを破損として扱い、他セグメントの読み出しを妨げない。中間セグメント破損の統合テストは`CorruptClosedSegmentIsSkippedWithoutBlockingHealthySegments`で検証する。
