# FrameEyeOSC (VRCFaceTracking module)

A [VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking) (VRCFT) module for the Steam Frame's eye tracking.
It pairs with **frameeyeosc** on the Steam Frame: frameeyeosc sends its eye data to this module over UDP, and VRCFT
then drives the avatar's eyes, eyelids (including wide eyes), squint and pupils in each avatar's own parameter format.
There is no per-avatar pupil format to choose: whatever eye parameters the avatar has, VRCFT fills them.

[日本語は下にあります](#日本語)

## Install

https://github.com/user-attachments/assets/a620942c-6e0a-4b28-9855-7b5b1c3e903a

1. Download `FrameEyeOSC_<version>.zip` from [Releases](https://github.com/sasaken1102r/VRCFT-FrameEyeOSC/releases).
2. In VRCFT, open **Module Registry** and press **Install from .zip**, then pick the zip. VRCFT puts the module in its
   own folder under `CustomLibs` and loads it as **FrameEyeOSC**.
3. Turn off other eye modules (such as LiveLink), and on the Steam Frame set frameeyeosc's output to
   **VRCFT (FrameEyeOSC)**.

Without the installer, unzip and copy `FrameEyeOSC.dll` into VRCFT's custom module folder, then restart VRCFT:
- Windows: `%APPDATA%\VRCFaceTracking\CustomLibs`
- Linux (VRCFT Avalonia): `~/.config/VRCFaceTracking/CustomLibs`

The module is built for .NET 8 against the VRCFT Avalonia SDK (Core 5.2.3.0). Checked so far:
- VRCFT from Steam (Windows, .NET 10): installed from the zip with "Install from .zip"; the avatar's eyes
  move.
- VRCFT Avalonia 1.1.1.0 (.NET 8), the Linux AppImage on Zorin OS 17: loads and receives packets from
  `tools/FeoSend` (an earlier build of this module, without VRChat).

Installing from VRCFT's module registry is planned for later.

### Firewall

The module receives UDP on port **11112**. It runs inside `VRCFaceTracking.ModuleProcess.exe`, not
`VRCFaceTracking.exe`, so on Windows that is the program asked about (allowing only VRCFaceTracking isn't enough). Allow it
on the network the Frame sends over; Steam Link's wireless adapter usually shows as an "Unidentified network", which
Windows treats as public. If no packets arrive, add an inbound rule for UDP 11112 that covers both (PowerShell as
administrator):

```powershell
New-NetFirewallRule -DisplayName "VRCFT FrameEyeOSC (UDP 11112)" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 11112 -RemoteAddress LocalSubnet -Profile Private,Public
```

On Linux, allow it in your firewall, e.g. `sudo ufw allow from 192.168.0.0/24 to any port 11112 proto udp` (adjust the
subnet; untested here).

### frameeyeosc side

In frameeyeosc's panel on the Steam Frame, choose **VRCFT (FrameEyeOSC)** as where to send. Under Output the target
PC (automatic usually finds it; otherwise type the PC's IP address, e.g. `192.168.1.20`) and the port (11112) must
match this PC. The module's log lists this PC's IPv4 addresses when it starts.

Turn off Steam Link's own OSC output on the PC (SteamVR settings > Steam Link > OSC), or it fights VRCFT over the
avatar's eyes.

VRCFT gives the eyes to the first module that takes them and never hands them to another module later. FrameEyeOSC
takes them at once, so frameeyeosc can start before or after VRCFT. If you also use another headset's eye module
(e.g. PICO 4 Pro), set `"claimWithoutPackets": false` (see below) so that module can drive the eyes while the Steam
Frame is off.

## Config (optional)

To use another port, or to leave the eyes to another headset's module while the Steam Frame is off, create
`FrameEyeOSCConfig.json` next to `FrameEyeOSC.dll`:

```json
{ "port": 11112, "claimWithoutPackets": true }
```

Without the file the module uses 11112 and takes the eyes at once; it never writes the file itself. With
`"claimWithoutPackets": false` it takes the eyes only when a packet from frameeyeosc arrives within 5 s of VRCFT
starting; without one it logs "No packets from frameeyeosc within 5 s" and leaves the eyes to other modules. Then start
frameeyeosc before VRCFT (or restart VRCFT after starting it). A file it can't read is reported in the log and the
defaults are used. Change frameeyeosc's port setting to the same number.

When installed with "Install from .zip", the DLL is in the module's own folder:
- Windows: `%APPDATA%\VRCFaceTracking\CustomLibs\103a993c-383f-4a7a-8cc6-5e8b4bf0d133\`
- Linux (VRCFT Avalonia): `~/.config/VRCFaceTracking/CustomLibs/103a993c-383f-4a7a-8cc6-5e8b4bf0d133/`

(Copied by hand, it's the folder you copied the DLL to.) Installing a new version from a zip deletes that folder first,
so the config file is gone after an update: create it again, and keep frameeyeosc's port setting the same.

## How the values map

Each eye sample from frameeyeosc is one 76-byte UDP packet. The module takes the newest one each update and sets:

- **Gaze**: as sent (-1..1, 1 = 45°).
- **Eyelid**: frameeyeosc's eyelid 0..1 (0.75 = relaxed open, 1 = fully wide) becomes VRCFT's `Openness` (0..0.75 →
  0..1) plus the `EyeWide` shape (0.75..1 → 0..1), so VRCFT's `v2/EyeLid` comes out as the original value.
- **Squint**: the `EyeSquint` shape, while frameeyeosc measures the eyelid with the eye cameras.
- **Pupils**: diameters in mm, plus a dilation range chosen so VRCFT's `v2/PupilDilation` equals frameeyeosc's
  dilation for this wearer. If only one eye has a pupil value, both eyes use it.
- Without tracking (headset off) or with no packets for 1 s: relaxed open eyes looking ahead; pupils stay.

The full packet format and mapping are in [docs/protocol.md](docs/protocol.md).

## Troubleshooting

- **"UDP port 11112 is already in use"**: another VRCFT instance, another FrameEyeOSC copy or another app listens on
  it. Close it, or change the port in `FrameEyeOSCConfig.json` and in frameeyeosc.
- **"Windows refused UDP port … (access denied)"**: Windows may have reserved the port (Hyper-V/WSL). Check
  `netsh interface ipv4 show excludedportrange protocol=udp` and pick a port outside those ranges.
- **The log never says "Receiving from frameeyeosc"**: check the firewall, that frameeyeosc sends to this PC's IP
  address (e.g. `192.168.1.20`, not a VPN address), that the headset and PC are on the same network, and that the
  ports match.
- **The eyes move oddly or not at all**: turn off other eye tracking modules in VRCFT (LiveLink, ETVR and so on) and
  frameeyeosc's other outputs, so only one source drives the eyes.
- **"VRCFT has no eye tracking slot for FrameEyeOSC"**: another eye module took the eyes first. Turn off or remove that
  module, then restart VRCFT.
- **"No packets from frameeyeosc within 5 s"** (with `"claimWithoutPackets": false`): frameeyeosc wasn't sending when
  VRCFT started, so the eyes were left to other modules (or none). Start frameeyeosc, then restart VRCFT.
- **"Ignoring a datagram …"**: something else sends to the port, or frameeyeosc and the module don't speak the same
  packet version; update both.

## Build and test

Needs the .NET 10 SDK (the module targets net8.0; the tests run on both net8.0 and net10.0, so the .NET 8 runtime too).
The reference DLLs are in [refs/](refs/).

```sh
dotnet build -c Release
dotnet test
```

The module is `FrameEyeOSC/bin/Release/net8.0/FrameEyeOSC.dll`. `VRCFaceTracking.Core.dll` and
`VRCFaceTracking.SDK.dll` in `refs/` are only compiled against; VRCFT provides them at runtime.
`pwsh tools/package.ps1` builds and writes `dist/FrameEyeOSC_<version>.zip` (the DLL plus `module.json`).

To try the module without a Steam Frame, `tools/FeoSend` sends synthetic packets (a slow look left and right, blinks,
wide eyes, a squint, changing pupils) to a running VRCFT on this PC:

```sh
dotnet run --project tools/FeoSend -- 127.0.0.1 11112
```

Add `--brows` to also send brows (raised, then lowered, over 8 s), or `--brows=0.6` for a constant raise.

## Credits

- [VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking)
- Made by sasaken@

## License

MIT (see `LICENSE`). The VRCFaceTracking DLLs used to build it are Apache 2.0 (see `THIRD_PARTY_NOTICES.md`).

Unofficial; not affiliated with or endorsed by Valve, VRChat Inc. or the VRCFaceTracking project. Steam, Steam Frame and
SteamVR are trademarks of Valve Corporation; VRChat is a trademark of VRChat Inc.

---

## 日本語

Steam Frame のアイトラッキング用の VRCFaceTracking（VRCFT）モジュールです。Steam Frame で動く **frameeyeosc**
と組み合わせて使います。frameeyeosc が目のデータを UDP でこのモジュールに送り、VRCFT がアバターの視線・まぶた
（見開きを含む）・目の細め・瞳孔を、それぞれのアバターが持つパラメーターの形式で動かします。アバターごとに瞳孔の
形式を選ぶ必要はありません。

### 入れ方

https://github.com/user-attachments/assets/7cc97be0-d6b9-4514-928b-64180b87ef02

1. [Releases](https://github.com/sasaken1102r/VRCFT-FrameEyeOSC/releases) から `FrameEyeOSC_<版>.zip` を落とします。
2. VRCFT の **Module Registry** を開いて **Install from .zip** を押し、その zip を選びます。VRCFT が `CustomLibs`
   の下にモジュール用のフォルダーを作って入れ、**FrameEyeOSC** として読み込みます。
3. ほかの目のモジュール（LiveLink など）はオフにして、Steam Frame の frameeyeosc の送り先を
   **VRCFT（FrameEyeOSC）** にします。

インストーラーを使わないときは、zip を展開して `FrameEyeOSC.dll` を VRCFT のカスタムモジュールのフォルダーに
コピーし、VRCFT を再起動します。
- Windows: `%APPDATA%\VRCFaceTracking\CustomLibs`
- Linux（VRCFT Avalonia）: `~/.config/VRCFaceTracking/CustomLibs`

モジュールは .NET 8 で、VRCFT Avalonia の SDK（Core 5.2.3.0）に合わせて作ってあります。確かめたこと:
- Steam 版の VRCFT（Windows、.NET 10）: 「Install from .zip」で zip から入れて、アバターの目が動くこと。
- VRCFT Avalonia 1.1.1.0（.NET 8）の Linux 版 AppImage（Zorin OS 17）: 読み込まれて、`tools/FeoSend` のパケットを
  受け取ること（このモジュールの少し前のビルドで、VRChat なし）。

VRCFT のモジュール一覧からのインストールは今後対応予定です。

### ファイアウォール

UDP の **11112** 番で受け取ります。モジュールは `VRCFaceTracking.exe` ではなく `VRCFaceTracking.ModuleProcess.exe`
の中で動くので、Windows で許可を聞かれるのもこちらです（VRCFaceTracking を許可するだけでは届きません）。Frame
から届くネットワークで許可してください。Steam Link のワイヤレスアダプターは、たいてい「識別されていないネットワーク」
（パブリック扱い）になります。届かないときは、両方に効く UDP 11112 の受信の規則を足します（管理者の PowerShell で）:

```powershell
New-NetFirewallRule -DisplayName "VRCFT FrameEyeOSC (UDP 11112)" -Direction Inbound -Action Allow -Protocol UDP -LocalPort 11112 -RemoteAddress LocalSubnet -Profile Private,Public
```

Linux ではお使いのファイアウォールで許可します。例: `sudo ufw allow from 192.168.0.0/24 to any port 11112 proto udp`
（サブネットは環境に合わせて。こちらでは未確認）。

### frameeyeosc 側の設定

Steam Frame の frameeyeosc のパネルで、送り先に **VRCFT（FrameEyeOSC）** を選びます。出力の送り先 PC（ふつうは
自動で見つかります。見つからなければ PC の IP アドレス、例 `192.168.1.20` を入力）とポート（11112）をこの PC に
合わせます。この PC の IPv4 アドレスはモジュールの起動時にログに出ます。

PC 側の Steam Link 自身の OSC 出力（SteamVR の設定 > Steam Link > OSC）はオフにしてください。オンのままだと
VRCFT と取り合いになります。

VRCFT は目を最初に受け持ったモジュールに任せ、あとから他のモジュールに渡しません。FrameEyeOSC はすぐに目を
受け持つので、frameeyeosc は VRCFT の前に起動しても後に起動しても動きます。別のヘッドセットの目のモジュール
（PICO 4 Pro など）も使うときは、Steam Frame を使わない間はそのモジュールが目を動かせるように
`"claimWithoutPackets": false` にしてください（下の設定ファイル）。

### 設定ファイル（任意）

ポートを変えるとき、または Steam Frame を使わない間は目を別のヘッドセットのモジュールに任せたいときは、
`FrameEyeOSC.dll` と同じフォルダーに `FrameEyeOSCConfig.json` を作ります。

```json
{ "port": 11112, "claimWithoutPackets": true }
```

ファイルが無ければ 11112 を使い、すぐに目を受け持ちます（モジュールがファイルを書くことはありません）。
`"claimWithoutPackets": false` にすると、VRCFT の起動から 5 秒以内に frameeyeosc のパケットが届いたときだけ目を受け持ちます。
届かなければ「No packets from frameeyeosc within 5 s」とログに出して目を他のモジュールに任せます。このときは
frameeyeosc を VRCFT より先に起動してください（後から起動したときは VRCFT を再起動します）。読めないファイルは
ログに警告を出して既定値を使います。frameeyeosc のポート設定も同じ番号にしてください。

「Install from .zip」で入れたときは、DLL はモジュール用のフォルダーにあります:
- Windows: `%APPDATA%\VRCFaceTracking\CustomLibs\103a993c-383f-4a7a-8cc6-5e8b4bf0d133\`
- Linux（VRCFT Avalonia）: `~/.config/VRCFaceTracking/CustomLibs/103a993c-383f-4a7a-8cc6-5e8b4bf0d133/`

（手でコピーしたときは、DLL をコピーしたフォルダーです。）新しい版を zip から入れると、そのフォルダーはいったん
消されるので、設定ファイルも消えます。もう一度作り、frameeyeosc のポート設定と合わせてください。

### 値の対応

- **視線**: 送られた値のまま（-1..1、1 = 45°）。
- **まぶた**: frameeyeosc のまぶた 0..1（0.75 = 自然に開いた状態、1 = 最大の見開き）を VRCFT の `Openness` と
  `EyeWide` に分けます。VRCFT の `v2/EyeLid` は元の値に戻ります。
- **目の細め**: アイカメラでまぶたを測っている間、`EyeSquint` に入れます。
- **瞳孔**: 直径（mm）と、VRCFT の `v2/PupilDilation` が frameeyeosc の散瞳度合いと同じになる範囲を設定します。
  片目だけ値があるときは両目にその値を使います。
- トラッキングが無いとき（ヘッドセットを外したなど）や 1 秒届かないとき: 正面を見た自然に開いた目。瞳孔はそのまま。

詳しくは [docs/protocol.md](docs/protocol.md) を見てください。

### うまく動かないとき

- **「UDP port 11112 is already in use」**: 別の VRCFT、別の FrameEyeOSC、または別のアプリがそのポートを使って
  います。閉じるか、`FrameEyeOSCConfig.json` と frameeyeosc のポートを変えてください。
- **「access denied」**: Windows がそのポートを予約していることがあります（Hyper-V/WSL）。
  `netsh interface ipv4 show excludedportrange protocol=udp` で確認し、範囲外のポートにしてください。
- **ログに「Receiving from frameeyeosc」が出ない**: ファイアウォール、frameeyeosc の送り先がこの PC の IP アドレス
  （例 `192.168.1.20`。VPN のアドレスではなく）になっているか、ヘッドセットと PC が同じネットワークか、ポートが
  合っているかを確認してください。
- **目の動きがおかしい・動かない**: VRCFT の他の目のモジュール（LiveLink、ETVR など）や frameeyeosc の他の出力を
  オフにして、目を動かすのが一つだけになるようにしてください。
- **「VRCFT has no eye tracking slot for FrameEyeOSC」**: 他の目のモジュールが先に目を受け持っています。そのモジュールを
  オフにするか削除して、VRCFT を再起動してください。
- **「No packets from frameeyeosc within 5 s」**（`"claimWithoutPackets": false` のとき）: VRCFT の起動時に frameeyeosc
  が送っていなかったので、目は他のモジュールに任せました（無ければ目は動きません）。frameeyeosc を起動してから VRCFT
  を再起動してください。
- **「Ignoring a datagram …」**: 他の何かがそのポートに送っているか、frameeyeosc とモジュールのパケットの版が
  合っていません。両方を新しくしてください。

### ビルドとテスト

.NET 10 SDK が要ります（モジュールは net8.0 向け。テストは net8.0 と net10.0 の両方で動くので、.NET 8 のランタイムも
要ります）。参照する DLL は [refs/](refs/) にあります。

```sh
dotnet build -c Release
dotnet test
```

`pwsh tools/package.ps1` でビルドして `dist/FrameEyeOSC_<版>.zip`（DLL と `module.json`）を作ります。Steam Frame
なしで試す `tools/FeoSend` などの詳細は、英語の [Build and test](#build-and-test) を見てください。

### クレジット

- [VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking)
- 作: ささけん＠

### ライセンス

MIT（`LICENSE`）。ビルドに使う VRCFaceTracking の DLL は Apache 2.0 です（`THIRD_PARTY_NOTICES.md`）。

非公式のモジュールです。Valve、VRChat Inc.、VRCFaceTracking プロジェクトとは関係がなく、承認も受けていません。Steam、Steam Frame、
SteamVR は Valve Corporation の、VRChat は VRChat Inc. の商標です。
