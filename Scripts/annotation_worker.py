import json
import os
import sys
import fitz


def color(value):
    value = (value or "#2B6CB0").lstrip("#")
    return tuple(int(value[i:i + 2], 16) / 255 for i in (0, 2, 4))


def color_hex(value):
    if not value or len(value) < 3:
        return "#2B6CB0"
    return "#{:02X}{:02X}{:02X}".format(*(round(component * 255) for component in value[:3]))


def annotation_color(annotation, info):
    subject = info.get("subject") or ""
    if subject.startswith("PDFReaderColor:"):
        return subject.split(":", 1)[1].split(";", 1)[0]
    return color_hex(annotation.colors.get("stroke"))


def annotation_font_size(info):
    subject = info.get("subject") or ""
    if ";font:" in subject:
        try:
            return float(subject.rsplit(";font:", 1)[1])
        except ValueError:
            pass
    return 11


def flatten_points(vertices):
    for value in vertices or []:
        if isinstance(value, fitz.Point):
            yield value
        elif isinstance(value, (list, tuple)):
            if len(value) == 2 and all(isinstance(coordinate, (int, float)) for coordinate in value):
                yield fitz.Point(value[0], value[1])
            else:
                yield from flatten_points(value)


def serialize(annotation, page_number):
    rect = annotation.rect
    info = annotation.info
    kind = annotation.type[1].lower()
    names = {"text": "Text", "freetext": "Text", "highlight": "Highlight", "ink": "Freehand", "line": "Line", "square": "Rectangle"}
    vertices = list(flatten_points(annotation.vertices))
    points = [{"x": point.x, "y": point.y} for point in vertices]
    start, end = (vertices[0], vertices[-1]) if len(vertices) >= 2 else (rect.tl, rect.br)
    return {"id": f"xref:{annotation.xref}", "subtype": annotation.type[1],
            "pageNumber": page_number, "type": names.get(kind, "Unknown"), "title": info.get("title"),
            "contents": info.get("content"), "x": rect.x0, "y": rect.y0,
            "width": rect.width, "height": rect.height, "startX": start.x,
            "startY": start.y, "endX": end.x, "endY": end.y, "points": points,
            "strokeColor": annotation_color(annotation, info), "strokeWidth": annotation.border.get("width", 2) or 2,
            "fontSize": annotation_font_size(info)}


def add(page, item):
    annotation = item["annotation"]
    kind = annotation["type"]
    if isinstance(kind, str):
        kind = {"Text": 0, "Line": 1, "Highlight": 2, "Rectangle": 3, "Freehand": 4}.get(kind, -1)
    rect = fitz.Rect(annotation["x"], annotation["y"], annotation["x"] + annotation["width"], annotation["y"] + annotation["height"])
    if kind == 0:
        annot = page.add_freetext_annot(
            rect,
            annotation.get("contents", ""),
            fontsize=annotation.get("fontSize", 11),
            text_color=color(annotation.get("strokeColor")),
        )
        annot.set_info(title=annotation.get("title") or "PDF Reader", content=annotation.get("contents") or "",
                       subject="PDFReaderColor:" + (annotation.get("strokeColor") or "#2B6CB0") + ";font:" + str(annotation.get("fontSize", 11)))
        annot.update()
        return
    elif kind == 1: annot = page.add_line_annot(fitz.Point(annotation["startX"], annotation["startY"]), fitz.Point(annotation["endX"], annotation["endY"]))
    elif kind == 2: annot = page.add_highlight_annot(rect)
    elif kind == 3: annot = page.add_rect_annot(rect)
    elif kind == 4:
        points = [(point["x"], point["y"]) for point in annotation.get("points", [])]
        if len(points) < 2:
            return
        annot = page.add_ink_annot([points])
    else: return
    annot.set_info(title=annotation.get("title") or "PDF Reader", content=annotation.get("contents") or "")
    annot.set_colors(stroke=color(annotation.get("strokeColor")))
    annot.set_border(width=max(0.1, annotation.get("strokeWidth", 2)))
    annot.update()


def main():
    command = sys.argv[1]
    if command == "read":
        doc = fitz.open(sys.argv[2]); page_number = int(sys.argv[3]); page = doc[page_number - 1]
        result = [serialize(annotation, page_number) for annotation in page.annots() or []]
        with open(sys.argv[4], "w", encoding="utf-8") as output: json.dump(result, output)
        doc.close(); return
    if command == "outline":
        doc = fitz.open(sys.argv[2])
        outline = []
        for entry in doc.get_toc(simple=True):
            if len(entry) >= 3 and isinstance(entry[2], int) and entry[2] > 0:
                outline.append({"level": max(1, int(entry[0])), "title": entry[1] or "未命名书签", "pageNumber": entry[2]})
        with open(sys.argv[3], "w", encoding="utf-8") as output: json.dump(outline, output)
        doc.close(); return
    if command == "export":
        source_path, output_path, manifest_path = sys.argv[2], sys.argv[3], sys.argv[4]
        with open(manifest_path, encoding="utf-8") as source: manifest = json.load(source)
        doc = fitz.open(source_path)
        toc = [[item["level"], item["title"], item["page"]] for item in manifest.get("bookmarks", [])]
        if toc: doc.set_toc(toc)
        for record in manifest.get("ocrRecords", []):
            for audio in record.get("audioFiles", []):
                path = audio.get("filePath")
                if path and __import__("os").path.isfile(path):
                    name = "audio/{}-{}".format(record["id"], __import__("os").path.basename(path))
                    if name in doc.embfile_names(): doc.embfile_del(name)
                    with open(path, "rb") as audio_source:
                        doc.embfile_add(name, audio_source.read(), filename=__import__("os").path.basename(path), desc="PDF Reader OCR audio")
        if "PDFReader-metadata.json" in doc.embfile_names(): doc.embfile_del("PDFReader-metadata.json")
        doc.embfile_add("PDFReader-metadata.json", json.dumps(manifest, ensure_ascii=False).encode("utf-8"), filename="PDFReader-metadata.json", desc="PDF Reader OCR and audio metadata")
        doc.save(output_path, garbage=3, deflate=True); doc.close(); return
    if command == "restore":
        source_path, audio_directory, result_path = sys.argv[2], sys.argv[3], sys.argv[4]
        doc = fitz.open(source_path)
        if "PDFReader-metadata.json" not in doc.embfile_names():
            doc.close(); return
        manifest = json.loads(doc.embfile_get("PDFReader-metadata.json").decode("utf-8"))
        os.makedirs(audio_directory, exist_ok=True)
        names = set(doc.embfile_names())
        for record in manifest.get("ocrRecords", []):
            for audio in record.get("audioFiles", []):
                basename = os.path.basename(audio.get("filePath") or "audio.mp3")
                attachment_name = "audio/{}-{}".format(record.get("id"), basename)
                if attachment_name in names:
                    destination = os.path.join(audio_directory, "{}-{}".format(record.get("id"), basename))
                    with open(destination, "wb") as output: output.write(doc.embfile_get(attachment_name))
                    audio["filePath"] = destination
                else:
                    audio["filePath"] = ""
        with open(result_path, "w", encoding="utf-8") as output: json.dump(manifest, output)
        doc.close(); return
    doc = fitz.open(sys.argv[2])
    with open(sys.argv[3], encoding="utf-8") as source: changes = json.load(source)
    for item in changes:
        annotation = item["annotation"]; page = doc[annotation["pageNumber"] - 1]
        if item["kind"] in (1, "Delete", 2, "Update"):
            annotation_id = annotation.get("id", "")
            if not annotation_id.startswith("xref:"):
                raise ValueError("只能删除具有 PDF xref 标识的已保存标注")
            xref = int(annotation_id.split(":", 1)[1])
            target = next((a for a in page.annots() or [] if a.xref == xref), None)
            if target is None:
                raise ValueError(f"未找到 PDF 标注对象 xref:{xref}")
            if item["kind"] in (1, "Delete"):
                page.delete_annot(target)
            else:
                if target.type[1].lower() == "line":
                    page.delete_annot(target)
                    add(page, {"annotation": annotation})
                    continue
                target.set_info(title=annotation.get("title") or "PDF Reader", content=annotation.get("contents") or "",
                                subject="PDFReaderColor:" + (annotation.get("strokeColor") or "#2B6CB0") + ";font:" + str(annotation.get("fontSize", 11)))
                stroke = color(annotation.get("strokeColor"))
                if target.type[1].lower() in ("freetext", "square"):
                    target.set_rect(fitz.Rect(annotation["x"], annotation["y"], annotation["x"] + annotation["width"], annotation["y"] + annotation["height"]))
                if target.type[1].lower() == "freetext":
                    target.update(text_color=stroke, fontsize=annotation.get("fontSize", 11))
                else:
                    target.set_colors(stroke=stroke)
                    target.set_border(width=max(0.1, annotation.get("strokeWidth", 2)))
                    target.update()
        else: add(page, item)
    doc.saveIncr(); doc.close()


if __name__ == "__main__":
    main()
