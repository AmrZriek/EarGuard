# EarGuard cover art

A designed marketing cover for EarGuard: a paper "safety notice" built around the
ear-in-a-hard-hat mark. It is not a UI screenshot and uses no generated imagery.

The joke is the product: a tiny safety inspector standing on a 100% Windows volume
bar, with a gold line at the user's limit.

## Placements and files

| Placement | File | Size |
|---|---|---|
| Gumroad cover | `exports/earguard-cover-16x9-1280x720.jpg` | 1280 × 720 |
| Gumroad thumbnail / Instagram | `exports/earguard-cover-square-1080x1080.jpg` | 1080 × 1080 |
| GitHub social preview | `exports/earguard-cover-github-1280x640.png` | 1280 × 640 |
| Website OG / link preview | `exports/earguard-cover-og-1200x630.jpg` | 1200 × 630 |
| Website hero (square) | `exports/earguard-cover-square-1080x1080.webp` | 1080 × 1080 |
| Wide placements | `exports/earguard-cover-16x9.webp` | 1920 × 1080 |

## Masters

Keep these for future edits and larger placements. They are the 2× renders the
platform files were made from.

| File | Size |
|---|---|
| `exports/earguard-cover-16x9-2560x1440.png` | 2560 × 1440 |
| `exports/earguard-cover-square-2160x2160.png` | 2160 × 2160 |
| `exports/earguard-cover-github-2560x1280.png` | 2560 × 1280 |
| `exports/earguard-cover-og-2400x1260.png` | 2400 × 1260 |

The GitHub PNG is 90 KB (GitHub's limit is 1 MB). The OG JPEG is 47 KB.

## Where it is used

- GitHub README hero: `exports/earguard-cover-16x9-1280x720.jpg`.
- EarGuard website hero and homepage app slideshow: `D:\Photos\Portfolio\Home Website\public\assets\apps\earguard\cover-square.webp` (copied from the square export).
- EarGuard website link preview: `D:\Photos\Portfolio\Home Website\public\assets\apps\earguard\og-cover.jpg` (copied from the OG export).
- Gumroad cover and thumbnail: files are ready in `exports/`; attach them in the seller editor before publishing.

## Copy

- Headline: **Accidental 100% happens. / Not for long.**
- Sub: EarGuard turns down unexpected Windows volume changes. Free, portable, open source.
- Pills: NO INSTALLER · 92 KB · OPEN SOURCE
- Note: Example limit. Windows volume setting, not listening loudness.

The claim language is deliberate. EarGuard reacts after Windows changes the volume
and only ever lowers it, so the cover says "turns down" and "example limit" — never
"prevents" or "protects your hearing."

## Design

- Paper `#FDFBF7`, ink `#1A1A1A`, gold `#A68D71` / `#796244`, spike red `#B44332`.
- Cormorant Garamond for the headline and wordmark; Montserrat for the small caps.
- The mascot is the app's existing logo, trimmed to its alpha bounds. It is not
  redrawn or recoloured.
- The beam is the site's own volume language: gold 0–30%, red 30–100%, a post at
  the limit, and a hatched over-limit zone.
- The wide and strip layouts use one line for the setup; the square breaks it into
  three lines so the poster fills the taller canvas.
- The page has a hairline frame, registration ticks, fine paper grain, and a soft
  top-left light so it reads as a printed object.

## Re-rendering

From this folder:

```powershell
node render.mjs
```

The script writes the masters and platform files into `exports/`. It uses the local
Playwright and Sharp install from `D:/Photos/Portfolio/Home Website`; if that
project moves, update `websitePackage` at the top of `render.mjs`. It launches the
installed Chrome and falls back to Edge. No browser download is needed.

For a quick look in a browser, open `cover.html` at the target viewport:

```text
cover.html?variant=wide     1280 × 720
cover.html?variant=square   1080 × 1080
cover.html?variant=strip    1280 × 640
```

## Editing

- Copy lives in `cover.html`.
- Layout and variants live in `cover.css`; the `html[data-variant="..."]` blocks at
  the bottom override the base wide layout.
- Change the source, then re-render. Do not hand-edit the PNG or JPEG exports.

## Assets

- `assets/earguard-mark.png` — trimmed original mark (186 × 225).
- `assets/earguard-mark-3x.png` — 3× Lanczos upscale (558 × 675), used by the HTML
  so the logo stays crisp at every export size.
- `assets/fonts/` — Cormorant Garamond 300 / 300 italic / 500 and Montserrat
  400 / 500 / 600, from Fontsource (SIL Open Font License).
- The untrimmed source is `../../EarGuard-transparent.png`.
