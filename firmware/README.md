# CodexKeyboard firmware

This directory contains the maintained firmware derived from [`eccherda/ch552g_mini_keyboard`](https://github.com/eccherda/ch552g_mini_keyboard) commit [`060bd13496e8ebd6a94029db8089b1544203c57a`](https://github.com/eccherda/ch552g_mini_keyboard/commit/060bd13496e8ebd6a94029db8089b1544203c57a).

The R1 baseline differs only by the `CodexKeyboard` sketch name, English translations of non-English comments, and removal of trailing whitespace. Upstream images and its duplicate README were not copied. The compiled HEX is byte-for-byte identical to the pinned upstream baseline.

The upstream license is preserved verbatim in [LICENCE](LICENCE). Derived firmware files remain subject to its attribution and share-alike terms.

From the repository root, run:

```powershell
pwsh -File .\Build-Firmware.ps1
```

The script downloads verified Arduino CLI `0.35.2` and installs ch55xduino `0.0.20` into the ignored `.tools/` directory. It performs a clean build and writes `CodexKeyboard.ino.hex` to `.build/firmware/`.
