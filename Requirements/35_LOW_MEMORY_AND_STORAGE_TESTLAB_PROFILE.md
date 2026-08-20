# 16GBホスト向け軽量TestLabプロファイル

## 1. 目的

15.8 GiB RAMと約172.8 GiBの空きNVMeで、テスト品質・対象件数・受入れ条件を落とさずTestLabの同時使用資源だけを抑える。

この文書は「テストを小さくして合格させる」要件ではない。並列度、VM常駐時間、重複データ生成、snapshot数を減らす。

## 2. 固定VM資源

Windows 11:

- 4096 MiB RAM
- 2 vCPU

Windows 10:

- 4096 MiB RAM
- 2 vCPU

4 GiB未満へ自動縮小しない。

同時に1 VMだけ起動する。

MFT 1M、Installer、Explorer correlation、Windows privileged matrixを互いに並行実行しない。

## 3. Codex配置

Codex本体をguest内で常駐実行しない。

Codexはhost通常ユーザーで動作し、guestへ必要なbuild/test artifactだけを `VBoxManage guestcontrol` で送る。

これによりguest側へIDE、Git履歴、NuGet cache、Codex runtimeを複製しない。

guest内で必要なself-contained artifactを優先し、ソースツリー全体のwrite共有を行わない。

## 4. buildとVMの重複負荷

重い `dotnet build` / coverage / BenchmarkDotNet portable suiteは原則hostで完了してからVMを起動する。

VM内ではWindows実APIを必要とする対象だけを実行する。

例外は次。

- Windows 10 runtime compatibility
- Installer clean install
- Service
- Windows-specific self-contained executable検証

VM起動中にhost側で別subagentが大規模buildを並列実行しない。

## 5. storage

OS diskはdynamic VDI。

長期保存するのは各OSのclean baselineだけとする。

runごとの一時snapshotを残さない。

guest内の通常VHDXは4 GiB dynamic。

MFT大規模datasetは内容0-byte中心にし、namespace/MFT entryを増やす。

1M MFT datasetを一度作成した後、再利用可能なseedとしてTestLabRoot内に保持してよい。毎回1Mファイルを再作成しない。

seedは製品ログではなくTestLab artifactとして明確に分離する。

## 6. MFT matrix

10K、100K、1Mの必須件数は維持する。

負荷軽減は次だけで行う。

- 10K/100K/1Mを順次実行
- 一度に一つのdatasetだけattach
- 0-byte file使用
- directoryを分散
- 既存seedからclone
- result回収後にtransient differencing imageを削除

1Mを100Kへ縮小して合格扱いしない。

## 7. Windows 10/11 baseline

両VMのbase OS diskを同時にexpandさせない。

Windows Update等のprovisioningはOSごとに完了させ、clean baseline確定後はnetworkを切る。

baseline確定後の不要なinstaller cache、Windows.old、一時downloadをTestLab内で通常のOS cleanup手段により削除してよい。

acceptance resultと必要なdebug logは削除しない。

## 8. host resource gate

VM起動前にavailable memoryを測る。

- >= 8 GiB: normal
- 6–8 GiB: heavy host processを止めるよう警告してから1 VM起動可
- < 6 GiB: VM起動禁止、handoff

host free disk:

- >= 100 GiB: full provisioning / matrix可能
- 60–100 GiB: 既存baseline利用のみ。新しいOS baselineを増やさない。
- 40–60 GiB: functional smokeのみ。1M seed作成禁止。
- < 40 GiB: TestLab write禁止。

数値不足を理由にacceptance項目をskipして最終完了へ進まない。

## 9. resource acceptanceとの分離

製品の正式な

- Agent + Session Agent < 50 MiB
- normalized CPU average <= 0.5%

はVM値で判定しない。

正式resource acceptanceは既存 `Test-ResourceBudgetAcceptance.ps1` に従い物理Windows環境で行う。

VirtualBox軽量化によってこの製品基準を緩和しない。

## 10. 完了条件

16GBホストでTestLab自体のために同時VMを増やさず、必須ケース数・MFT件数・Windows特権matrixを維持して順次完走できる。
