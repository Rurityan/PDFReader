import json
import os
import sys
from pathlib import Path

# PaddlePaddle 3.3.1 can fail in the oneDNN path for PP-OCRv6 on Windows CPU.
os.environ.setdefault("FLAGS_use_mkldnn", "0")

from paddleocr import PaddleOCR


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: ocr_worker.py <image> <output>", file=sys.stderr)
        return 2

    image_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    if not image_path.is_file():
        print(f"image not found: {image_path}", file=sys.stderr)
        return 3

    ocr = PaddleOCR(
        lang="ch",
        device="cpu",
        enable_mkldnn=False,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
    )
    results = ocr.predict(str(image_path))
    lines = []

    for result in results:
        data = result.json
        if callable(data):
            data = data()
        values = data.get("res", data)
        texts = values.get("rec_texts", [])
        scores = values.get("rec_scores", [])
        for index, text in enumerate(texts):
            score = scores[index] if index < len(scores) else None
            lines.append({"text": text, "score": score})

    payload = {
        "text": "\n".join(line["text"] for line in lines),
        "lines": lines,
    }
    output_path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
