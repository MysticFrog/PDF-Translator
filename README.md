# PDF Translator

WPF (.NET 8) app that translates PDFs while keeping the original layout, with a side-by-side original/translated preview.

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
3. **Translate** – blocks go to the selected service in one batch per page. List numbers are split off and localised (`二．` → `2.`, `①` → `(1)`),
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
| Google Translate (free web endpoint) | nothing; unofficial – Google refuses it from some networks/clients |
| LibreTranslate | server URL (+ key for libretranslate.com) |
| DeepL API | API key (Free keys end in `:fx`) |
| Google Cloud Translation | API key |
| Microsoft Translator (Azure) | key + region |
| Claude (Claude Code sign-in) | the Claude Code CLI signed in to your Claude account: a saved `/login`, or a token from `claude setup-token` pasted into Settings › Claude Code token; no API key. Found on PATH, in `~\.local\bin`, or the copy bundled with Claude Desktop |
| Claude (Anthropic API key) | API key or `ANTHROPIC_API_KEY`; default model `claude-opus-5-5` with server-side refusal fallback |
| Extract only | nothing; finds the blocks **without translating** them – fill them in the editor, or ask Claude in Claude Code / Claude Desktop to translate the open PDF through MCP |

Keys are entered under **Settings…** and stored DPAPI-encrypted in `%APPDATA%\PdfTranslator\settings.json`.

## Using it

Open (or drop) a PDF → pick languages, service and OCR mode → **Translate page** / **Translate all** → review side by side.
The block list under the preview shows every block; edit a translation to re-render the page, or select blocks and **Re-translate selected**. **Save translated PDF…** writes `<name>.<lang>.pdf`.

## Claude via MCP (no API key needed)

MCP works the other way round from the Translate buttons: Claude drives the app. Ask Claude (in Claude Code or Claude Desktop)
to translate the PDF that is open in PDF Translator; the buttons don't call Claude. For button-driven Claude translation use the
"Claude (Claude Code sign-in)" service.


While the app runs it serves an MCP endpoint at `http://localhost:8765/mcp` (loopback only; port and on/off in Settings).
Claude can then read the open document, look at page images, write translations and fix the layout while you watch the side-by-side view.
Click the **MCP** indicator in the status bar to copy the setup command.

```
claude mcp add --transport http pdf-translator http://localhost:8765/mcp
```

Claude Desktop (stdio; starts the app if it isn't running) – in `claude_desktop_config.json`:

```json
"mcpServers": { "pdf-translator": { "command": "C:\\Users\\<you>\\source\\build\\PdfTranslator\\bin\\Release\\net8.0-windows10.0.19041.0\\win-x64\\PdfTranslator.exe", "args": ["--mcp-stdio"] } }
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

Uses a portable .NET 8 SDK at `%USERPROFILE%\source\tools\dotnet8` (no system install). Build output goes to
`%USERPROFILE%\source\build\PdfTranslator` (kept off Google Drive by `Directory.Build.props`).

```
powershell -File build.ps1 [-Run]
powershell -File build.ps1 -Installer
```

`-Installer` publishes the app self-contained (no .NET install needed on the target PC) and builds
`PdfTranslatorSetup-<version>.exe`, a single-file installer in the BasicConnect style (`tools\Setup`, .NET Framework 4.7.2):
per-user by default, all-users with elevation on request, folder choice, desktop shortcut, Add/Remove Programs entry, and an
`Uninstall.exe` copy of itself. Command line: `/S` silent, `/allusers`, `/D="C:\Path"`, `/nodesktop`, `/uninstall`.
An `EULA.txt` in the project root is shown and must be accepted when present.

The icon is `Assets\PdfTranslator.ico`, generated from `PDF_TRANSLATE.png` by `tools\make-icon.ps1`
(the supplied `PDF_TRANSLATE.ico` has an empty 256×256 frame).

## Limitations

- The original text stays in the PDF underneath the cover boxes, so copy/search finds both languages.
- Rotated pages (`/Rotate`) and vertical text are copied unchanged.
- No bidi/shaping for Arabic, Hebrew or Indic scripts (PDFsharp limitation).
- Bold/italic inside a sentence (other than a leading `Label:`) is not carried over; the block takes its dominant style.
- Text cut off at an image edge (e.g. "X9：编码") may not be detected; add it with Shift+drag or `add_block`.
