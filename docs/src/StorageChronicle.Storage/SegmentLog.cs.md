# SegmentLog.cs

## 役割

不変の分割追記ログを管理する。ヘッダーはmagic、format version、segment ID、作成時刻を持ち、各レコードは長さ、型、schema version、内部Sequence、payload、CRC32Cを持つ。

## 書込みと回復

現在の`.open`セグメントだけを追記対象にし、容量上限でローテーションする。閉じるとraw bytesをZstandard圧縮し、展開してbyte単位で検証してから`.zst`とmanifestを確定する。manifestのSHA-256参照はsegment ID、Sequence範囲、件数、圧縮状態、履歴ブランチというメタデータから計算し、ファイル内容ハッシュではない。

末尾の未完了フレームは`.open`に限り安全に切り捨てる。CRC、header、圧縮展開の不整合はそのセグメントを`SegmentSkipped`へ通知してスキップし、健全セグメントを列挙する。確定済みセグメントは更新しない。

## 依存関係・失敗動作

ZstdSharp.Port、System.Text.Json、SHA-256を使用する。flush・圧縮・manifestの確定に失敗した場合は上位へ例外を返し、黙って書込みを継続しない。

## 関連テスト

正常往復、圧縮確定、キャンセル、同時reader、Zstandard破損スキップ、SQLiteなしのログ読み出しを`StorageEngineTests`が検証する。

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
