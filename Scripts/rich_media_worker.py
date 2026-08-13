"""Create an Acrobat-compatible PDF with page Sound annotations for OCR audio."""

import json
import os
import sys
from pathlib import Path

try:
    import pikepdf
    import miniaudio
except ImportError as error:
    raise RuntimeError(
        "Adobe Acrobat 富媒体导出需要 pikepdf 和 miniaudio。x64 版本应通过安装包随附；"
        "开发环境可执行 python -m pip install -r Scripts/requirements-rich-media.txt。"
    ) from error


def pdf_name(value: str):
    return pikepdf.Name(value)


def add_attachment(pdf, name: str, data: bytes, description: str):
    # Attachments are only created by this on-demand export worker.
    pdf.attachments[name] = pikepdf.AttachedFileSpec(
        pdf, data, filename=name, description=description
    )


def add_sound_annotation(pdf, page, rect, audio_path: str):
    decoded = miniaudio.decode_file(
        audio_path,
        output_format=miniaudio.SampleFormat.SIGNED16,
        nchannels=1,
        sample_rate=44100,
    )
    # PDF Sound streams use signed linear PCM. Store samples in big-endian order.
    samples = decoded.samples
    data = b"".join(int(sample).to_bytes(2, "big", signed=True) for sample in samples)
    sound = pdf.make_stream(data)
    sound["/Type"] = pdf_name("/Sound")
    sound["/R"] = int(decoded.sample_rate)
    sound["/C"] = int(decoded.nchannels)
    sound["/B"] = 16
    sound["/E"] = pdf_name("/Signed")

    annotation = pikepdf.Dictionary(
        Type=pdf_name("/Annot"),
        Subtype=pdf_name("/Sound"),
        Rect=pikepdf.Array(rect),
        Sound=sound,
        Name=pdf_name("/Speaker"),
        C=pikepdf.Array([0.17, 0.42, 0.69]),
        CA=0.55,
        Contents="PDFReader OCR audio",
        T="PDFReader",
    )
    annots = page.obj.get("/Annots")
    if annots is None:
        annots = pikepdf.Array()
        page.obj["/Annots"] = annots
    annots.append(pdf.make_indirect(annotation))


def calculate_audio_button_rects(page, x, y, width, height, audio_count):
    media_box = [float(value) for value in page.obj["/MediaBox"]]
    page_left, page_bottom, page_right, page_top = media_box
    page_width = max(1.0, page_right - page_left)
    page_height = max(1.0, page_top - page_bottom)
    gap = 2.0
    button_size = min(20.0, max(14.0, min(width, height)))
    total_width = audio_count * button_size + max(0, audio_count - 1) * gap
    if total_width > page_width:
        button_size = max(8.0, (page_width - max(0, audio_count - 1) * gap) / audio_count)
        total_width = audio_count * button_size + max(0, audio_count - 1) * gap

    desired_left = x + width - total_width - 2.0
    left = min(max(desired_left, page_left), page_right - total_width)
    desired_top = page_top - y - 2.0
    top = min(max(desired_top, page_bottom + button_size), page_top)
    rects = []
    for index in range(audio_count):
        button_left = left + index * (button_size + gap)
        rects.append([
            button_left,
            top - button_size,
            button_left + button_size,
            top,
        ])
    return rects


def main() -> int:
    if len(sys.argv) != 5 or sys.argv[1] != "export-acrobat":
        print("usage: rich_media_worker.py export-acrobat <source.pdf> <output.pdf> <manifest.json>", file=sys.stderr)
        return 2

    source_path, output_path, manifest_path = sys.argv[2:]
    with open(manifest_path, encoding="utf-8") as source:
        manifest = json.load(source)

    pdf = pikepdf.Pdf.open(source_path)
    used_names = set()
    try:
        toc = [
            [item["level"], item["title"], item["page"]]
            for item in manifest.get("bookmarks", [])
        ]
        if toc:
            pdf.Root["/Outlines"] = pdf.make_indirect(pikepdf.Dictionary())

        for index, record in enumerate(manifest.get("ocrRecords", [])):
            page_number = int(record.get("pageNumber", 0))
            if page_number < 1 or page_number > len(pdf.pages):
                continue

            x = float(record.get("x", 0))
            y = float(record.get("y", 0))
            width = max(1.0, float(record.get("width", 1)))
            height = max(1.0, float(record.get("height", 1)))
            page = pdf.pages[page_number - 1]
            audio_items = [
                audio for audio in record.get("audioFiles", [])
                if audio.get("filePath") and os.path.isfile(audio["filePath"])
            ]
            button_rects = calculate_audio_button_rects(
                page, x, y, width, height, len(audio_items)
            )
            for audio_index, audio in enumerate(audio_items):
                path = audio.get("filePath")

                suffix = Path(path).suffix.lower() or ".bin"
                attachment_name = f"audio/ocr-{index}-{audio_index}{suffix}"
                if attachment_name not in used_names:
                    add_attachment(pdf, attachment_name, Path(path).read_bytes(), "PDFReader OCR audio")
                    used_names.add(attachment_name)
                add_sound_annotation(pdf, page, button_rects[audio_index], path)

        metadata = json.dumps(manifest, ensure_ascii=False).encode("utf-8")
        add_attachment(pdf, "PDFReader-metadata.json", metadata, "PDFReader OCR metadata")
        pdf.save(output_path, linearize=False)
    finally:
        pdf.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
