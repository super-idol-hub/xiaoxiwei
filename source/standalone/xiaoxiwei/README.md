# 小曦薇

开发者：Anbunensi

The standalone build recovers 4× frames directly from the approved high-resolution source strips. It embeds 176 authored PNG keyframes plus compact bidirectional motion meshes in a ZIP resource and decodes/scales them through a bounded LRU cache. Runtime interpolation warps exactly one opaque pose at a time, presenting every complete animated cycle with at least 24 display stages while preserving the v3.0.1 cadence, without whole-frame cross-fades or duplicate silhouettes.

Runtime features:

- borderless per-pixel transparent, always-on-top window
- source-recovered 528×808 RGBA frames with a fixed crop and baseline
- authored action timings, runtime easing, and direct 16-direction mouse gaze
- drag-threshold reaction: startled lift while held, then frown → foot stomp → pout on release
- ordinary clicks, double-click waves, sleep wake-up, and phone-break exit remain independent
- persistent sit-and-play-phone state with click-to-put-away/stand
- persistent side-sleep state with head-anchored rising `z/Z` and click-to-wake
- atomic 60-stage skin transition: spin → top-to-bottom scan reveal → final pose → idle
- external 176-frame skin discovery with manifest/path/size validation and built-in fallback
- left-drag positioning, double-click wave, and mouse-wheel scaling
- right-click actions, pause, size, reset, tray icon, and single-instance guard
- embedded frame archive, built-in self-test, and timed layered-window QA mode

Build with `build.ps1`. The local compiler targets .NET Framework 4.x and produces `outputs/xiaoxiwei-standalone-4k-v3/小曦薇.exe`. Motion-mesh generation is a build-time step only and uses `opencv-python-headless`; install it into the workspace build-tool directory with `python -m pip install --target .build-tools/opencv opencv-python-headless` when reproducing the build on a new machine. The packaged EXE does not depend on Python or OpenCV.

Build an external skin with `build_skin.ps1`. The generated folder contains a UTF-8 `skin.xml`, root-level `rNN/cNN.png` entries plus `motion/*.mtn` meshes in `frames.zip`, and visual/JSON QA artifacts. Runtime and packer both enforce 32 MiB per entry, 128 MiB compressed archive, and 128 MiB total uncompressed limits.

## 免责声明

本程序由 Anbunensi 独立、非商业开发，仅供田曦薇粉丝个人欣赏、交流与非商业使用，纯属为爱发电。人物姓名、肖像、形象及相关素材的权利归田曦薇本人及相应权利方所有。本程序为非官方作品，与田曦薇本人、工作室、经纪机构及品牌方无官方关联，也不代表已获得其授权。禁止售卖、收费分发、广告引流、商业推广、二次商用、冒用官方名义，或用于侵犯肖像权、名誉权及其他合法权益。若权利方认为内容不妥，请停止传播并联系开发者处理。
