# PDF Translator

WPF (.NET 10) app that translates PDFs while keeping the original layout, with a side-by-side original/translated preview.

**VERY MUCH A WIP** there may be issues with certain PDF's

## How it works

1. **Layout** – PdfPig letters (advance-width boxes, size, weight, colour) are grouped into lines by baseline, then into blocks.
   Table borders and rules are detected from the rendered page; text is never grouped across them, so every table cell is its own block.
   A block also ends at a change of size or weight, a short last line, a line ending in a colon, a list marker (`1)`, `（一）`, `①`, `•`…)
   or a bold `Label:` lead, so headings and list items stay separate. Bold comes from the font weight/name or from fill+stroke text
   (how Word/WPS export bold CJK).
2. **OCR** – pages are rendered with PDFium and read by the first engine that can handle the document's script:
   Windows OCR (`Windows.Media.Ocr`) if an installed Windows OCR language covers it; **PaddleOCR PP-OCRv5** (RapidOcrNet / ONNX)
   for Chinese and Japanese; otherwise **Tesseract 5**. Paddle's Chinese recogniser (85 MB, SHA-256 checked) and Tesseract language
   data are downloaded on first use to `%LOCALAPPDATA%\PdfTranslator\models` and `…	essdata`. What gets OCR'd:
   - *Images + scanned pages* (default): every embedded image, plus whole pages that have no text layer.
   - *Whole page*: OCRs everything, keeping only words the text layer doesn't already cover.
   OCR words go through the same rule-aware line/block builder; bold is estimated from ink density.
3. **Translate** – blocks go to the selected service in one batch per page. Small graphics inside a line of text (key and
   button icons) keep the sentence whole: they travel as `{{1}}` placeholders and are redrawn at the matching place in the
   translation. List numbers are split off and localised (`二．` → `2.`, `①` → `(1)`),
   blocks without any letters of the page's script (codes such as `F52983/4`, `25000/25201`) are left as they are, and CJK punctuation is
   converted for Latin-script targets.
4. **Compose** – PDFsharp copies every original page untouched (vectors, images, fonts), paints each block's box in its sampled background
   colour, and draws the translation in a matching font/style/colour. Blocks may grow into free background to the right (single lines),
   below and above (e.g. table-cell padding); cover boxes never cross detected rules. Each block is fitted, then blocks that shared a
   source style are normalised to the same output size.

## Editing blocks

- **Original pane:** click a block to select it. **Translated pane:** drag to move, drag the corner handle to resize, double-click (or F2) to edit text in place, Ctrl+click for multi-select, arrows nudge (Shift = 10 pt).
- **Edit bar / right-click menu:** bold, italic, size (− / + / exact / Auto), alignment, Merge (Ctrl+M), Split into lines, Keep original (Del), Reset layout, Re-translate.
- **Shift+drag on the original page** draws a block around text the detector missed; the app OCRs just that box and translates it (or lets you type it). **Delete block** removes a block.
- **Ctrl + mouse wheel** zooms both pages around the cursor.
- **Images & stamps** mode lists the page's removable objects: raster images (purple), form objects such as stamps, watermarks
  and logos (teal), and annotations (e.g. a CONFIDENTIAL stamp). Click to select, **Delete image** (Del), **…on all pages** for
  objects repeated at the same place on every page, **Restore**. Objects are removed from the page content itself (the draw
  operation or annotation is deleted, together with any link or popup attached to it), not painted over.
- Undo/Redo per page (Ctrl+Z / Ctrl+Y); **Re-analyse page** detects the blocks again.
- Hand-edited text is kept when the page is translated again.

## Translation services

| Service | Needs |
|---|---|
| MyMemory (default) | nothing; optional e-mail raises the daily quota |
| Offline AI (Ollama, on this PC) | nothing after a one-time install (Settings › Offline AI › Install, or `PdfTranslator.exe --install-ollama [model]`): [Ollama](https://ollama.com) in `%LOCALAPPDATA%\PdfTranslator\ollama` (about 1.6 GB download; graphics-card libraries the PC can't use are removed) plus a language model from [ollama.com/library](https://ollama.com/library), e.g. `qwen3:4b` (2.5 GB) or `qwen3:8b` (5.2 GB). An Ollama already installed on the PC is used instead. Translates a page at a time with the same instructions as Claude, so it follows meaning far better than LibreTranslate; minutes per page on a laptop CPU. Free, private, offline |
| LibreTranslate offline (on this PC) | nothing after a one-time install (Settings › Offline translation › Install, or `PdfTranslator.exe --install-offline`): [LibreTranslate](https://github.com/LibreTranslate/LibreTranslate) with its own Python 3.11 (via uv) in `%LOCALAPPDATA%\PdfTranslator\libretranslate`, ~1 GB. The app starts it on 127.0.0.1:5099 with only the languages in use (each new pair downloads a ~100 MB model once) and stops it on exit. Free, private, offline |
| Google Translate (free web endpoint) | nothing; unofficial – Google refuses it from some networks/clients |
| LibreTranslate | server URL (+ key for libretranslate.com) |
| DeepL API | API key (Free keys end in `:fx`) |
| Google Cloud Translation | API key |
| Microsoft Translator (Azure) | key + region |
| Claude (Claude Code sign-in) | the Claude Code CLI signed in to your Claude account: a saved `/login`, or a token from `claude setup-token` pasted into Settings › Claude Code token; no API key. Found on PATH, in `~\.local\bin`, or the copy bundled with Claude Desktop |
| Claude (Anthropic API key) | API key or `ANTHROPIC_API_KEY`; default model `claude-opus-5-5` with server-side refusal fallback |
| Extract only | nothing; finds the blocks **without translating** them – fill them in the editor, or ask Claude in Claude Code / Claude Desktop to translate the open PDF through MCP |

When a service needs a key that isn't set, a dialog explains how to get one, with buttons for the sign-up page, Settings,
and a free alternative; links in any message are clickable. Keys are entered under **Settings…** and stored DPAPI-encrypted in `%APPDATA%\PdfTranslator\settings.json`.

## Using it

Open (or drop) a PDF → pick languages, service and OCR mode → **Translate page** / **Translate all** → review side by side.
The block list under the preview shows every block; edit a translation to re-render the page, or select blocks and **Re-translate selected**. **Save translated PDF…** writes `<name>.<lang>.pdf`.

**Several files:** choose more than one in **Open PDF…**, drop several PDFs (or a folder) on the window, or use **Queue › Add files…**. **Start queue** translates them one after another with the current From / To / Service / OCR and saves each as `<name>.<lang>.pdf` next to its original, or in the folder chosen with **Save to a folder…**; an existing file is never overwritten (`<name>.<lang> (2).pdf`). A file that fails is marked and the queue moves on; a missing key, sign-in or model stops it with the fix. Double-click a saved file to open it.

## Claude via MCP (no API key needed)

MCP works the other way round from the Translate buttons: Claude drives the app. Ask Claude (in Claude Code or Claude Desktop)
to translate the PDF that is open in PDF Translator; the buttons don't call Claude. For button-driven Claude translation use the
"Claude (Claude Code sign-in)" service.


While the app runs it serves an MCP endpoint at `http://localhost:8765/mcp` (loopback only; port and on/off in Settings).
Claude can then read the open document, look at page images, write translations and fix the layout while you watch the side-by-side view.
Click the **MCP** indicator in the status bar: **Add to Claude Code** runs the `claude mcp add` command in a terminal,
**Add to Claude Desktop** writes the entry into Claude Desktop's config (restart it afterwards).

```
claude mcp add --transport http pdf-translator http://localhost:8765/mcp
```

Claude Desktop (stdio; starts the app if it isn't running) – in `claude_desktop_config.json`:

```json
"mcpServers": { "pdf-translator": { "command": "C:\\Users\\<you>\\source\\build\\PdfTranslator\\bin\\Release\\net10.0-windows10.0.19041.0\\win-x64\\PdfTranslator.exe", "args": ["--mcp-stdio"] } }
```

Tools: `get_status`, `open_pdf`, `set_options`, `analyze_page`, `get_blocks`, `get_page_image`, `set_translations`, `translate_page`,
`update_block`, `merge_blocks`, `split_block`, `add_block`, `delete_block`, `list_images`, `delete_images`, `restore_images`,
`show_page`, `save_pdf`. With `get_page_image` Claude can
also read labels the OCR missed and place them with `add_block`. Every change goes through the app's undo.

## Command line

```
PdfTranslator.exe --translate in.pdf out.pdf --to de [--from auto] [--provider mymemory] [--ocr auto|fullpage|off]
PdfTranslator.exe --render in.pdf outDir [--dpi 110]
PdfTranslator.exe --snapshot in.pdf out.png [--to de] [--provider mymemory] [--page 1]
PdfTranslator.exe --make-sample out.pdf
PdfTranslator.exe --providers
PdfTranslator.exe --mcp-stdio [--port 8765]
PdfTranslator.exe --dump in.pdf <page>        # letters with font, size, rendering mode, detected bold
PdfTranslator.exe --structure in.pdf <page>   # images, form objects and annotations the page draws
PdfTranslator.exe --ocr-box in.pdf <page> x y w h [paddle|chi_sim+eng]   # what OCR reads in a box (pt)
```

## Build

Uses a portable .NET 10 SDK at `%USERPROFILE%\source\tools\dotnet10` (no system install). Build output goes to
`%USERPROFILE%\source\build\PdfTranslator`). The built app needs the .NET 10 Desktop Runtime; `build.ps1 -Run` points it at
the SDK's copy.

```
powershell -File build.ps1 [-Run]
powershell -File build.ps1 -Installer
```

`-Installer` builds two single-file installers in the BasicConnect style (`tools\Setup`, .NET Framework 4.7.2, which every
Windows 10/11 PC has):

- `dist\PdfTranslatorSetup-<version>.exe` (~45 MB): the app without .NET. When the PC has no .NET 10 Desktop Runtime, setup
  downloads the newest one from Microsoft (taken from Microsoft's release list, checked against its SHA-512): for **all users**
  Microsoft's runtime installer, run quietly (machine-wide, patched by Microsoft Update); for **just me** Microsoft's zips,
  unpacked into a `dotnet` folder beside the program — no administrator rights. The app's launcher looks there first, then
  in the machine-wide locations (`AppHostRelativeDotNet`).
- `dist\PdfTranslatorSetup-<version>-offline.exe` (~105 MB): self-contained, nothing to download (also published to `dist\PdfTranslator`).

Both: per-user by default, all-users with elevation on request, folder choice, desktop shortcut, Add/Remove Programs entry,
and an `Uninstall.exe` copy of themselves; files from an earlier version that the new one doesn't carry are removed.
Command line: `/S` silent, `/allusers`, `/D="C:\Path"`, `/nodesktop`, `/uninstall`, and `/S /portable /D="C:\Path"` for just
the program files (and a private .NET when needed) without shortcuts or registry entries.
An `EULA.txt` in the project root is shown and must be accepted when present.

The icon is `Assets\PdfTranslator.ico`, generated from `PDF_TRANSLATE.png` by `tools\make-icon.ps1`
(the supplied `PDF_TRANSLATE.ico` has an empty 256×256 frame).

## Limitations

- The original text stays in the PDF underneath the cover boxes, so copy/search finds both languages.
- Vertical text is left untranslated (rotated pages are translated as displayed).
- No bidi/shaping for Arabic, Hebrew or Indic scripts (PDFsharp limitation).
- Bold/italic inside a sentence (other than a leading `Label:`) is not carried over; the block takes its dominant style.
- Text cut off at an image edge (e.g. "X9：编码") may not be detected; add it with Shift+drag or `add_block`.

## Licences

Source: <https://github.com/MysticFrog/PDF-Translator> (also linked from **About…** in the app).

The app ships with open-source components under MIT, Apache-2.0, BSD and Boost licences: the .NET runtime, PDFium,
PdfPig, PDFsharp, PDFtoImage, SkiaSharp, Tesseract and Leptonica, RapidOcrNet with the PaddleOCR models, ONNX Runtime,
Clipper2 and the Anthropic SDK. Their licences and notices are in `THIRD-PARTY-NOTICES.txt`, installed next to the
program and opened from **About… › Third-party notices**. `build.ps1 -Installer` regenerates it from the restored
packages (`tools\Notices.ps1`), so it always matches what ships.

Ollama (MIT) and its models (each under its own licence, e.g. Qwen3: Apache-2.0) are not bundled either: they are
downloaded on the PC that uses offline AI. LibreTranslate (AGPL-3.0) is not bundled: the offline install downloads it from its official source onto the PC that
uses it, where it runs as a separate program.
