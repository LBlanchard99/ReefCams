import time
from typing import Iterable, List, Sequence, Tuple

import cv2
import numpy as np
import onnxruntime as ort


def letterbox(image: np.ndarray, new_shape: int, color=(114, 114, 114)) -> Tuple[np.ndarray, float, Tuple[int, int]]:
    h, w = image.shape[:2]
    r = min(new_shape / h, new_shape / w)
    new_unpad = (int(round(w * r)), int(round(h * r)))
    dw, dh = new_shape - new_unpad[0], new_shape - new_unpad[1]
    left, right = int(np.floor(dw / 2)), int(np.ceil(dw / 2))
    top, bottom = int(np.floor(dh / 2)), int(np.ceil(dh / 2))
    resized = cv2.resize(image, new_unpad, interpolation=cv2.INTER_LINEAR)
    padded = cv2.copyMakeBorder(resized, top, bottom, left, right, cv2.BORDER_CONSTANT, value=color)
    return padded, r, (left, top)


def box_iou(box: Sequence[float], boxes: np.ndarray) -> np.ndarray:
    ax1, ay1, ax2, ay2 = box
    bx1 = boxes[:, 0]
    by1 = boxes[:, 1]
    bx2 = boxes[:, 2]
    by2 = boxes[:, 3]
    inter_x1 = np.maximum(ax1, bx1)
    inter_y1 = np.maximum(ay1, by1)
    inter_x2 = np.minimum(ax2, bx2)
    inter_y2 = np.minimum(ay2, by2)
    inter = np.clip(inter_x2 - inter_x1, 0, None) * np.clip(inter_y2 - inter_y1, 0, None)
    area_a = (ax2 - ax1) * (ay2 - ay1)
    area_b = (bx2 - bx1) * (by2 - by1)
    union = area_a + area_b - inter + 1e-7
    return inter / union


def nms_xyxy(boxes: np.ndarray, scores: np.ndarray, max_iou: float) -> List[int]:
    if boxes.size == 0:
        return []
    idxs = scores.argsort()[::-1]
    keep: List[int] = []
    while len(idxs) > 0:
        i = idxs[0]
        keep.append(int(i))
        if len(idxs) == 1:
            break
        rest = boxes[idxs[1:]]
        iou = box_iou(boxes[i], rest)
        idxs = idxs[1:][iou < max_iou]
    return keep


class MegaDetector:
    def __init__(self, model_path, providers: Iterable[str] | None = None, img_size: int = 1280):
        init_t0 = time.perf_counter()
        so = ort.SessionOptions()
        so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        requested = list(providers) if providers else ["CPUExecutionProvider"]
        available = set(ort.get_available_providers())
        filtered = [p for p in requested if p in available]
        if not filtered:
            filtered = ["CPUExecutionProvider"] if "CPUExecutionProvider" in available else requested

        session = None
        try:
            session = ort.InferenceSession(str(model_path), sess_options=so, providers=filtered)
        except Exception:
            if filtered != ["CPUExecutionProvider"] and "CPUExecutionProvider" in available:
                session = ort.InferenceSession(str(model_path), sess_options=so, providers=["CPUExecutionProvider"])
            else:
                raise

        self.session = session
        self.provider_requested = requested
        self.provider_used = session.get_providers()
        self.input_name = session.get_inputs()[0].name
        inp_shape = session.get_inputs()[0].shape
        expected = inp_shape[-1] if inp_shape and isinstance(inp_shape[-1], int) else img_size
        self.img_size = expected
        self.load_ms = (time.perf_counter() - init_t0) * 1000.0

    def infer(
        self,
        image_bgr: np.ndarray,
        conf_thresh: float,
        min_area_frac: float,
        max_iou: float = 0.5,
    ) -> List[Tuple[int, float, float, float, float, float, float]]:
        img, r, (dw, dh) = letterbox(image_bgr, self.img_size)
        rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
        tensor = rgb.transpose(2, 0, 1)[None].astype(np.float32) / 255.0
        out = self.session.run(None, {self.input_name: tensor})[0]
        preds = np.squeeze(out)
        if preds.ndim != 2 or preds.shape[1] < 6:
            return []

        boxes = preds[:, 0:4]
        obj = preds[:, 4:5]
        cls_scores = preds[:, 5:]
        scores = obj * cls_scores
        class_ids = scores.argmax(axis=1)
        confs = scores[np.arange(scores.shape[0]), class_ids]

        keep_mask = confs >= conf_thresh
        boxes, confs, class_ids = boxes[keep_mask], confs[keep_mask], class_ids[keep_mask]
        if boxes.size == 0:
            return []

        xyxy = np.zeros_like(boxes)
        xyxy[:, 0] = boxes[:, 0] - boxes[:, 2] / 2
        xyxy[:, 1] = boxes[:, 1] - boxes[:, 3] / 2
        xyxy[:, 2] = boxes[:, 0] + boxes[:, 2] / 2
        xyxy[:, 3] = boxes[:, 1] + boxes[:, 3] / 2
        xyxy[:, [0, 2]] -= dw
        xyxy[:, [1, 3]] -= dh
        xyxy /= r

        h, w = image_bgr.shape[:2]
        filtered: List[Tuple[int, float, Tuple[float, float, float, float], float]] = []
        for cls, conf, (x1, y1, x2, y2) in zip(class_ids, confs, xyxy):
            x1 = float(max(0.0, min(w - 1.0, x1)))
            x2 = float(max(0.0, min(w - 1.0, x2)))
            y1 = float(max(0.0, min(h - 1.0, y1)))
            y2 = float(max(0.0, min(h - 1.0, y2)))
            if x2 <= x1 or y2 <= y1:
                continue
            area = (x2 - x1) * (y2 - y1)
            if area < min_area_frac * h * w:
                continue
            area_frac = area / (h * w + 1e-9)
            filtered.append((int(cls), float(conf), (x1, y1, x2, y2), float(area_frac)))

        if not filtered:
            return []

        boxes_arr = np.array([f[2] for f in filtered])
        conf_arr = np.array([f[1] for f in filtered])
        keep_idxs = nms_xyxy(boxes_arr, conf_arr, max_iou=max_iou)

        results: List[Tuple[int, float, float, float, float, float, float]] = []
        for i in keep_idxs:
            cls_id, conf, (x1, y1, x2, y2), area_frac = filtered[i]
            x = x1 / w
            y = y1 / h
            bw = (x2 - x1) / w
            bh = (y2 - y1) / h
            results.append((cls_id, conf, x, y, bw, bh, area_frac))
        return results
