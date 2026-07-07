# Media Assets

Media referenced by the project READMEs (`README.md` / `README.zh-CN.md`). The
same files serve both languages.

| Asset | Where | Notes |
|-------|-------|-------|
| Hero video | Top of the README | Links to YouTube (`youtu.be/X9RL0WqCAvM`) through its thumbnail — GitHub can't embed a player inline, so the thumbnail acts as a click-to-play poster. |
| `demo.gif` | Features → Animation & Effects | In-repo demo clip. |

## Tips

- `demo.gif` is ~20 MB. If GitHub load time becomes an issue, re-encode it
  smaller (cap width ~1280px / ~10s with `gifski` or `ffmpeg`), or host large
  clips as an `.mp4` in a GitHub Release and link by URL to keep the repo lean.
- The hero thumbnail uses `maxresdefault.jpg`; if it ever 404s (non-HD upload),
  switch to `hqdefault.jpg`.
