# PDFReader Local REST API

This API lets an external OCR, vision, LLM, or TTS workflow import completed OCR text and optional audio into PDFReader. The external workflow decides page regions, performs OCR, and optionally creates audio. PDFReader stores the records, copies audio into its managed resource directory, and associates each OCR record with a same-page bookmark when available.

The API is local only. It listens on `127.0.0.1` and is not reachable from other machines. It is disabled by default; enable it in Settings and choose a port before sending requests.

## Endpoint

`POST http://127.0.0.1:{port}/api/v1/import/ocr-tts`

The PDFReader application must be running and the local API must be enabled before sending requests. The default port is `38421`.

## Authentication

Every request must include the Token configured in PDFReader settings:

```http
X-PDFReader-Token: YOUR_LOCAL_API_TOKEN
Content-Type: application/json
```

The token is shown in the Settings window under `本地自动化接口 Token`. Do not write it to source control or send it to untrusted services.

## Request Body

```json
{
  "pdfPath": "D:\\documents\\book.pdf",
  "records": [
    {
      "page": 12,
      "region": {
        "x": 120.0,
        "y": 240.0,
        "width": 860.0,
        "height": 180.0
      },
      "captureZoom": 1.0,
      "title": "Optional short title",
      "text": "Required OCR text to save.",
      "audioFile": "D:\\automation-output\\page-12-part-1.mp3"
    }
  ]
}
```

Field rules:

- `pdfPath`: required. An absolute path to an existing local PDF file.
- `records`: required. A JSON array. An empty array is valid but imports nothing.
- `page`: required. One-based PDF page number. Values less than `1` are ignored.
- `region`: required. OCR region in PDFReader page coordinates: `x`, `y`, `width`, `height`.
- `captureZoom`: optional. The zoom used to produce the region coordinates. Use `1.0` for PDF page coordinates. Values less than or equal to `0` become `1.0`.
- `title`: optional. If empty, PDFReader derives a title from the first 32 characters of `text`.
- `text`: required. Empty or whitespace-only records are ignored.
- `audioFile`: optional. An existing local audio file path. PDFReader copies the file into its own voice resource directory. It does not read remote URLs.

Coordinates must use the PDF page origin at the top-left. `x` and `y` identify the top-left corner of the OCR region. `width` and `height` are the region size. Use the same coordinate system consistently for all records in a PDF.

## Import Behavior

For each valid record, PDFReader:

1. Finds an existing PDF record with the same absolute `pdfPath`, or creates one.
2. Creates an OCR record with the supplied page, region, title, and text.
3. Finds bookmarks on the same page. If several match, it attaches to the deepest bookmark. If no bookmark matches, the OCR record remains unattached.
4. If `audioFile` exists, copies it into `user_data/resource/voice` and creates the linked audio record.
5. Refreshes the current UI when the imported PDF is currently open in the workspace.

The API does not call OCR or TTS models. It only imports final results.

REST-imported OCR records are marked as externally imported. If no same-page bookmark exists,
they remain unattached and are preserved by startup cleanup. Creating a bookmark on that page
or choosing `重新读取 OCR 记录` from the bookmark context menu attaches those pending records;
after attachment they follow the normal bookmark-owned lifecycle.

### Duplicate Requests

The import is idempotent for the same OCR data. A record is considered a duplicate when
`pdfPath`, `page`, trimmed `text`, and the four region values (`x`, `y`, `width`, `height`)
match an existing record. Region values allow a difference of at most `0.01` to account for
floating-point serialization. Duplicate records are skipped. If a duplicate has no usable
audio and the request provides an existing `audioFile`, the audio is copied and attached to
the existing record instead of creating another OCR record.

## Success Response

HTTP `200`:

```json
{
  "imported": 2,
  "pdf_path": "D:\\documents\\book.pdf"
}
```

`imported` is the number of valid records written. Records with invalid page numbers or blank `text` are ignored.

## Error Responses

| Status | Meaning |
| --- | --- |
| `400` | Invalid JSON, or `pdfPath` is missing or does not point to an existing file. The response JSON contains the exact reason in `error`. |
| `401` | Missing or incorrect `X-PDFReader-Token`. |
| `404` | Incorrect HTTP method or endpoint path. |
| `500` | Import failure. Inspect the returned `error` text and PDFReader status/log output. |

## curl Example

```powershell
$token = "YOUR_LOCAL_API_TOKEN"
$body = @'
{
  "pdfPath": "D:\\documents\\book.pdf",
  "records": [
    {
      "page": 1,
      "region": { "x": 72, "y": 110, "width": 420, "height": 96 },
      "captureZoom": 1.0,
      "title": "Introduction",
      "text": "This is OCR text produced by an external workflow.",
      "audioFile": "D:\\automation-output\\book-p001-001.mp3"
    }
  ]
}
'@

Invoke-RestMethod `
  -Method Post `
  -Uri "http://127.0.0.1:38421/api/v1/import/ocr-tts" `
  -Headers @{ "X-PDFReader-Token" = $token } `
  -ContentType "application/json" `
  -Body $body
```

## Recommended Automation Contract

An external agent should follow this sequence:

1. Keep the PDFReader application running.
2. Open the target PDF in PDFReader at least once when the UI should refresh immediately after import.
3. Read the PDF externally and choose OCR regions page by page.
4. Produce OCR text and, if needed, audio files locally.
5. Send records in batches for one PDF. Retrying a request creates new OCR records, so do not retry after an uncertain success without checking the response.
6. If OCR should be attached automatically, create or import same-page bookmarks before calling the API.
