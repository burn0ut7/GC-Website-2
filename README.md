# Global Conflicts website

The selected community website, formerly prototype two.

## Local preview
Double-click `Launch.cmd`. Python 3 is required. Keep its window open while viewing.
The preview binds only to 127.0.0.1 and opens the home page automatically.

## Editing
- `dist/app.js`: landing-page content and introduction video.
- `dist/styles.css`: styling and responsive layout.
- `dist/`: images, font assets, and the site entry point.

Only the landing page (`#home`) is active. Old page links return to the landing page.
The introduction video starts at 25% volume and requires internet access.
Additional pages and real telemetry playback will be built on the backend foundation later.

`.openai/hosting.json` retains the existing hosted site's identity.
Font license information is preserved in `dist/fonts/`.
