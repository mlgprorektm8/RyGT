# Renderer changes (shadGT, branch `ryty-gpu`)

The renderer (`libSceVideoOut.prx`) is built from
[shadGT, branch `ryty-gpu`](https://github.com/mlgprorektm8/shadGT/tree/ryty-gpu). Changes made
after shadGT commit `29af7ca6` are kept here as patches, in order (the branch itself includes all of them from
commit `4371a500`):

| Patch | What it adds |
|---|---|
| `0001-ryGT-frame-generation-...` | AMD FSR 3.1.4 frame generation, shader precompile on the splash screen (with a progress bar), pausing the game while new shaders compile, compile batching, efficiency-core shader compiles, opt-in diagnostics |
| `0002-ryGT-splash-screen-shows-...` | Hotfix: the splash screen and progress bar show while the game waits for the precompile (it was a black screen) |
| `0003-ryGT-Lewis-Hamilton-Time-Trial-...` | GTS-002: unlocks the Lewis Hamilton Time Trial Challenge in the Campaign menu (the add-on's unlock flags came from the shut-down servers); `RYGT_LEWIS=0` turns it off |
| `0004-ryGT-Livery-Editor-offline-GTS-003` | GTS-003: enables the Livery button on the main menu while offline (the menu script greyed it out without the servers; the editor has its own offline mode that saves liveries locally); `RYGT_LIVERY=0` turns it off |
| `0005-ryGT-effects-fix-frame-generation-...` | Dust, smoke and rain no longer show pink and black rectangles (the game's clears of reused effect memory ran as skipped "mask clears"); frame generation starts after the splash screen (it could freeze the precompile); slow-motion shader compiles; freeze and frame-capture diagnostics |
| `0006-ryGT-on-screen-keyboard-offline-livery-...` | On-screen keyboard for text fields, whole-script replacements from `patches\scripts` (GTS-005), custom decals from the `decals` folder (GTS-006) |
| `0007-ryGT-renderer-speed-work-2026-10-15` | Recorder speed-ups (fence writes through cached aliases, texture lookup memo), the decoder's shader search tries the last match first, early-fence deadlock fixes, and the opt-in multithreaded groundwork (per-thread draw state and command rings, buffer stage workers, shared buffer-cache lock) with ceiling and timeline diagnostics |
| `0008-ryGT-direct-memory-renderer-2026-10-16` | The new renderer (launcher option "New renderer", `GPU.direct_renderer`, or `SHADGT_DIRECT=1`): shaders read game memory in place through a GPU page table over imported guest memory instead of copying buffers every draw (Tokyo rain benchmark about 32 -> 43 fps); freed render targets are reused instead of rebuilt (all modes); wet-road reflection (GTS-007) and half-rate mirror (GTS-008) toggles; texture-binding shortcuts; import prototype, ceiling and timing diagnostics |
| `0009-ryGT-buffer-stage-with-direct-renderer` | With the new renderer, a helper thread fetches the remaining (copied) buffers ahead of the draw recorder (about +1.5 fps in the Tokyo rain benchmark) |
| `0010-ryGT-framegen-compile-freeze-fix` | Frame generation pauses while new shaders compile (with it on, heavy compiling could freeze the game in the driver); timing switches for the command thread and pipeline selection |
| `0011-ryGT-quality-preset-memory` | The reused-image pool size follows the launcher's quality preset (`GPU.image_recycle_mb`) |
| `0012-ryGT-old-shader-fallback-direct-default` | The new renderer is the only renderer (`SHADGT_DIRECT=0` for comparisons); while its own shaders and pipelines are built in the background, draws use the old renderer's stored shaders, so switching causes no shader-compile freezes |
| `0019-ghost-quad-sample-mask` | Per-quad sample coverage masks restore recorded ghost transparency; ordinary shader caches remain compatible |

To build: check out shadGT at `29af7ca6`, apply the patches with
`git am <RyGT>/renderer/shadGT-patches/*.patch`, then build the `ryty_gpu` target as in the main
README (`cmake --preset x64-Clang-Release -DSHADGT_RYTY_GPU=ON`).

For the portable stdout severity gate and timestamped log rotation, see
[LOGGING.md](LOGGING.md). Patch 0019 is applied after the existing 0018 patch;
the current upstream renderer does not include it.

Patch 0020 fixes the non-renderer host libraries' shared-stub link dependencies
when rebuilding the complete library set with logging enabled.

Patches 0026-0038 come from contributors: 0026-0027 and `0019-ryGT-limit-concurrent-vulkan-pipeline-compiles`
(Tanos; that one applies after 0025), 0029-0038 (xacidr4inx, plain diffs: apply them with
`git apply`) with `vma-patches/0001-trim-empty-default-blocks.patch` for shadGT's `externals/vma`.
Patch 0039 is the shader-compile work (all-core compiles when many wait, faster shader-pack
translation, store dump). Patch 0040 adds the native renderer (experimental): GT Sport's command
buffers are recorded straight into Vulkan by its own renderer instead of shadGT's (about 33-39 fps
against 29 in the Tokyo rain benchmark). The launcher's "Renderer" dropdown picks it
(`GPU.native_renderer`; `RYGT_NATIVE=1` or `=0` overrides); the old renderer stays the default.
Known open problems: in Tokyo at night the cockpit's ambient light flickers between warm and dark
frames, and rare frames are drawn from a wrong camera. Patch 0041 makes shadGT apply the VMA patch
by itself at configure time. Patch 0042 lets the non-renderer host libraries link again (a no-op
Controllers window toggle), and 0043 replaces CreateFileMapping2, which Wine and Proton before 11
lack, with CreateFileMappingW so that every host library loads under Proton 10 (see
[docs/user/LINUX.md](../docs/user/LINUX.md)). Patch 0044 (FIX-160) makes the native renderer and
the background shader workers read guest memory safely, since the game can release it while
the renderer still reads it (two crashes in a Linux tester's runs).
Patch 0045 (DIAG-161) prints GT Sport's own fatal-error message to stderr before the game ends
itself ("[game] fatal error at <file>(<line>): <message>"). Patch 0046 (DIAG-163) logs the first 60
scripts the game loads at start-up ("[boot] script <n> (+<ms> ms): <unit>"; `RYGT_LOG_BOOT=0` turns it
off), to show how far a boot that ends early got.

`0019-ghost-quad-sample-mask` (Gabarsolon) is applied in shadGT after the native renderer, merged
with its larger push block (the quad sample mask follows the four buffer-offset vectors).

shadGT's branch `ryty-gpu` has every patch applied (commit `0522545b`; `84cf50ce` added skipping
screenshots of an empty picture); building from it is the simplest way to get the current renderer.
Patches 0042-0050 are included there too (`ryty-gpu` and `linux-proton` are both at `0522545b`). Patch 0047
puts the shader cache in `<RyGT folder>\shaders` instead of `user\cache`; 0048 makes the launcher's Info
log level include the renderer's Info lines (frame-time windows). 0049 (FIX-165) stops using the driver's
pipeline cache file (`RYGT_DRIVER_CACHE=1` turns it back on, saved only at exit): at 3.7 GB, its
saves every 30 s made native races drop to 10 fps and back. 0050 (DIAG-166) attaches RenderDoc when its
layer is on: F12 or a `capture.now` file in the RyGT folder captures a frame into `captures`.
Patch 0051 (GTS-009) fills screens wider (or narrower) than 16:9: `GPU.aspect_ratio` ("auto", the
launcher's Display > Screen shape > "Fill the screen", is the shape of the game's window or screen;
also "21:9", "32:9", width:height or a number; `RYGT_ASPECT` overrides it) widens the race camera's
16:9 aspect (eboot 0x5e717f) and stretches the picture over the whole window instead of black
bars. The HUD and menus are stretched with the picture; 16:9, the default, changes nothing.
