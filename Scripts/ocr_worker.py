import json
import math
import os
import sys
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort
try:
    import pyclipper
except ImportError as error:
    raise RuntimeError(
        "OCR 需要 pyclipper 进行 DB 检测框扩张，请安装 pyclipper。"
    ) from error


DET_THRESHOLD = 0.3
BOX_THRESHOLD = 0.6
REC_HEIGHT = 48
REC_WIDTH = 320
REC_MAX_WIDTH = 3200
MAX_DET_SIDE = 960
DET_STRIDE = 128
MAX_CANDIDATES = 1000
UNCLIP_RATIO = 1.5


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: ocr_worker.py <image> <output>", file=sys.stderr)
        return 2

    image_path = Path(sys.argv[1])
    output_path = Path(sys.argv[2])
    if not image_path.is_file():
        print(f"image not found: {image_path}", file=sys.stderr)
        return 3

    try:
        image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
        if image is None:
            raise RuntimeError(f"无法读取 OCR 图片: {image_path}")

        engine = OcrEngine.from_environment()
        lines = engine.recognize(image)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(
            json.dumps(
                {
                    "text": "\n".join(line["text"] for line in lines),
                    "lines": lines,
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        return 0
    except Exception as error:
        print(f"OCR 执行失败: {error}", file=sys.stderr)
        return 1


class OcrEngine:
    def __init__(self, det_path: Path, rec_path: Path, dictionary_path: Path):
        self.det_session, self.rec_session, self.device = create_sessions(
            det_path, rec_path
        )
        self.dictionary = load_dictionary(dictionary_path)
        if len(self.dictionary) + 1 != self.rec_session.get_outputs()[0].shape[-1]:
            expected = self.rec_session.get_outputs()[0].shape[-1]
            actual = len(self.dictionary) + 1
            raise RuntimeError(
                f"OCR 字典与识别模型不匹配：模型需要 {expected} 类，字典推导为 {actual} 类"
            )

    @classmethod
    def from_environment(cls):
        application_directory = Path(__file__).resolve().parent.parent
        model_dir = Path(
            os.environ.get(
                "PDFREADER_OCR_MODEL_DIR",
                application_directory / "ocr_model",
            )
        )
        det_path = Path(
            os.environ.get("PDFREADER_OCR_DET_MODEL", model_dir / "det.onnx")
        )
        rec_path = Path(
            os.environ.get("PDFREADER_OCR_REC_MODEL", model_dir / "rec.onnx")
        )
        configured_dictionary = os.environ.get("PDFREADER_OCR_DICTIONARY")
        dictionary_path = Path(configured_dictionary) if configured_dictionary else next(
            (
                path
                for path in (
                    model_dir / "ppocr_keys_v1.txt",
                    model_dir / "rec.yml",
                    model_dir / "inference.yml",
                )
                if path.is_file()
            ),
            model_dir / "ppocr_keys_v1.txt",
        )

        for path in (det_path, rec_path, dictionary_path):
            if not path.is_file():
                raise FileNotFoundError(
                    f"找不到 OCR 资源 {path}。请准备 det.onnx、rec.onnx 和 ppocr_keys_v1.txt，"
                    "或将识别模型的 inference.yml 放入模型目录。"
                )
        return cls(det_path, rec_path, dictionary_path)

    def recognize(self, image: np.ndarray):
        boxes, resized_size = self.detect(image)
        if not boxes:
            return []

        crops = [perspective_crop(image, box, resized_size) for box in boxes]
        texts, scores = self.recognize_lines(crops)
        return [
            {"text": text, "score": float(score)}
            for text, score in zip(texts, scores)
            if text.strip()
        ]

    def detect(self, image: np.ndarray):
        original_height, original_width = image.shape[:2]
        scale = MAX_DET_SIDE / max(original_height, original_width)
        resized_width = max(1, int(original_width * scale))
        resized_height = max(1, int(original_height * scale))
        input_width = max(
            DET_STRIDE, int(math.ceil(resized_width / DET_STRIDE) * DET_STRIDE)
        )
        input_height = max(
            DET_STRIDE, int(math.ceil(resized_height / DET_STRIDE) * DET_STRIDE)
        )

        resized = cv2.resize(image, (input_width, input_height))
        tensor = resized.astype(np.float32) / 255.0
        tensor = (tensor - np.array([0.485, 0.456, 0.406], dtype=np.float32)) / np.array(
            [0.229, 0.224, 0.225], dtype=np.float32
        )
        tensor = np.transpose(tensor, (2, 0, 1))[None, ...]

        input_name = self.det_session.get_inputs()[0].name
        prediction = self.det_session.run(None, {input_name: tensor})[0]
        probability = prediction[0, 0]
        mask = (probability > DET_THRESHOLD).astype(np.uint8) * 255
        boxes = db_boxes_from_bitmap(
            probability,
            mask,
            original_width,
            original_height,
            BOX_THRESHOLD,
            UNCLIP_RATIO,
        )
        boxes.sort(key=lambda box: (float(box[0, 1]), float(box[0, 0])))
        for index in range(len(boxes) - 1):
            for previous in range(index, -1, -1):
                if (
                    abs(float(boxes[previous + 1][0, 1]) - float(boxes[previous][0, 1]))
                    < 10
                    and boxes[previous + 1][0, 0] < boxes[previous][0, 0]
                ):
                    boxes[previous], boxes[previous + 1] = (
                        boxes[previous + 1],
                        boxes[previous],
                    )
                else:
                    break
        return boxes, (original_width, original_height)

    def recognize_lines(self, crops):
        max_width = max(
            REC_WIDTH,
            min(
                REC_MAX_WIDTH,
                max(
                    int(math.ceil(REC_HEIGHT * crop.shape[1] / max(crop.shape[0], 1)))
                    for crop in crops
                ),
            ),
        )
        tensors = np.stack(
            [prepare_recognition_crop(crop, max_width) for crop in crops]
        )
        input_name = self.rec_session.get_inputs()[0].name
        prediction = self.rec_session.run(None, {input_name: tensors})[0]
        indexes = prediction.argmax(axis=-1)
        probabilities = prediction.max(axis=-1)

        texts = []
        scores = []
        for line_indexes, line_probabilities in zip(indexes, probabilities):
            previous = -1
            characters = []
            selected_scores = []
            for index, probability in zip(line_indexes, line_probabilities):
                index = int(index)
                if index != 0 and index != previous:
                    if index > len(self.dictionary):
                        raise RuntimeError(f"识别模型输出了未知字符索引: {index}")
                    characters.append(self.dictionary[index - 1])
                    selected_scores.append(float(probability))
                previous = index
            texts.append("".join(characters))
            scores.append(sum(selected_scores) / len(selected_scores) if selected_scores else 0.0)
        return texts, scores


def create_sessions(det_path: Path, rec_path: Path):
    requested_device = os.environ.get("PDFREADER_OCR_DEVICE", "auto").strip().lower()
    if requested_device not in {"auto", "cpu", "directml"}:
        raise ValueError("PDFREADER_OCR_DEVICE 只能是 auto、cpu 或 directml")

    available = set(ort.get_available_providers())
    directml_available = "DmlExecutionProvider" in available
    if requested_device == "directml" and not directml_available:
        raise RuntimeError(
            f"当前 ONNX Runtime 没有 DirectML provider，可用 provider: {sorted(available)}"
        )

    use_directml = requested_device == "directml" or (
        requested_device == "auto" and directml_available
    )
    if use_directml:
        try:
            sessions = open_sessions(
                det_path,
                rec_path,
                ["DmlExecutionProvider", "CPUExecutionProvider"],
            )
            print("OCR device: DirectML", file=sys.stderr)
            return sessions[0], sessions[1], "directml"
        except Exception as error:
            if requested_device == "directml":
                raise RuntimeError(f"DirectML OCR 初始化失败: {error}") from error
            print(f"DirectML 初始化失败，回退 CPU: {error}", file=sys.stderr)

    sessions = open_sessions(det_path, rec_path, ["CPUExecutionProvider"])
    print("OCR device: CPU", file=sys.stderr)
    return sessions[0], sessions[1], "cpu"


def open_sessions(det_path: Path, rec_path: Path, providers):
    options = ort.SessionOptions()
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    det_session = ort.InferenceSession(str(det_path), options, providers=providers)
    rec_session = ort.InferenceSession(str(rec_path), options, providers=providers)
    return det_session, rec_session


def load_dictionary(path: Path):
    lines = path.read_text(encoding="utf-8").splitlines()
    if path.suffix.lower() in {".yml", ".yaml"}:
        characters = []
        in_dictionary = False
        for line in lines:
            if line.strip() == "character_dict:":
                in_dictionary = True
                continue
            if in_dictionary and line.startswith("  - "):
                characters.append(parse_yaml_character(line[4:].rstrip("\r\n")))
                continue
            if in_dictionary and characters and line.strip() and not line.startswith("  - "):
                break
    else:
        characters = [line for line in lines if line]

    if not characters:
        raise RuntimeError(f"OCR 字典为空: {path}")
    # PP-OCR recognition models reserve a class for the regular space in addition
    # to the characters listed in inference.yml.
    if " " not in characters:
        characters.append(" ")
    return characters


def parse_yaml_character(value: str):
    trimmed = value.strip()
    if len(trimmed) >= 2 and trimmed[0] == "'" and trimmed[-1] == "'":
        return trimmed[1:-1].replace("''", "'")
    if len(trimmed) >= 2 and trimmed[0] == '"' and trimmed[-1] == '"':
        return json.loads(trimmed)
    return value


def order_box_points(points: np.ndarray):
    points = np.asarray(points, dtype=np.float32)
    sums = points.sum(axis=1)
    differences = np.diff(points, axis=1).reshape(-1)
    ordered = np.empty((4, 2), dtype=np.float32)
    ordered[0] = points[np.argmin(sums)]
    ordered[2] = points[np.argmax(sums)]
    ordered[1] = points[np.argmin(differences)]
    ordered[3] = points[np.argmax(differences)]
    return ordered


def db_boxes_from_bitmap(
    probability: np.ndarray,
    bitmap: np.ndarray,
    destination_width: int,
    destination_height: int,
    box_threshold: float,
    unclip_ratio: float,
):
    """Reproduce PP-OCR's quad DBPostProcess without Paddle dependencies."""
    height, width = bitmap.shape
    width_scale = destination_width / width
    height_scale = destination_height / height
    contours, _ = cv2.findContours(bitmap, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    boxes = []

    for contour in contours[:MAX_CANDIDATES]:
        points, short_side = get_mini_boxes(contour)
        if short_side < 3:
            continue
        points = np.asarray(points, dtype=np.float32)
        score = box_score_fast(probability, points)
        if score < box_threshold:
            continue

        expanded = unclip_polygon(points, unclip_ratio)
        if expanded is None or len(expanded) == 0:
            continue
        box, expanded_short_side = get_mini_boxes(expanded.reshape(-1, 1, 2))
        if expanded_short_side < 5:
            continue

        box = np.asarray(box, dtype=np.float32)
        box[:, 0] = np.clip(np.round(box[:, 0] * width_scale), 0, destination_width - 1)
        box[:, 1] = np.clip(np.round(box[:, 1] * height_scale), 0, destination_height - 1)
        boxes.append(box)
    return boxes


def get_mini_boxes(contour: np.ndarray):
    bounding_box = cv2.minAreaRect(contour)
    points = sorted(list(cv2.boxPoints(bounding_box)), key=lambda point: point[0])
    index_a, index_b, index_c, index_d = 0, 1, 2, 3
    if points[1][1] > points[0][1]:
        index_a, index_d = 0, 1
    else:
        index_a, index_d = 1, 0
    if points[3][1] > points[2][1]:
        index_b, index_c = 2, 3
    else:
        index_b, index_c = 3, 2
    return [points[index_a], points[index_b], points[index_c], points[index_d]], min(
        bounding_box[1]
    )


def box_score_fast(probability: np.ndarray, box: np.ndarray):
    height, width = probability.shape[:2]
    xmin = max(0, min(math.floor(float(box[:, 0].min())), width - 1))
    xmax = max(0, min(math.ceil(float(box[:, 0].max())), width - 1))
    ymin = max(0, min(math.floor(float(box[:, 1].min())), height - 1))
    ymax = max(0, min(math.ceil(float(box[:, 1].max())), height - 1))
    if xmax < xmin or ymax < ymin:
        return 0.0
    mask = np.zeros((ymax - ymin + 1, xmax - xmin + 1), dtype=np.uint8)
    local_box = box.copy()
    local_box[:, 0] -= xmin
    local_box[:, 1] -= ymin
    cv2.fillPoly(mask, [local_box.astype(np.int32)], 1)
    return float(cv2.mean(probability[ymin : ymax + 1, xmin : xmax + 1], mask)[0])


def unclip_polygon(box: np.ndarray, ratio: float):
    area = abs(float(cv2.contourArea(box)))
    perimeter = float(cv2.arcLength(box, True))
    if area <= 0 or perimeter <= 0:
        return None
    distance = area * ratio / perimeter
    offset = pyclipper.PyclipperOffset()
    offset.AddPath(
        np.round(box).astype(np.int64).tolist(),
        pyclipper.JT_ROUND,
        pyclipper.ET_CLOSEDPOLYGON,
    )
    expanded = offset.Execute(distance)
    return np.asarray(expanded, dtype=np.float32) if expanded else None


def perspective_crop(image: np.ndarray, box: np.ndarray, resized_size):
    source_box = np.asarray(box, dtype=np.float32)
    target_width = max(
        1,
        int(
            max(
                np.linalg.norm(source_box[0] - source_box[1]),
                np.linalg.norm(source_box[2] - source_box[3]),
            )
        ),
    )
    target_height = max(
        1,
        int(
            max(
                np.linalg.norm(source_box[0] - source_box[3]),
                np.linalg.norm(source_box[1] - source_box[2]),
            )
        ),
    )
    target = np.array(
        [
            [0, 0],
            [target_width, 0],
            [target_width, target_height],
            [0, target_height],
        ],
        dtype=np.float32,
    )
    transform = cv2.getPerspectiveTransform(source_box, target)
    crop = cv2.warpPerspective(
        image,
        transform,
        (target_width, target_height),
        borderMode=cv2.BORDER_REPLICATE,
        flags=cv2.INTER_CUBIC,
    )
    if crop.shape[0] / max(crop.shape[1], 1) >= 1.5:
        crop = cv2.rotate(crop, cv2.ROTATE_90_CLOCKWISE)
    return crop


def prepare_recognition_crop(image: np.ndarray, target_width: int):
    height, width = image.shape[:2]
    ratio = width / max(height, 1)
    resized_width = min(target_width, max(1, int(math.ceil(REC_HEIGHT * ratio))))
    resized = cv2.resize(image, (resized_width, REC_HEIGHT))
    tensor = resized.astype(np.float32) / 255.0
    tensor = (tensor - 0.5) / 0.5
    padded = np.zeros((REC_HEIGHT, target_width, 3), dtype=np.float32)
    padded[:, :resized_width] = tensor
    return np.transpose(padded, (2, 0, 1))


if __name__ == "__main__":
    raise SystemExit(main())
