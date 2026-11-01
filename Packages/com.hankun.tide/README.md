# Tide

A deterministic timeline for Unity. Every frame depends on the time alone:
seeking, scrubbing and playback show the same thing, and a capture repeats
exactly, take after take. Like a tide table, anything that builds up over
time is worked out ahead, so any moment can be looked up directly.

## Features

- **Clip tracks** with ease in/out, crossfades between overlapping clips and
  pre/post extrapolation (hold, loop).
- **Built-in tracks:** channel (any float a component exposes), activation,
  transform, animation (Animator clips, crossfaded, in edit mode too), audio,
  markers and nested sub-timelines.
- **Phase integration:** components whose effects accumulate at an adjustable
  rate (a speed you can animate) implement `ITidePhaseTarget`; the player
  integrates their rates over the whole timeline into a table when it binds,
  so the phase at any time is a lookup instead of a running sum.
- **Scene clock:** `TideClock.Time` is the timeline's time while a player that
  drives the clock is bound, and game or editor time otherwise. Read it
  instead of `Time.time` and your effects follow the timeline too.
- **Edit-mode preview** without play mode: binding remembers the targets'
  values and unbinding puts them back, so previewing never dirties the scene.
- **Audio clock:** the player can follow an `AudioSource`, for music-driven pieces.
- **No dependencies**: the Unity Timeline package is not needed.

## Install

Add the package to `Packages/` as an embedded package, or reference its git
repository from `Packages/manifest.json` (or as a git submodule).

## Quick start

1. *Create > Tide > Timeline* makes a timeline asset.
2. Add a **Tide Player** to a GameObject (*Add Component > Tide > Tide Player*)
   and assign the asset.
3. *Window > Tide* opens the editor. Drag scene objects onto it to add tracks
   bound to them, add clips, and scrub to preview in edit mode.

The asset never references the scene: each player keeps its own binding per
track, so one timeline can be played by several players.

## Extending

- **Channels:** implement `ITideChannelTarget` on a component to expose named
  float channels to channel tracks.
- **Tracks and clips:** subclass `TideClipTrack` (or `TideTrack`) and `TideClip`.
  The editor finds new types on its own. `[TideDisplay("Name", r, g, b)]`
  gives a track its name and colour in the editor.
- **Clip tools:** subclass `TideClipEditor` in an editor assembly to add
  buttons for a clip type ("fit to content", "capture from the scene") and to
  fill new clips from what the track is bound to.
- **Phases:** implement `ITidePhaseTarget` to have the player own a
  component's accumulated phases while it is bound.

## License

MIT — see [LICENSE.md](LICENSE.md).
